# LLM Work GUI — Normalized Approval Mapping: OpenCode и Cursor

- **Статус:** Accepted (нормативный артефакт Phase 0)
- **Дата:** 2026-09-22
- **Задача:** TASK-006 (Phase 0 — discovery и архитектурные контракты)
- **Нормативные ссылки:** `TECHNICAL_SPECIFICATION.md` §6.9, §6.11, §6.12, §8, §10; `ROADMAP.md` Phase 0, Phase 3, Phase 6
- **Связанные артефакты:** `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`, `docs/adr/ADR-0003-cursor-acp-lifecycle.md`, `docs/protocols/opencode/server-api-inventory.json`, `docs/protocols/cursor/acp-capabilities.json`, `docs/architecture/THREAT_MODEL.md`, `docs/architecture/DATA_FLOW.md`
- **Контрактные тесты:** `tests/LLMWorkGUI.Backends.ContractTests/StateAndApprovalContractTests.cs`

## 1. Назначение и правила

1. Каждый adapter публикует детерминированный mapping `native approval kind → normalized kind → поддерживаемые ответы` (§6.9). Этот документ — нормативная база такого mapping для OpenCode и Cursor.
2. Нормализованный набор закрыт и состоит ровно из семи категорий: `ReadFile`, `WriteFile`, `ShellCommand`, `NetworkTool`, `WorkspaceExpansion`, `HighRiskDestructive`, `UnknownHighRisk`.
3. **Обязательное правило:** любой unknown native kind всегда маппится на `UnknownHighRisk`. Исключений нет ни для одного backend.
4. Mapping выполняется по native kind/категории и path scope, а не по тексту prompt, имени tool или догадке. Одинаковый native вход всегда даёт один и тот же normalized kind.
5. OpenCode и Cursor не считаются автоматически эквивалентными: adapter переводит только доказанно совместимые capabilities, mapping строится раздельно для каждого backend.
6. `auto approve` не включается по умолчанию и не выводится из native config. Persistent approval rule создаётся только на экране Approval Rules с подтверждением и записью в audit.
7. Секреты, credentials и приватные пути пользователя в approval artifacts не попадают. Placeholder-значения: `C:\workspace\demo-app`, `user@example.com`.

## 2. Канонические нормализованные категории

| Normalized kind | Описание | Supported answers | Persistent rule |
|---|---|---|---|
| `ReadFile` | Чтение файлов workspace | `allow once`, `allow for execution`, `deny` | да, через Approval Rules с project/path scope |
| `WriteFile` | Запись, создание или редактирование файлов | `allow once`, `allow for execution`, `deny` | да, через Approval Rules с project/path scope |
| `ShellCommand` | Запуск shell/executable команды | `allow once`, `allow for execution`, `deny` | да, через Approval Rules с operation scope |
| `NetworkTool` | Сетевой доступ, MCP или внешний tool | `allow once`, `allow for execution`, `deny` | да, через Approval Rules с operation scope |
| `WorkspaceExpansion` | Доступ к пути вне закреплённого workspace | `allow once`, `allow for execution`, `deny` | да, только с явным path scope |
| `HighRiskDestructive` | Destructive/high-risk действие (delete, force reset, разрушительная команда) | `allow once`, `deny` | да, через отдельный экран Approval Rules |
| `UnknownHighRisk` | Native kind не распознан или отсутствует | `allow once` (разовый explicit allow), `deny` | нет, постоянные правила запрещены |

1. Для `UnknownHighRisk` `allow for execution` недоступен: разрешение выдаётся только разово и только явным действием пользователя.
2. Ответ `allow for execution` действует только на текущий execution и не создаёт постоянного правила.
3. `HighRiskDestructive` не поддерживает `allow for execution`; массовое одобрение для него запрещено.
4. UI всегда показывает исходный native запрос и нормализованное объяснение рядом.

## 3. Mapping OpenCode

Таблица нормализованных ответов выше описывает общую policy. Конкретный adapter
показывает только подтверждённые им ответы. На 04.10.2026 OpenCode lifecycle/UI
поддерживает `allow once` → native `once` и `deny` → native `reject`.
`allow for execution` недоступен: native `always` действует шире одного execution
и не может подставляться вместо него. Неизвестные response literals отклоняются,
неподтверждённый reply не повторяется автоматически. HTTP 2xx без native JSON `true`
не считается подтверждением ответа.

Категории native permission OpenCode зафиксированы в `docs/adr/ADR-0002-opencode-serve-topology-and-api.md` §5; ответ отправляется через `POST /permission/{requestID}/reply`. Категории в этой таблице — нормативные функциональные классы (prose), а не зафиксированный protocol enum: до фиксации native enum literals из OpenAPI `/doc` конкретного инстанса или живого события любой нераспознанный литерал маппится на `UnknownHighRisk`.

| Native OpenCode kind | Normalized kind | Evidence |
|---|---|---|
| read file | `ReadFile` | ADR-0002 §5; `POST /permission/{requestID}/reply` |
| write/edit file | `WriteFile` | ADR-0002 §5; `POST /permission/{requestID}/reply` |
| shell command | `ShellCommand` | ADR-0002 §5; `POST /permission/{requestID}/reply` |
| network/MCP/tool | `NetworkTool` | ADR-0002 §5; `POST /permission/{requestID}/reply` |
| расширение workspace | `WorkspaceExpansion` | ADR-0002 §5; path scope вне workspace |
| destructive/high-risk | `HighRiskDestructive` | ADR-0002 §5; `POST /permission/{requestID}/reply` |
| любой неизвестный native kind | `UnknownHighRisk` | ТЗ §6.9; ADR-0002 §5 |

1. Native response literals версионно-зависимы: adapter определяет их capability probe и не пересылает неизвестные значения.
2. Если запрос read/write file адресован пути вне закреплённого workspace, normalized kind — `WorkspaceExpansion`, независимо от базового kind.
3. Любое значение native kind, отсутствующее в таблице, маппится на `UnknownHighRisk` без исключений.

## 4. Mapping Cursor ACP

Permission requests Cursor приходят как интерактивные протокольные requests (capability `acp.approvals`, `docs/protocols/cursor/acp-capabilities.json`). Per-kind mapping Cursor не подтверждён: sanitized permission-request fixture в Phase 0 не захвачена, поэтому предварительная таблица ACP tool-kind (`read`, `search`, `think`, `edit`, `move`, `execute`, `fetch`, `delete`, `other`) снята и не является нормативной. До захвата fixture действует строгое правило:

| Native Cursor kind | Normalized kind | Evidence / constraint |
|---|---|---|
| любой native запрос | `UnknownHighRisk` | sanitized permission-request fixture не захвачена в Phase 0; нормативное правило §1.3 |

1. До захвата sanitized permission-request fixture (Phase 6) любой входящий approval-запрос Cursor нормализуется строго как `UnknownHighRisk`; per-kind классификация не предполагается и не выводится из документации ACP.
2. Persistent rules и `allow for execution` для Cursor approvals запрещены до подтверждённого per-kind mapping; допустимы только разовый explicit `allow once` либо `deny`.
3. Режимы `plan`, `ask` и `agent` не дают read-only writer-lock exemption без доказанного в runtime mode payload: mode payload в handshake response отсутствует, поэтому применяется консервативное правило ADR-0003 §10.
4. `--force`/`--yolo` и любые скрытые approval-обходы не отменяют mapping: каждый request проходит нормализацию.

## 5. Ответы, persistent rules и audit

1. Поддерживаемые ответы: `allow once`, `allow for execution`, `deny`; для `HighRiskDestructive` и `UnknownHighRisk` — только `allow once`/разовый explicit allow либо `deny`.
2. Persistent approval rule содержит backend/provider scope, project/path scope, operation, срок действия и автора.
3. Создание, изменение и удаление правил выполняется только на экране Approval Rules и записывается в audit (без секретов).
4. `UnknownHighRisk` никогда не попадает под постоянные правила и не одобряется автоматически.
5. Любой ответ на approval фиксируется с correlation IDs (`sessionId`, `executionId`, `clientRequestId`) и redacted payload.

## 6. Контрактные тесты

Инварианты этого документа проверяются классом `tests/LLMWorkGUI.Backends.ContractTests/StateAndApprovalContractTests.cs`:

- наличие ровно семи канонических категорий и допустимых ответов;
- детерминированный mapping OpenCode на эти категории на синтетических входах; actual native fixture подтверждает только `edit` и `reject`, а не семь native категорий;
- строгий fallback любого входящего Cursor approval на `UnknownHighRisk` до захвата sanitized permission-request fixture в Phase 6;
- обязательный fallback любого неизвестного native kind на `UnknownHighRisk`;
- запрет persistent rules для `UnknownHighRisk` и запрет `allow for execution` для `HighRiskDestructive`.

## 7. Evidence index

- `TECHNICAL_SPECIFICATION.md`
- `ROADMAP.md`
- `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`
- `docs/adr/ADR-0003-cursor-acp-lifecycle.md`
- `docs/protocols/opencode/server-api-inventory.json`
- `docs/protocols/cursor/acp-capabilities.json`
- `docs/architecture/THREAT_MODEL.md`
- `docs/architecture/DATA_FLOW.md`
- `docs/architecture/STATE_TRANSITION_TABLES.md`
