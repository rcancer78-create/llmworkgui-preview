# ADR-0004: Multi-account и quota capabilities

- **Статус:** Accepted
- **Дата:** 2026-09-22
- **Задача:** TASK-004 (Phase 0 — discovery и архитектурные контракты)
- **Связанные документы:** `TECHNICAL_SPECIFICATION.md` §2.1, §4.2, §5, §6.3, §6.4, §6.5, §6.6; `ROADMAP.md` Phase 0, Phase 5
- **Артефакты:** `docs/protocols/capabilities/capability-matrix.md`, `docs/protocols/capabilities/quota-capability-inventory.json`, `docs/protocols/capabilities/multi-account-routing-contract.json`

## Контекст

Исторически ADR был принят для multi-account bridge вокруг OpenCode. Решением от 2026-09-23 транспортная часть уточнена ADR-0007: Codex/AGY исключены из OpenCode и переходят на `star-cliproxy`; доменные требования discovery, pin, observed route, `requestedRoute`/`observedRoute`, eligibility и честных quota states сохраняются.

Фактическая проверка окружения в рамках TASK-004 показала:

- конфигурация OpenCode `1.18.31` не содержит внешних multi-account plugins (`plugin` не задан, `disabled_providers: []`); подключены MCP `windows-mcp` и custom provider по baseUrl;
- credentials существуют как отдельные профили (`OpenCode Go api`, `plusvibeapi api`); их значения в Phase 0 не читались;
- отдельные профили `.codex_*` в окружении присутствуют, то есть multi-account Codex может обеспечиваться изоляцией профилей/инстансов;
- Quota API отсутствует: OpenCode serve не имеет endpoint `/quota` (только `/stats` локального расхода токенов текущей сессии), Cursor ACP не раскрывает quota API (ADR-0003 §8), AGY CLI не имеет команды запроса квот аккаунта; quota API Codex не подтверждён;
- generic server API OpenCode не предоставляет операций account pin и observed route (ADR-0002 §7, `server-api-inventory.json` → `missingOperations`).

Следствие: обещать automatic multi-account routing на generic API или на скрытых сторонних плагинах нельзя — pin и фактический route невозможно доказать.

## Решение

### 1. Capability matrix как нормативный артефакт

1. `docs/protocols/capabilities/capability-matrix.md` фиксирует для OpenCode serve, Cursor ACP, Codex CLI, AGY CLI и multi-account bridge только состояния `Supported`, `Unsupported`, `Unknown` с evidence-ссылками.
2. `Unknown` — полноценное состояние, а не разрешение предположить поддержку. UI показывает capability как недоступную; runtime capability probe при старте adapter обязателен, отсутствие обязательной capability даёт degraded/`UnsupportedVersion`, silent fallback запрещён.
3. Матрица — snapshot от 2026-09-22, а не вечный контракт; она не создаёт фиктивные fork/resume fixtures и не приписывает backends несуществующие операции.
4. Фиктивные проценты квот, фиктивные `observedRoute` и скрытые оценки запрещены на уровне контракта.

### 2. Multi-account через изолированные ProviderProfiles/инстансы

1. Изоляция аккаунтов обеспечивается на уровне управляемых ProviderProfiles и изолированных инстансов OpenCode/Cursor, а не через скрытые сторонние плагины.
2. Один managed backend-инстанс обслуживает один ProviderProfile; shared server для нескольких аккаунтов запрещён, пока отдельный spike не докажет credential isolation и невозможность исполнить session account A под account B (ADR-0002 §1).
3. Если pin/isolation внутри одного инстанса не доказаны, граница инстанса сужается до account. Provider, для которого доказательства отсутствуют, отображается как один непрозрачный route и не участвует в автоматических multi-account режимах.
4. Multi-account Codex/AGY или через отдельные профили (`.codex_*`), или через plugin-specific bridge, только при выполнении контракта §6.4. Plugin без pin/observed route не допускается в automatic-режимы.
5. Credentials остаются у OpenCode/plugin либо в Windows secret storage; приложение не извлекает и не показывает OAuth refresh tokens и не читает undocumented credential files.

### 3. Pin и отключение auto-rotation

1. Pin конкретного account/model/variant выполняется до отправки prompt и должен быть подтверждён bridge'ем. Тихое отклонение pin — нарушение контракта (`PinFailure`), а не повод продолжить.
2. Plugin auto-rotation для GUI-managed session отключена; plugin не вправе изменить account/model/variant после pin.
3. Binding — кортеж `(backend, providerProfile, account, model, reasoning, speed, mode)`. После подтверждения native session binding неизменяем; любое изменение создаёт новую session с ancestry `RouteChange` и видимой discontinuity.
4. Источник выбора binding сохраняется и показывается пользователю: явный выбор пользователя → роль workflow → default policy проекта → глобальная default policy.

### 4. `requestedRoute` vs `observedRoute` и RouteMismatch

1. Для каждого execution сохраняются `requestedRoute` и `observedRoute`; сравнение обязательно по всем binding-полям.
2. `observedRoute` берётся только из backend/plugin evidence. Подстановка `requestedRoute` вместо `observedRoute` запрещена (`treatRequestedAsObservedAllowed: false`).
3. Любое несовпадение (или отсутствие observed route там, где режим требует evidence) завершает execution как `RouteMismatch`: binding не продолжается, execution не считается success, автоматический failover и повтор prompt запрещены, evidence сохраняется и показывается пользователю.
4. `RouteMismatch` не расходует workflow retry budget и не маскируется под сетевую ошибку.
5. Непрозрачный route допустим только в `ManualOnly`, явно помечается `OpaqueRoute`, не считается verified route и требует подтверждения пользователя.

### 5. Политика квот

1. Dashboard обязан содержать строку для каждого подключённого account/model family, даже если provider не раскрывает квоту.
2. Состояния значений: `ExactProviderReported`, `PluginReported`, `LocallyCalculated`, `Estimated`, `Stale`, `Unsupported`, `Unknown`, `Error`. `Unsupported/Unknown/Stale/Error` не имеют числового значения и никогда не изображаются как пустая или полная шкала.
3. Выдуманные проценты и скрытые оценки запрещены. Для provider без quota API допускается ручной лимит или локальный usage counter, но он явно помечается как локальный/оценочный (`LocallyCalculated`/`Estimated`) и участвует в scoring только после явного opt-in пользователя.
4. Числовые hard reserve и automatic quota scoring используют только fresh `ExactProviderReported`/`PluginReported` snapshots (default TTL 5 минут, настраивается per source). `QuotaFirst` и `Balanced` по умолчанию отклоняют route без fresh trusted quota; `Pinned`, `ManualOnly` и `PriorityFirst` могут использовать явно выбранный route с предупреждением о недостоверной квоте.
5. Отсутствующий bucket не равен нулю; Dashboard и Router ссылаются на один `quotaSnapshotId`.

### 6. Routing Engine gates и режимы

1. Routing Engine — единственный компонент, выбирающий account/route для GUI-managed session; решение сохраняется с human-readable explanation (выбранный route, источник policy, отклонённые кандидаты и gates, snapshot ID, health evidence, tie-break).
2. Eligibility gates применяются до scoring: route enabled; authentication valid; model доступна account; required reasoning/speed/mode поддерживаются; route не quarantined; quota gate по mode-specific правилам; concurrency slot доступен; data classification проекта не превышает `maxDataClass` route.
3. Поддерживаемые режимы: `Pinned`, `SessionSticky`, `QuotaFirst`, `PriorityFirst`, `Balanced`, `ManualOnly`. Структура режимов, флаги auto-failover и требования к observed route зафиксированы в `multi-account-routing-contract.json`.
4. `Pinned` и `ManualOnly` никогда не выполняют auto-failover. Automatic-режимы могут сменить eligible account только до первого prompt; после подтверждения session или возможной доставки prompt автоматическая пересылка запрещена, а `SessionSticky` при потере eligibility останавливает следующий turn и предлагает replacement session.
5. Failover не меняет backend, provider profile, model, reasoning, speed или mode. `Ambiguous` execution никогда не пересылается автоматически.

## Последствия

- Phase 5 строит bridge и Routing Engine поверх `multi-account-routing-contract.json`; условия `RouteMismatch`, pin и отключение auto-rotation — обязательная часть acceptance.
- Dashboard квот реализуется строго по `quota-capability-inventory.json`: честные `Unsupported/Unknown` вместо фиктивных процентов.
- Capability matrix становится входным контрактом для capability probe Phase 1-6; `Unknown`-строки блокируют только зависящие от них режимы, а не всё приложение.
- Скрытые сторонние multi-account plugins не используются; каждое расширение bridge требует нового evidence и обновления ADR/контракта.

## Риски и ограничения

- **Отсутствие pin/observed route у generic API:** automatic multi-account для таких provider невозможен; это документированное ограничение, а не баг реализации.
- **Quota API отсутствует у OpenCode/Cursor/AGY:** Dashboard остаётся без provider-квот; локальные счётчики неполны и явно помечаются.
- **Multi-account Codex через `CODEX_HOME`:** изоляция требует runtime-проверки с `star-cliproxy`; до неё route остаётся opaque/ManualOnly.
- **Версионный дрейф:** matrix — snapshot; обязателен capability probe и `UnsupportedVersion` при отсутствии обязательной операции.
- **Opaque route:** `ManualOnly` с непрозрачным route не может доказать observed route; такие executions не считаются verified и требуют явного подтверждения пользователя.

## Альтернативы

- **OpenCode plugins/providers для Codex/AGY** — отклонено ADR-0007 независимо от наличия plugin contract.
- **Shared server на несколько аккаунтов** — отклонено до spike, доказывающего credential isolation (ADR-0002 §1).
- **Вывод квот из косвенных сигналов и фиктивные проценты** — запрещено §6.6; не является доказательством.
- **Подстановка `requestedRoute` вместо `observedRoute`** — запрещено: маскирует cross-account исполнение и делает routing непроверяемым.
- **Автоматический failover после подтверждения session** — отклонено §6.5: создаёт неявную смену binding.
