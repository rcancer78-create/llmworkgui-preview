# ADR-0003: Cursor ACP lifecycle

- **Статус:** Accepted
- **Дата:** 2026-09-22
- **Задача:** TASK-003 (Phase 0 — discovery и архитектурные контракты)
- **Связанные документы:** `TECHNICAL_SPECIFICATION.md` §2.1, §4.2, §5, §6.12; `ROADMAP.md` Phase 0, Phase 6
- **Fixtures:** `docs/protocols/cursor/acp-handshake-response.json`, `docs/protocols/cursor/acp-session-new-schema.json`, `docs/protocols/cursor/acp-model-catalog.json`, `docs/protocols/cursor/acp-capabilities.json`

## Контекст

ТЗ §2.1 и §6.12 закрепляют Cursor как отдельный нативный backend: запуск `cursor-agent acp` и управление по ACP через `stdio`, без проксирования через OpenCode. Phase 0 обязан провести handshake, подготовить protocol fixtures без credentials и принять ADR о lifecycle.

В окружении проверен Cursor Agent `2026.09.15-d2fe57e`: ACP initialize завершился ответом с `protocolVersion: 1`, `agentCapabilities` (`loadSession`, MCP http/sse, prompt image, `sessionCapabilities.list`) и `authMethods` (`cursor_login`). Вызов `session/new` требует `cwd` и `mcpServers`. Результаты санитизированы и зафиксированы в fixtures; тесты `CursorAcpProtocolFixtureTests` валидируют структуру, обязательные поля и отсутствие секретов и приватных путей.

Ограничения Phase 0: продуктовый adapter не пишется (Phase 6); Cursor credentials не читаются; quota API не подтверждён; ACP остаётся единственным основным transport для Cursor, CLI print-mode — только диагностическое исключение.

## Решение

### 1. Transport и framing

1. Cursor запускается как managed-процесс `cursor-agent acp`; обмен идёт по `stdio` кадрами JSON-RPC 2.0 (newline-delimited), `protocolVersion: 1`.
2. Процесс принадлежит Process Supervisor (§4.2, §6.8): запуск без видимой консоли, неблокирующее чтение stdout/stderr, startup timeout, liveness, restart/orphan reconciliation. Heartbeat и любой иной трафик в stdin ACP-канала не пишутся: это разрушило бы JSON-RPC поток.
3. stdout зарезервирован под протокол; диагностический вывод читается из stderr. Парсинг TUI/ANSI запрещён (§6.12, §6.11).
4. Сетевой транспорт не используется: ACP-канал локален и изолирован per instance.

### 2. Handshake и capabilities

1. Adapter однократно отправляет `initialize` с `clientInfo` (`LLMWorkGUI`) и клиентскими `capabilities`, после чего фиксирует ответ как capability evidence (`acp-handshake-response.json`).
2. `protocolVersion` — JSON-число `1` (не строка). Сравнение выполняется численно; `"1"` или иное значение означает `UnsupportedVersion`, а не попытку продолжить.
3. Обязательные для продукта capability: `loadSession`, `promptCapabilities.image`, `sessionCapabilities.list`, MCP `http`/`sse`; отсутствие обязательной capability переводит backend в degraded/`UnsupportedVersion`. Silent fallback запрещён.
4. `authMethods` (`cursor_login`) — только описательная информация для UI; приложение не извлекает и не хранит Cursor credentials (§6.12).

### 3. Sessions

1. Сессия создаётся вызовом `session/new` с обязательными `cwd` (абсолютный путь checkout проекта) и `mcpServers` (массив; пустой массив валиден). Схема — `acp-session-new-schema.json`.
2. Native session ID сохраняется и отображается; workspace binding неизменяем в рамках сессии. Перепривязка выполняется только новой сессией.
3. `loadSession=true` разрешает `session/load` для восстановления persisted native session; `sessionCapabilities.list` используется для discovery.
4. Падение ACP переводит session в `orphaned`/`failed`; новый turn вслепую не запускается (§6.12), выполняется reconciliation по §6.7.

### 4. Streaming и approvals

1. Streaming updates приходят протокольными событиями по stdio; adapter нормализует их в события Execution. Backpressure, bounded buffer и запрет молчаливой потери terminal events — по §6.8.
2. Permission requests агента отображаются пользователю как normalized approvals. До захвата sanitized permission-request fixture (Phase 6) per-kind mapping Cursor не подтверждён: любой входящий approval нормализуется строго как `UnknownHighRisk`, persistent rules и `allow for execution` запрещены. Режимы `plan`, `ask` и `agent` не дают read-only writer-lock exemption без доказанного в runtime mode payload.
3. `auto approve` по умолчанию выключен; неизвестный native kind никогда не попадает под постоянные правила, OpenCode permissions и Cursor permissions не считаются эквивалентными (§6.9).

### 5. Modes

1. `plan` — read-only планирование; `ask` — read-only Q&A; default write mode (`agent`) — исполнение с изменениями workspace.
2. Доступность mode подтверждается discovery; неподтверждённый mode имеет state `Unknown` и недоступен для отправки.
3. Write mode требует checkout writer lock (см. §10). Режимы `plan` и `ask` не дают writer-lock exemption без доказанного в runtime mode payload: mode payload в handshake response отсутствует, поэтому применяется консервативное правило §10.1.

### 6. Cancellation

1. Cancel или timeout переводит Execution в `Cancelling`.
2. Adapter отправляет ACP cancellation in-band и ожидает terminal evidence из протокольных событий; факт отправки cancellation terminal confirmation не является.
3. Если terminal evidence не получен в bounded-окно, допускается прерывание stdio и завершение только управляемого дерева процессов силами Process Supervisor (§6.8) с последующей reconciliation.
4. Итог reconciliation — `Cancelled` либо `Ambiguous`; автоматический повтор prompt запрещён, user cancel/deny/`Ambiguous` не расходуют workflow retry budget (§6.7, §6.9).

### 7. Parameterized model overrides

1. Каталог моделей получается отдельным discovery-вызовом и фиксируется как `acp-model-catalog.json`.
2. Базовый model ID и набор overrides хранятся раздельно; на провод передаётся комбинированная форма `<baseModelId>[<parameter>=<value>,...]` (например `[context=1m,effort=high]`).
3. Отправляются только параметры и значения, подтверждённые per-model discovery. Параметр со state `Unknown` (например `fast`) в UI недоступен для отправки и не изображается `Supported` (§6.12, Phase 6 exit criteria).

### 8. Quota policy

1. Cursor Agent/ACP не раскрывает quota API; соответствующие строки Dashboard имеют честный state `Unsupported` либо `Unknown`.
2. Quota не выводится из косвенных признаков и не подменяется оценками без явной пометки; фиктивные значения запрещены (§6.6, §6.12).
3. Health Cursor строится из executable discovery, ACP handshake, protocol events и turn outcomes, а не из quota-данных.

### 9. DiagnosticCliFallback

1. `cursor-agent --print --mode ask` допускается только как read-only диагностический fallback для операций, отсутствующих в ACP, с предпочтением machine-readable output.
2. Такой execution явно помечается `DiagnosticCliFallback`, виден пользователю, не продолжает ACP session и не участвует в routing; результат проходит redaction до записи и отображения.
3. CLI fallback не является скрытой заменой ACP; недоступность обязательной ACP-операции даёт degraded/`UnsupportedVersion`, но не silent fallback (§6.11, §6.12).

### 10. Concurrency и locks

1. Checkout writer lock берёт любой execution, для которого read-only не доказан mode/capabilities: write mode `agent`, edit/legacy entrypoint, shell/write-capable execution (§6.1).
2. `plan` и `ask`, read-only природа которых подтверждена, могут выполняться параллельно друг с другом и с writer'ом другого checkout.
3. В рамках одной native session одновременно исполняется не более одного turn; параллельные сессии используют раздельные ACP-процессы/каналы.
4. Checkout writer lock освобождается только по подтверждённому terminal outcome, при котором execution не находится в состоянии `Orphaned` или `Ambiguous`. Cancel-запрос сам по себе, без подтверждённого terminal evidence, lock не снимает (ТЗ §6.1).

### 11. Process Supervisor и reconciliation

1. Жизненный цикл ACP-процесса (запуск, liveness, рестарт, завершение дерева процессов) принадлежит Process Supervisor; UI процессы не запускает (§4.2).
2. После рестарта/падения reconciliation даёт `Reattached`, `Orphaned`, `Ambiguous` или `BackendMissing`; prompt повторно не отправляется (§6.7).

## Последствия

- Phase 6 adapter строится поверх ACP stdio; CLI print-mode остаётся диагностическим исключением.
- Fixtures — регрессионная и контрактная база (`CursorAcpProtocolFixtureTests`); runtime capability probe обязателен, snapshot версии — evidence, а не вечный контракт.
- Modes и overrides со state `Unknown` недоступны в UI для отправки; Dashboard Cursor честно показывает `Unsupported/Unknown` по quota.
- Cancellation требует watchdog-подтверждения и reconciliation, а не доверия к ack.

## Риски и ограничения

- **Версионный дрейф ACP:** snapshot `2026.09.15-d2fe57e` может устареть; обязателен capability probe и `UnsupportedVersion` при отсутствии обязательной операции.
- **Тип `protocolVersion`:** частая ошибка интеграций — сравнение с `"1"` (строкой); тест фиксирует числовой тип.
- **Отсутствие quota API:** UI не может показать лимиты Cursor; строки остаются `Unsupported/Unknown`, это документированное ограничение, а не blocker.
- **Падение ACP в середине turn:** возможен только `Orphaned`/`Ambiguous`; автоматический повтор prompt запрещён.
- **Approvals mapping:** у Cursor нет автоматической эквивалентности permissions OpenCode; полный normalized mapping уточняется в Phase 6 на основе capability probe.

## Альтернативы

- **CLI print-mode как основной transport** — отклонено: §6.12 допускает CLI только как видимый диагностический fallback.
- **Проведение Cursor через OpenCode** — отклонено: §2.1 требует отдельного нативного backend.
- **Хранение overrides внутри единой строки model ID** — отклонено: базовый ID и параметры хранятся раздельно для переиспользования, audit и корректной деградации Unknown-параметров.
- **Вывод quota из косвенных сигналов** — запрещено §6.12; такие данные не являются доказательством.
- **Постоянное использование `--force`/`--yolo`** — запрещено: только по явно выбранной пользователем policy (§6.12).
