# Capability Matrix — OpenCode, star-cliproxy, Cursor ACP, Codex CLI, AGY CLI, Multi-Account Bridge

- **Статус:** Accepted (нормативный артефакт Phase 0)
- **Дата:** 2026-09-22
- **Задача:** TASK-004 (Phase 0 — discovery и архитектурные контракты)
- **Связанные документы:** `TECHNICAL_SPECIFICATION.md` §2.1, §4.2, §5, §6.3, §6.4, §6.5, §6.6; `ROADMAP.md` Phase 0, Phase 5
- **Парные артефакты:** `docs/protocols/capabilities/quota-capability-inventory.json`, `docs/protocols/capabilities/multi-account-routing-contract.json`
- **ADR:** `docs/adr/ADR-0004-multi-account-and-quota-capabilities.md`

## 1. Назначение и правила

1. Матрица фиксирует проверенные в Phase 0 возможности backends и multi-account bridge и служит нормативной доказательной базой для Phase 1-6.
2. Допустимые состояния — только `Supported`, `Unsupported`, `Unknown`. Иные состояния не используются; отсутствие evidence даёт `Unknown`, а не предположение. Решение ADR-0007 от 2026-09-23 является нормативным для topology; capability `starcliproxy.*` остаются `Unknown` до Phase 5R live-spike.
3. `Supported` — capability подтверждена evidence; `Unsupported` — проверенное отсутствие; `Unknown` — не подтверждена и не опровергнута, UI не вправе изображать её доступной.
4. Матрица — snapshot от 2026-09-22. Runtime capability probe обязателен; отсутствие обязательной capability переводит backend в degraded/`UnsupportedVersion`, silent fallback запрещён.
5. Для строк `bridge.*` состояние `Supported` означает, что принятый контракт обязывает bridge реализовать capability и запрещает маршруты без неё; runtime-реализация проверяется в Phase 5.
6. Фиктивные значения запрещены: проценты квот, fork/resume fixtures и observed route не создаются без evidence (§6.3-§6.6).
7. Секреты, credentials и приватные пути в матрицу не включаются. Санитизированные placeholder-значения: `C:\workspace\demo-app`, `user@example.com`.

## 2. Backend: OpenCode serve (`opencode.*`)

| Capability ID | Capability | State | Evidence |
|---|---|---|---|
| `opencode.transport.serve` | Managed `opencode serve` HTTP transport | Supported | ADR-0002 §1-§2: отдельный инстанс на ProviderProfile, loopback, `--port 0` |
| `opencode.session.create` | Создание native session | Supported | `POST /session` (server-api-inventory.json) |
| `opencode.session.stream` | Потоковая выдача turn | Supported | `POST /session/{sessionID}/prompt_async` + `GET /event` (ADR-0002 §3) |
| `opencode.session.cancel` | Отмена execution | Supported | `POST /session/{sessionID}/abort`; terminal outcome только из event stream |
| `opencode.session.resume` | Продолжение существующей session | Supported | Continue того же native ID при неизменном binding в работающем инстансе (ADR-0002 §4); доказанный crash-resume после рестарта сервера не подтверждён |
| `opencode.session.fork` | Fork native session | Supported | `POST /session/{sessionID}/fork` |
| `opencode.session.close` | Close/delete native session | Unknown | Операция отсутствует в inventory 1.18.31; перепроверка `/doc` в Phase 3 |
| `opencode.approvals.reply` | Ответ на approval request | Supported | `POST /permission/{requestID}/reply` |
| `opencode.model.discovery` | Discovery моделей и provider'ов | Supported | `GET /api/model`, `GET /api/provider`, `GET /config/providers` |
| `opencode.model.parameterizedOverrides` | Параметризованные model overrides | Unknown | Variant-поля наблюдаются; синтаксис `<base>[param=value]` для OpenCode не подтверждён |
| `opencode.account.pin` | Pin конкретного account | Unsupported | missingOperations.account.pin: generic server API не даёт pin |
| `opencode.account.observedRoute` | Evidence фактического account/model/variant | Unsupported | missingOperations.account.observedRoute |
| `opencode.account.autoRotation.disable` | Отключение auto-rotation для GUI session | Unsupported | Multi-account plugins в конфигурации отсутствуют; plugin contract не обнаружен |
| `opencode.account.multiAccount` | Автоматический multi-account | Unsupported | Нет pin и observed route; plugin bridge не обнаружен |
| `opencode.quota.api` | Quota API | Unsupported | Endpoint `/quota` отсутствует; доступен только `/stats` локального расхода |
| `opencode.quota.providerReported` | Provider-reported quota fields | Unsupported | Quota API отсутствует |
| `opencode.quota.localCounter` | Локальный счётчик расхода | Supported | `/stats`: локальный расход токенов текущей session |
| `opencode.health.probe` | Health probe | Supported | `GET /api/health`, `GET /global/health` |
| `opencode.customBaseUrl` | Custom provider по baseUrl | Supported | OpenCode-compatible provider contract (§6.2) |
| `opencode.plugins.inventory` | Plugin inventory | Unknown | Fail-closed фильтр: любой plugin с agy/antigravity/codex в имени или описании исключён из managed routes (ТЗ §6.11, FIX-033 F4); API инвентаря не подтверждён |
| `opencode.diagnostic.cliFallback` | Machine-readable CLI fallback | Supported | `opencode export` только как диагностический CLI (ADR-0002 §7) |

## 3. Backend: Cursor ACP (`cursor.*`)

| Capability ID | Capability | State | Evidence |
|---|---|---|---|
| `cursor.transport.acp` | `cursor-agent acp` stdio JSON-RPC 2.0 | Supported | ADR-0003 §1; acp-handshake-response.json |
| `cursor.session.create` | `session/new` с cwd и mcpServers | Supported | acp-session-new-schema.json |
| `cursor.session.stream` | Streaming protocol updates | Unknown | protocol event stream не захвачен в Phase 0 sanitized fixture; требуется Phase 6 probe |
| `cursor.session.cancel` | In-band cancel с terminal evidence | Unknown | in-band cancellation не захвачен в Phase 0 sanitized fixture; требуется Phase 6 probe |
| `cursor.session.resume` | Resume через `loadSession` | Supported | agentCapabilities.loadSession=true |
| `cursor.session.fork` | Native fork | Unknown | Capability не подтверждена; фиктивные fork fixtures запрещены |
| `cursor.approvals.reply` | Permission requests | Supported | acp-capabilities.json capability acp.approvals |
| `cursor.model.discovery` | Model discovery | Supported | acp-model-catalog.json |
| `cursor.model.parameterizedOverrides` | Параметризованные overrides | Supported | Синтаксис `<baseModelId>[context=...,effort=...]` |
| `cursor.account.pin` | Pin конкретного account | Unsupported | ACP не предоставляет account pin |
| `cursor.account.observedRoute` | Evidence фактического account | Unsupported | ACP не сообщает фактический account |
| `cursor.account.autoRotation.disable` | Отключение auto-rotation | Unsupported | Нет plugin/rotation contract |
| `cursor.account.multiAccount` | Multi-account | Unsupported | Credentials остаются вне приложения (ADR-0003 §2) |
| `cursor.quota.api` | Quota API | Unsupported | acp-capabilities.json quota.api=Unsupported |
| `cursor.quota.providerReported` | Provider-reported quota fields | Unsupported | Quota API отсутствует |
| `cursor.quota.localCounter` | Local counter | Unknown | Надёжный estimator не подтверждён в Phase 0 |
| `cursor.health.probe` | Health | Supported | ADR-0003 §8: discovery + handshake + protocol events + turn outcomes |
| `cursor.customBaseUrl` | Custom base URL | Unsupported | Нативный ACP backend не поддерживает baseUrl override |
| `cursor.diagnostic.cliFallback` | Read-only diagnostic CLI | Supported | `cursor-agent --print --mode ask` (ADR-0003 §9) |

## 4. Backend: Codex CLI (`codex.*`)

Codex CLI `0.155.0-alpha.9.2` не является самостоятельным product transport: его запускает `star-cliproxy`. Multi-account обеспечивается изолированными `CODEX_HOME`/proxy instances, а не OpenCode plugins.

| Capability ID | Capability | State | Evidence |
|---|---|---|---|
| `codex.backend.native` | Прямой Codex CLI как штатный product transport | Unsupported | ADR-0007: CLI запускает только `star-cliproxy` provider adapter |
| `codex.integration.viaOpenCode` | Интеграция как OpenCode provider | Unsupported | ADR-0007: Codex через OpenCode запрещён |
| `codex.integration.viaStarCliProxy` | Интеграция через `star-cliproxy` | Unknown | Целевая topology ADR-0007; требуется Phase 5R live-spike |
| `codex.account.multiAccount.profiles` | Multi-account через отдельные `CODEX_HOME` | Unknown | Нормативный механизм выбран; runtime изоляция через proxy ещё не доказана |
| `codex.account.pin` | Pin конкретного account | Unknown | Pin через StarCliProxyAccountBridge + IAccountContextManager (сериализованный CODEX_HOME context, RequiresNewSession); end-to-end proxy evidence не получен |
| `codex.account.observedRoute` | Evidence фактического route | Unknown | StarCliProxyClient сравнивает requested/observed provider/account/model/session и терминально останавливает turn при mismatch (ТЗ §6.4, FIX-033 F1); live evidence не получен |
| `codex.account.autoRotation.disable` | Отключение auto-rotation | Unknown | Plugin bridge не подтверждён |
| `codex.quota.api` | Quota API | Unknown | Отдельный probe квот Codex не проводился; API не подтверждён |
| `codex.quota.providerReported` | Provider-reported quota fields | Unknown | Нет проверенного evidence |
| `codex.quota.localCounter` | Local counter | Unknown | Надёжный estimator не подтверждён в Phase 0 |
| `codex.session.resume` | Resume session | Unknown | §5 snapshot: resume подтверждён для CLI, product contract не зафиксирован |
| `codex.session.fork` | Fork session | Unknown | §5 snapshot: fork подтверждён для CLI, product contract не зафиксирован |

## 5. Backend: AGY CLI (`agy.*`)

AGY CLI `1.1.23` должен запускаться provider adapter'ом `star-cliproxy`; профиль до запуска выбирается через `agy-profile`. Собственный quota API не обнаружен.

| Capability ID | Capability | State | Evidence |
|---|---|---|---|
| `agy.backend.native` | Прямой AGY CLI как штатный product transport | Unsupported | ADR-0007: прямой TASK-032 runner является переходным migration code |
| `agy.integration.viaOpenCode` | Интеграция через OpenCode | Unsupported | ADR-0007: AGY через OpenCode запрещён |
| `agy.integration.viaStarCliProxy` | Интеграция через `star-cliproxy` | Unknown | Целевая topology ADR-0007; требуется Phase 5R live-spike |
| `agy.model.discovery` | Model discovery | Supported | §5: model discovery в AGY CLI 1.1.23 |
| `agy.modes` | plan/accept-edits и effort low/medium/high | Supported | §5 capability snapshot |
| `agy.session.continue` | `--conversation` / `--continue` | Supported | §5 capability snapshot |
| `agy.account.pin` | Pin конкретного account через `agy-profile` | Unknown | Direct TASK-032 bridge удалён из product DI (FIX-033 F2); switch только через StarCliProxyAccountBridge + IAccountContextManager с RequiresNewSession (F5); end-to-end proxy evidence не получен |
| `agy.account.observedRoute` | Evidence фактического route | Unknown | RouteMismatch-терминация покрыта loopback TCP contract tests (FIX-033 F1/F3); live evidence не получен |
| `agy.quota.api` | Quota API | Unsupported | Нет команды запроса квот аккаунта |
| `agy.quota.providerReported` | Provider-reported quota fields | Unsupported | Quota API отсутствует |
| `agy.quota.localCounter` | Local counter | Unknown | Надёжный estimator не подтверждён в Phase 0 |
| `agy.diagnostic.jsonOutput` | JSON/stream-JSON output | Supported | §5 capability snapshot |

## 6. Multi-account bridge (`bridge.*`)

Bridge-строки описывают нормативные требования принятого контракта (Phase 0); реализация проверяется в Phase 5.

| Capability ID | Capability | State | Evidence |
|---|---|---|---|
| `bridge.isolation.providerProfile` | Изоляция на уровне ProviderProfile/инстанса | Supported | ADR-0004; multi-account-routing-contract.json isolationModel |
| `bridge.account.pin` | Pin account/model/variant до отправки prompt | Supported | contract pin.required=true |
| `bridge.account.observedRoute` | Получение фактически использованного route | Supported | contract routeEvidence.observedRoute |
| `bridge.autoRotation.disable` | Auto-rotation отключена для GUI sessions | Supported | contract autoRotation.forGuiManagedSessions=Disabled |
| `bridge.account.discovery` | Явное перечисление доступных account identities | Supported | Контракт `docs/protocols/capabilities/multi-account-routing-contract.json`, bridgeCapabilities.required; Unknown runtime evidence не создаёт выбираемый productive route |
| `bridge.quota.read` | Чтение quota с честным Unknown/Unsupported | Supported | Контракт `docs/protocols/capabilities/multi-account-routing-contract.json`, bridgeCapabilities.required; численные quota не являются обязательным условием pinned ManualOnly, автоматический quota-aware выбор требует доказанных значений |
| `bridge.health.read` | Чтение наблюдаемого health | Supported | Контракт `docs/protocols/capabilities/multi-account-routing-contract.json`, bridgeCapabilities.required; локальные observations отделены от native recovery evidence |
| `bridge.routeMismatch` | Terminating invariant RouteMismatch | Supported | contract routeMismatch.code=RouteMismatch; StarCliProxyRouteEvidence (provider/account/model/session) + терминальный Error клиента; loopback TCP contract tests (FIX-033 F1/F3) |
| `bridge.quota.honestStates` | Честные Unsupported/Unknown без фиктивных значений | Supported | quota-capability-inventory.json normativeRules.fabricatedValuesAllowed=false |

`Supported (контракт)` фиксирует обязанность API/представления и не доказывает runtime
capability каждого backend. Полный pin и observed binding обязательны для automatic
multi-account admission. Отсутствие численного quota не блокирует явно выбранный opaque
ManualOnly route; оно запрещает выдавать Unknown за свободный лимит или выбирать аккаунт
автоматически на основе такого значения. Health `Healthy` без pinned model probe не
доказывает verified native recovery.

## 7. Сводка статусов quota и multi-account

| Backend | account.pin | observedRoute | quota.api | Provider-reported quota | Local counter | Multi-account |
|---|---|---|---|---|---|---|
| OpenCode serve | Unsupported | Unsupported | Unsupported | Unsupported | Supported (`/stats`) | Unsupported |
| Cursor ACP | Unsupported | Unsupported | Unsupported | Unsupported | Unknown | Unsupported |
| Codex via star-cliproxy | Unknown | Unknown | Unknown | Unknown | Unknown | Unknown (`CODEX_HOME` topology требует live-spike) |
| AGY via star-cliproxy | Unknown | Unknown | Unsupported | Unsupported | Unknown | Unknown (`agy-profile` + proxy end-to-end не доказан) |
| Multi-account bridge | Supported (контракт) | Supported (контракт) | не применимо | не применимо | не применимо | Supported (контракт) |

1. `Unknown` в сводке означает отсутствие подтверждающего evidence, а не отказ: UI не изображает capability доступной до runtime probe.

## 8. Evidence index

- `TECHNICAL_SPECIFICATION.md`
- `ROADMAP.md`
- `docs/adr/ADR-0001-runtime-and-cli-baseline.md`
- `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`
- `docs/adr/ADR-0003-cursor-acp-lifecycle.md`
- `docs/adr/ADR-0004-multi-account-and-quota-capabilities.md`
- `docs/adr/ADR-0007-star-cliproxy-codex-agy-boundary.md`
- `docs/protocols/opencode/server-api-inventory.json`
- `docs/protocols/opencode/session-schema.json`
- `docs/protocols/cursor/acp-capabilities.json`
- `docs/protocols/cursor/acp-handshake-response.json`
- `docs/protocols/cursor/acp-model-catalog.json`
- `docs/protocols/cursor/acp-session-new-schema.json`
- `docs/protocols/capabilities/quota-capability-inventory.json`
- `docs/protocols/capabilities/multi-account-routing-contract.json`
