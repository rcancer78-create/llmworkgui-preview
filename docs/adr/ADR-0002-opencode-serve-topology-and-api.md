# ADR-0002: OpenCode serve — топология и API

- **Статус:** Accepted
- **Дата:** 2026-09-22
- **Задача:** TASK-002 (Phase 0 — discovery и архитектурные контракты)
- **Связанные документы:** `TECHNICAL_SPECIFICATION.md` §2.1, §4.2, §5, §6.4, §6.7–§6.11; `ROADMAP.md` Phase 0
- **Fixtures:** `docs/protocols/opencode/session-schema.json`, `docs/protocols/opencode/session-export-sample.json`, `docs/protocols/opencode/server-api-inventory.json`, `docs/protocols/opencode/event-stream-sample.jsonl`

## Контекст

ТЗ §6.11 фиксирует управляемый локальный `opencode serve` как основной transport и запрещает менять его без отдельного change proposal. Phase 0 обязан исследовать server API, event stream, sessions, approvals и cancellation, принять ADR о топологии и списке отсутствующих операций, а также подготовить protocol fixtures без credentials.

В окружении проверен OpenCode `1.18.31`: headless `serve` на loopback, OpenAPI 3.1 в `/doc`, SSE в `/event`, создание/список сессий, fork, abort, permissions и `opencode export`. Результаты probe санитизированы и зафиксированы в fixtures; тесты `OpenCodeProtocolFixtureTests` валидируют их структуру и отсутствие секретов.

Ограничения Phase 0: ACP не заменяет serve-first transport; generic server API не даёт plugin-specific account/quota контрактов (§6.4); undocumented credential files не читаются; парсинг TUI/ANSI запрещён.

## Решение

### 1. Топология managed-инстансов

1. Default — отдельный managed инстанс `opencode serve` на каждый provider profile; shared server запрещён, пока отдельный spike не докажет изоляцию credentials и невозможность исполнить pinned session account A через account B.
2. Hostname — только `127.0.0.1`; внешняя публикация не выполняется по умолчанию. Порт — `--port 0` (свободный порт назначает ОС); фактический порт Process Supervisor получает из machine-readable startup line сервера и хранит в записи BackendInstance. Парсинг TUI для этого не используется.
3. Если plugin contract доказывает безопасный pin внутри одного инстанса, route switch не перезапускает server. Если pin/isolation не доказаны, граница инстанса сужается до account.
4. Lifecycle целиком принадлежит Process Supervisor (§6.8): запуск без видимой консоли, неблокирующее чтение stdout/stderr, startup timeout, liveness и restart policy, orphan reconciliation. Turn hard timeout не завершает живой server; heartbeat в stdin протокола не пишется.
5. Завершение инстанса — `/instance/dispose` (если жив) плюс подтверждённое завершение только своего дерева процессов.
6. Опционально включается HTTP basic auth (`OPENCODE_SERVER_PASSWORD`, username `OPENCODE_SERVER_USERNAME`); значения хранятся только в Windows secret storage и не попадают в command line, логи или fixtures. Default loopback-инстанс работает без пароля (риск — см. раздел «Риски»).

### 2. HTTP API

- Base URL: `http://127.0.0.1:{port}`. Спецификация OpenAPI 3.1 доступна в `/doc`; фактическая схема запросов/ответов резолвится из `/doc` конкретного инстанса при capability probe.
- Каталог обнаруженных endpoints: `docs/protocols/opencode/server-api-inventory.json` (19 операций, snapshot 1.18.31). Это evidence, а не вечный контракт.
- Ключевые группы:
  - sessions: `GET/POST /session`, `GET /session/{sessionID}`, `POST /session/{sessionID}/fork`, `POST /session/{sessionID}/abort`, `GET /session/{sessionID}/diff`;
  - messages: `GET /session/{sessionID}/message`, `POST /session/{sessionID}/message`, `POST /session/{sessionID}/prompt_async`;
  - approvals: `POST /permission/{requestID}/reply`; вопросы модели: `POST /question/{requestID}/reply`;
  - providers/models: `GET /config/providers`, `GET /api/provider`, `GET /api/model`;
  - health: `GET /api/health`, `GET /global/health`;
  - lifecycle: `POST /instance/dispose`.
- Streaming turn выполняется через `prompt_async`; нормализованный terminal outcome берётся только из event stream, а не из HTTP-ответа отправки prompt.

### 3. SSE event stream

1. В discovery snapshot 1.18.31 зафиксированы `/event` (поток всех сессий инстанса) и `/api/session/{sessionID}/event` (scoped-поток); content type `text/event-stream`. Текущий adapter использует подтверждённый workspace capture от 2026-10-02: общий `/event` с проверкой session ID на стороне клиента (`workspace-sse-sample-20261002.json`). Исторический inventory сохраняется как исходное evidence и не задаёт другой URL текущему transport.
2. Envelope каждого data payload — JSON `{ "type": string, "properties": object }`; первый event — `server.connected`. Типы и формы зафиксированы в `event-stream-sample.jsonl` (части `step-start`, `text`, `tool`, `step-finish`).
3. Поток читается непрерывно. После redaction сырые события немедленно spool'ятся в run directory; UI получает bounded in-memory window. При невозможности записи и переполнении буфера execution переходит в terminal state `Failed` с причиной `BufferOverflow` (отдельного состояния `BufferOverflow` нет — закрытый набор состояний ТЗ §6.7 сохранён); terminal events не отбрасываются молча (§6.8).
4. Тишина модели — наблюдение, не автоматический failure. Terminal success/failure определяется только нормализованным terminal event. Malformed event относится к health-классу `malformed protocol/event` и не роняет приложение.
5. Reconnect выполняется с backoff и jitter; после reconnect adapter сверяет состояние сессии discovery-вызовом и не отправляет prompt повторно. Рестарт сервера в середине turn приводит к reconciliation (§6.7).
6. Fixture хранит payload без SSE-рамки `data: `; снятие рамки — ответственность adapter.

### 4. Sessions

- `POST /session` создаёт native session; `Starting → Active` возможен только после подтверждённого native session ID (`ses_...`) и observed binding.
- `Continue` использует тот же native ID и неизменный immutable binding (§6.7).
- `Fork` показывается только при подтверждённой capability `/session/{sessionID}/fork`; иначе UI даёт `New session` с ancestry, но не ложный fork.
- Отдельная операция close/delete/reset в обнаруженный inventory не входит. По умолчанию Reset реализуется как новая native session с сохранением истории; Phase 3 обязан перепроверить `/doc` перед тем, как опираться на delete/close.
- `opencode export <sessionID>` используется только как диагностический CLI (см. §7); sanitized форма экспорта — `session-export-sample.json`.
- Схема Session — `session-schema.json`: `id`, `slug`, `projectID`, `directory`, `summary`, `cost`, `tokens`, `title`, `agent`, `model`, `version`, `time`, `permission`. Поле `model` — requested descriptor; доказательством фактически использованного account/model/variant оно не является.

### 5. Permissions → normalized approvals

| Native класс запроса | Normalized kind | Поддерживаемые ответы |
|---|---|---|
| read file | `ReadFile` | allow once / deny |
| write/edit file | `WriteFile` | allow once / deny |
| shell command | `ShellCommand` | allow once / deny |
| network/MCP/tool | `NetworkTool` | allow once / deny |
| расширение workspace | `WorkspaceExpansion` | allow once / deny |
| destructive/high-risk | `HighRiskDestructive` | allow once / deny |
| неизвестный native kind | `UnknownHighRisk` | разовый explicit allow / deny |

1. Ответ отправляется через `POST /permission/{requestID}/reply`. Native response literals версионно-зависимы: adapter определяет их capability probe и не пересылает неизвестные значения.
2. Текущий OpenCode lifecycle/UI отправляет только native `once` и `reject`, после проверки capability и привязки запроса к сессии. Native `always` не ограничен текущим execution, поэтому не является реализацией `allow for execution` и отклоняется lifecycle. Наличие literal в низкоуровневом client API не означает доступность в UI. Постоянные правила создаются исключительно на экране Approval Rules с backend/provider scope, project/path scope, сроком действия и audit (§6.9); они не расширяют допустимые native replies.
3. Неизвестный native kind никогда не попадает под постоянные правила; `auto approve` не включается по умолчанию. OpenCode config и Cursor permissions не считаются автоматически эквивалентными.
4. Каждый adapter публикует mapping `native approval kind → normalized kind → поддерживаемые ответы`; mapping выше обязателен к покрытию capability-тестами Phase 3.

### 6. Cancellation

1. Пользовательский cancel или timeout переводит Execution в `Cancelling`.
2. Adapter выполняет graceful `POST /session/{sessionID}/abort`; HTTP-ack abort не считается terminal confirmation.
3. Execution Watchdog ожидает terminal evidence в event stream: подтверждённый terminal event → `Cancelled`; session возвращается в `Idle` только при подтверждённой жизнеспособности backend, иначе становится `Ambiguous` (§6.7).
4. Если terminal evidence не получен в bounded-окно, допускается bounded forced termination только соответствующего execution/process tree с последующей reconciliation; посторонние процессы и живой server не завершаются.
5. User cancel, approval deny и `Ambiguous` не увеличивают circuit breaker и не расходуют workflow retry budget; автоматический повтор prompt запрещён.

### 7. Отсутствующие операции и границы DiagnosticCliFallback

Обнаруженные ограничения generic server API (детали — `missingOperations` в `server-api-inventory.json`):

- нет операции pin конкретного account и управления auto-rotation plugin;
- нет endpoint, возвращающего observed account/model/variant;
- нет generic quota API;
- нет подтверждённой операции close/delete session;
- `/session/{sessionID}/abort` не даёт terminal confirmation (только event stream);
- рестарт сервера не восстанавливает in-flight turn.

Следствия и границы:

1. Multi-account/observed-route/quota реализуются только через plugin-specific bridge (§6.4). Plugin без pin и evidence остаётся одним opaque route и не участвует в automatic multi-account режимах.
2. `DiagnosticCliFallback` разрешён только для операций, отсутствующих в server API (пример — `opencode export` для sanitized session diagnostics), и только при machine-readable output. Запрещены парсинг TUI/ANSI и использование fallback как скрытой замены serve.
3. Такой execution явно помечается `DiagnosticCliFallback`, виден пользователю, не продолжает обычную server session и не участвует в routing; результат проходит redaction до записи и отображения.
4. Отсутствие обязательной операции переводит backend в `UnsupportedVersion`/degraded state, но не допускает silent fallback.

## Последствия

- Phase 3 adapter строится поверх HTTP REST + SSE; CLI остаётся диагностическим исключением.
- `/doc` конкретного инстанса — источник runtime-схем; fixtures — регрессионная и контрактная база для нормализации.
- Топология «инстанс на provider profile» может быть сужена до account по результатам Phase 5 plugin contract.
- Cancellation и restart требуют обязательного watchdog-подтверждения через event stream и reconciliation-исходов.

## Риски и ограничения

- **Версионный дрейф:** публичная документация OpenCode отслеживает более новые версии, чем 1.18.31 (в частности, путь ответа на permission request в dev-документации отличается). Inventory — snapshot, поэтому обязателен runtime capability probe и `UnsupportedVersion` при отсутствии обязательной операции.
- **Loopback без auth по умолчанию:** любой локальный процесс может обратиться к незащищённому инстансу. Митигация: опциональный basic auth, короткоживущий инстанс, отсутствие внешней публикации; окончательное решение — Phase 2/4.
- **Multi-account ограничен generic API:** без plugin bridge невозможно гарантировать pin/observed route/quota; Phase 5 не может обещать automatic multi-account routing для таких plugin.
- **Стоимость `--port 0`:** порт определяется из startup line; разрешён только этот machine-readable startup-контракт, не TUI.
- **Session close не подтверждён:** до перепроверки `/doc` в Phase 3 reset реализуется как `New session`, что является документированным ограничением, а не assumption.

## Альтернативы

- **Единый shared server на все profiles** — отклонено до spike, доказывающего credential isolation и невозможность cross-account исполнения.
- **ACP как primary transport** — отклонено: §6.11 требует отдельного change proposal.
- **CLI-first / парсинг TUI** — отклонено: нет machine-readable streaming event, парсинг ANSI запрещён ТЗ.
- **Фиксированный порт** — отклонено: конфликты и port squatting; используется `--port 0`.
