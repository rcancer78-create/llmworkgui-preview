# LLM Work GUI — roadmap реализации

**Связанное ТЗ:** `TECHNICAL_SPECIFICATION.md`
**Версия:** 1.4, синхронизирована с ТЗ 1.5; добавлен полный цикл создания и управления workflow разработки
**Правило:** следующий этап начинается только после выполнения exit criteria текущего либо после явного принятия зафиксированного ограничения владельцем продукта.

**Жёсткий gate Phase 0 → Phase 1:** Phase 1 запрещено начинать, пока все обязательные исследования Phase 0 не выполнены и их результаты не сохранены в capability matrix, protocol fixtures и ADR. Владелец может принять документированное `Unsupported`, blocker или ограничение как результат исследования, но не может заменить отсутствующий probe предположением и тем самым пропустить Phase 0.

---

## Общие правила выполнения

1. Делать небольшие проверяемые вертикальные срезы.
2. Сохранять уже существующие пользовательские файлы и конфигурации.
3. Не изменять импортированный `WORKFLOW.ZIP`.
4. Не расходовать реальную модельную квоту там, где достаточно fixtures/mock server.
5. Все реальные model calls должны быть минимальными и явно отмеченными.
6. Любой backend fallback видим пользователю; silent fallback запрещён.
7. Каждый этап завершается:
   - scoped diff review;
   - build/test report;
   - обновлением документации;
   - списком известных ограничений;
   - evidence по exit criteria.
8. Каждая крупная Phase 0–12 перед закрытием проходит независимый read-only review и обновление acceptance report. Для текущего завершения прямое указание пользователя задаёт Grok 4.6 High и Space Bunny через OpenCode; полные правила и provenance — [REVIEWER_POLICY_20261005.md](docs/work/REVIEWER_POLICY_20261005.md). Без фактического PASS соответствующего gate следующая Phase не считается принятой.
9. Reviewer не коммитит. Исполнитель фиксирует baseline commit/tag только после фактического закрытия gate и записывает hash в `PROJECT_STATE` и acceptance report. До этого immutable source manifests и receipts фиксируют проверяемые кандидаты, не заменяя принятие gate. `push` и remote требуют отдельной команды пользователя. Историческая роль Gemini/старые отчёты не переписываются.
10. Наблюдаемый набор визуальных WPF UI-тестов (QuickViewer suite):
    - приложение и окна запускаются и видимы на разблокированном рабочем столе Windows;
    - пользователь воочию видит навигацию, диалоги, состояния моделей, провайдеров, аккаунтов и workflow;
    - по умолчанию используются детерминированные test doubles без расходования модельных квот;
    - автоматизированные сценарии сохраняют скриншоты и артефакты ошибок;
    - видимые тесты строго отделены от быстрых headless и CI-safe тестов;
    - каждая крупная UI-фаза обновляет сценарии, а финальные фазы (11–12) выполняют полный визуальный регресс;
    - предусмотрена простая локальная команда запуска и запрет одновременной работы двух GUI-драйверов.

Фазы задают безопасный default order. Phase 8 не зависит от Health Center и после принятой Phase 4 может выполняться раньше Phase 7 только как изолированный scope без параллельного writer; это не меняет release gates Phases 5–7.

---

## Phase 0 — discovery и архитектурные контракты

### Цель

Зафиксировать реальные интерфейсы установленных OpenCode, Cursor Agent, Codex CLI, стандартного `agy` CLI, выбранного пользователем `agy-profile` и `star-cliproxy` до продуктовой интеграции соответствующего backend.

### Работы

- создать solution skeleton и docs/ADR;
- определить minimum supported Windows и .NET runtime;
- зафиксировать фактические версии CLI;
- исследовать OpenCode server API, event stream, sessions, approvals и cancellation;
- проверить, какие операции доступны через OpenCode ACP; он не заменяет закреплённый serve-first transport без отдельного change proposal;
- провести Cursor ACP handshake и записать sanitized protocol fixture;
- проверить контракт `star-cliproxy` как отдельного локального gateway для Codex/AGY, включая lifecycle, OpenAI-compatible API, streaming, cancel, session identity, model/route evidence и безопасную передачу provider-specific окружения;
- проверить контракт стандартных Codex/`agy` CLI, которые запускает `star-cliproxy`, для подтверждения фактической сессии, модели и аккаунта;
- проверить документированные команды `agy-profile` для сохранения, перечисления и выбора профиля AGY; не переключать аккаунт при работающем `agy` и не использовать принудительное переключение;
- зафиксировать границы: `agy-profile` работает с профилями одного Windows-пользователя; Codex account profile задаётся отдельным каталогом `CODEX_HOME`; оба механизма находятся за adapter `star-cliproxy` и не используют OpenCode;
- определить, какие quota fields provider действительно раскрывает;
- подготовить protocol fixtures без credentials;
- создать threat model и data-flow diagram;
- принять ADR: OpenCode serve API/topology и список отсутствующих операций;
- принять ADR: Cursor ACP lifecycle;
- принять ADR: secret storage;
- принять ADR: workflow blob/version storage.
- подготовить normative drafts таблиц Session, Execution и Health из ТЗ и сопоставить переходы с наблюдаемыми confirm/cancel/resume/crash events;
- зафиксировать capability matrix: `Supported/Unsupported/Unknown`, не создавать фиктивные fork/resume fixtures;
- определить normalized approval mapping OpenCode/Cursor;
- если package с entrypoint доступен, зафиксировать внешний контракт optional legacy entrypoint: start, terminal outcome, artifacts и `Not reported` fields, не меняя workflow; иначе явно отложить evidence до Phase 8 без блокировки остальных результатов Phase 0;
- подтвердить data-classification defaults и threat boundaries;

### Не делать

- не строить полный UI;
- не адаптировать workflow;
- не запускать массовые model probes;
- не читать credential stores.

### Exit criteria

- OpenCode session create/stream/cancel/resume доказаны минимальным spike или точно отмечены как blocker;
- Cursor ACP handshake доказан;
- account/quota capability matrix составлена;
- для `agy`/`agy-profile` записаны доступные команды, ограничения безопасности, способ подтверждения выбранного профиля и нативной сессии; недоказанные quota/route операции оформлены как blocker/ограничение;
- session/execution/health transition tables сверены с protocol evidence и готовы к кодированию без изменения инвариантов ТЗ;
- approval capability matrix и legacy entrypoint boundary задокументированы;
- неизвестные API не замаскированы предположениями;
- architecture и security ADR приняты.
- для каждого обязательного discovery-вопроса существует проверенный результат либо документированный `Unsupported/Blocked` с evidence; пустые/предполагаемые значения не закрывают Phase 0;
- только после сохранения всех перечисленных artifacts и формальной приёмки Phase 0 разрешён первый product-code change Phase 1.

---

## Phase 1 — фундамент приложения

### Цель

Получить запускаемый WPF shell, доменную модель, устойчивое локальное хранение и раннюю основу наблюдения за исполнителями и ревьюерами.

### Работы

- проекты Domain/Application/Infrastructure/App и tests;
- Generic Host, DI, logging, configuration;
- SQLite schema и migrations;
- Windows secret store abstraction;
- базовые entities: Project, ProviderProfile, BackendInstance, Account, Model, Route, Session, ClientRequest, Execution, ProjectLock, ApprovalRule;
- state machines session/execution/health строго по normative tables ТЗ и подтверждённым результатам Phase 0;
- compact three-pane application shell;
- базовая observable-run projection поверх Execution: роль (`Coordinator|Executor|Reviewer|Escalation|Unknown`), display label, state, route/session references, timestamps и evidence source без orchestration semantics;
- компактная Activity/Role timeline в Workspace: кто сейчас работает, в какой роли, состояние, последняя активность и подтверждённая session; неизвестные поля отображаются как `Not reported`;
- synthetic fixture для последовательности `Coordinator → Executor → Reviewer → Fix → Acceptance`, используемая только для UI/domain tests и явно помеченная как synthetic;
- dark/light/system theme;
- navigation и global status bar;
- базовый набор наблюдаемых визуальных WPF UI-тестов в стиле QuickViewer (видимый shell, навигация, переключение тем и DPI, status bar на детерминированных test doubles с сохранением скриншотов);
- crash-safe local app-data directories;
- redaction pipeline.
- retention categories/defaults и transactional cleanup contracts;

### Exit criteria

- clean build;
- migrations создают и повторно открывают БД;
- secrets round-trip без plaintext в DB/logs;
- state-machine unit tests проходят;
- `Ambiguous`, `RouteMismatch`, Reset/New session и reconciliation outcomes покрыты unit tests;
- observable-run projection и role timeline проходят unit/UI tests на synthetic fixture; UI не выдаёт synthetic/unknown данные за backend-observed;
- UI smoke при 100/150/200% DPI;
- базовый визуальный QuickViewer UI-тест на видимом рабочем столе проходит и сохраняет скриншоты;
- приложение стартует без установленных CLI и показывает понятный degraded state.

---

## Phase 2 — Process Supervisor и Execution Watchdog

### Цель

Создать общий безопасный слой управления backend-процессами до конкретных adapters.

### Работы

- отдельный lifecycle долгоживущего Process Supervisor и turn-level Execution Watchdog;
- async stdout/stderr и bounded buffers;
- normalized process events;
- process startup/liveness отдельно от session-confirmation/turn hard timeout и transport activity;
- graceful cancellation и bounded process-tree termination;
- run directories вне project checkout;
- orphan detection/reconciliation;
- устойчивый checkout writer lock + named OS mutex + single-supervisor guard;
- `clientRequestId`, prompt hash и manual duplicate confirmation;
- buffer overflow policy с непрерывным spool в run directory;
- raw/normalized event persistence;
- test fake-process harness.
- типизированный process launch contract: executable/arguments/working directory/stdin policy/environment передаются раздельно, без shell interpolation;
- запрет вложенных PowerShell `-Command` и произвольного shell text в штатных adapter paths; исключения только через фиксированный script contract;
- закрытый stdin для неинтерактивных процессов и нормализованный `InteractiveInputWait`/orchestration failure без списания model retry budget;
- argument-boundary/quoting fixtures для пустого обязательного аргумента, пробелов, кавычек, `$variable`, Unicode и длинных Windows paths.

### Exit criteria

- нет stdout/stderr deadlock;
- simulated silent process не считается failed только из-за тишины;
- startup failure, timeout, cancel, crash и ambiguous completion различаются;
- перезапуск GUI корректно обнаруживает orphan;
- recovery matrix различает `Reattached`, `Orphaned`, `Ambiguous`, `BackendMissing`;
- второй writer одного checkout заблокирован, подтверждённый read-only execution работает параллельно;
- stale writer lock не снимается до reconciliation, второй GUI не запускает Supervisor;
- hard timeout turn не завершает живой server/ACP process, heartbeat не пишет в protocol stdin;
- overflow не теряет terminal event молча;
- Supervisor не убивает посторонние процессы.
- fake process, пытающийся запросить обязательный параметр через stdin, не зависает бессрочно и завершается предсказуемой orchestration-классификацией;
- тесты доказывают, что argument list сохраняет точные границы значений и ни один штатный путь не создаёт вложенный PowerShell `-Command`.

---

## Phase 3 — OpenCode core adapter

### Цель

Сделать OpenCode основным рабочим backend программы.

### Работы

- executable/version discovery;
- managed `opencode serve` на loopback;
- API client и protocol normalization;
- providers/models discovery;
- create/confirm/continue/fork/reset session;
- prompt и streaming events;
- approvals;
- cancellation;
- usage/error normalization;
- server restart/reconnect;
- export sanitized session diagnostics;
- capability cache с freshness;
- requested/observed route evidence и `RouteMismatch`;
- basic health event capture для последующего circuit breaker;
- contract fixtures.

### Exit criteria

- UI создаёт OpenCode session и показывает native ID;
- process start и session confirmation визуально различимы;
- продолжение использует тот же binding;
- reset создаёт новый binding, не удаляя историю;
- cancel подтверждён;
- malformed event не рушит приложение;
- server restart/reconnect даёт определённый reconciliation outcome;
- unsupported operation скрыта/disabled, а diagnostic CLI fallback видим и не продолжает server session;
- один минимальный live smoke задокументирован.

---

## Phase 4 — providers и custom base URL

### Цель

Позволить пользователю настраивать OpenCode providers без ручного редактирования скрытых файлов.

### Работы

- Providers & Accounts UI;
- base URL/key/headers form;
- secret references;
- config preview/diff;
- connection test;
- plugin inventory;
- model refresh;
- capability details;
- local mock OpenAI-compatible server;
- HTTPS/loopback validation;
- redacted diagnostic export.
- Approval Rules UI с backend/project/operation scope и audit;
- сценарии наблюдаемых визуальных WPF UI-тестов в стиле QuickViewer для Providers & Accounts UI, формы конфигурации и модальных диалогов с сохранением скриншотов на mock-сервере.

### Exit criteria

- custom provider работает против local mock;
- визуальный QuickViewer UI-тест экранов провайдеров и аккаунтов успешно выполняется и сохраняет скриншоты;
- API key отсутствует в DB/logs/process list/export;
- secret headers/query/environment и config preview редактируются до отображения/записи;
- существующий OpenCode config не перезаписывается без preview/confirm;
- invalid URL/certificate/auth имеют разные понятные ошибки;
- unsupported model options нельзя выбрать.

---

## Phase 5 — account routing и quota dashboard

### Цель

Дать единое отображение аккаунтов, лимитов и управляемую маршрутизацию. Реализованная часть Phase 5 сохраняется как доменный/UI baseline. Интеграции Codex/AGY через OpenCode запрещены; непосредственная нативная AGY-реализация TASK-032 считается переходной и должна быть адаптирована в Phase 5R под `star-cliproxy`.

### Работы

- provider-neutral account bridge и route evidence, не привязанные к OpenCode plugins;
- account identity и auth state;
- quota-source adapters;
- normalized quota buckets и provenance;
- refresh scheduler, jitter/backoff;
- routing eligibility;
- Pinned/Sticky/QuotaFirst/PriorityFirst/Balanced/ManualOnly;
- reserve thresholds и concurrency limits;
- routing explanation;
- account cooldown;
- Quotas UI;
- test doubles для нескольких accounts;
- live read-only quota probe, где поддерживается.
- deterministic score/tie-break из ТЗ и сохранение quota snapshot IDs;
- profile pin/observed-route enforcement по evidence `star-cliproxy` и соответствующего CLI; при недоказанной привязке автоматическая multi-account маршрутизация запрещена;
- переключать AGY-профиль только после завершения всех связанных процессов и под единым writer/account lock; не использовать `-Force`, не копировать credentials и не выполнять ротацию для обхода лимитов;
- сценарии наблюдаемых визуальных WPF UI-тестов в стиле QuickViewer для Quotas UI и routing dashboard на детерминированных doubles с сохранением скриншотов.

### Exit criteria

- визуальный QuickViewer UI-тест экранов Quotas Dashboard и переключения аккаунтов успешно выполняется и сохраняет скриншоты;
- каждый account/model bucket отображается, включая Unknown/Unsupported;
- freshness/source/reset time видимы;
- два test accounts проходят deterministic routing scenarios;
- requested и observed route совпадают; mismatch не считается success;
- подтверждённый sticky binding не меняется; replacement session требует явного подтверждения;
- quota/auth failure исключает account согласно policy;
- exact/estimated/local quota визуально не смешиваются.
- `Unknown/Unsupported/Stale/Error` не получают числовой score; Router и Dashboard используют один snapshot ID;

---

## Phase 5R — рефакторинг backend-архитектуры Codex/AGY через star-cliproxy

### Цель

До начала Phase 6 убрать продуктовую зависимость Codex/AGY от OpenCode и заменить переходный прямой AGY adapter единым `star-cliproxy` gateway, сохранив уже реализованные доменные routing/quota/UI контракты.

### Работы

- принять ADR-0007 и выполнить Windows-spike актуального `star-cliproxy`: build/start/health, `/v1/models`, streaming, cancel, ошибки, provider/model evidence и process ownership;
- ввести отдельный `StarCliProxy` backend adapter и typed client; OpenCode adapter оставить только для OpenCode-native/custom providers;
- удалить из runtime/DI/config/UI все маршруты и плагины, которые проводят Codex либо AGY через OpenCode;
- подключить Codex к `star-cliproxy`; каждый разрешённый аккаунт запускается в отдельном неизменяемом account context с собственным абсолютным `CODEX_HOME`, без изменения глобальных `HOME`/`USERPROFILE` и без копирования `auth.json`;
- проверить на Windows, способен ли один `star-cliproxy` безопасно передавать разные `CODEX_HOME` для provider instances; если нет — один managed proxy process/config/port на Codex account;
- подключить AGY к `star-cliproxy`, сохранив `agy-profile` как единственный механизм явного выбора профиля; switch только под общим account/writer lock и при отсутствии живых AGY executions;
- запретить `agy-profile -Force`, `next`, `random`, чтение/копирование credentials и продолжение session после смены аккаунта;
- вынести account selection из transport adapter в общий `IAccountContextManager`; serialized select → verify → launch исключает TOCTOU;
- мигрировать существующие `AgyProcessRunner`/`AgyProfileAccountBridge` и OpenCode plugin filtering без потери тестов; удалить мёртвые ветви после миграции;
- обновить capability matrix, fixtures, DI, degraded-mode UI и acceptance; выполнить full tests и live smoke без вывода секретов.

### Exit criteria

- ни один Codex/AGY execution не создаётся через OpenCode, его plugins или provider configuration;
- Codex route однозначно связан с отдельным `CODEX_HOME`, а AGY route — с подтверждённым профилем `agy-profile`;
- `star-cliproxy` lifecycle, health, streaming, cancellation и фактические provider/model/session доказаны live fixtures;
- параллельные account switches сериализованы; смена account при живой связанной execution невозможна;
- requested/observed account, provider, model и session сохраняются; mismatch завершается `RouteMismatch`;
- существующие routing/quota/dashboard сценарии проходят после смены transport;
- документация, runtime configuration и UI не предлагают Codex/AGY через OpenCode.

---

## Phase 6 — Cursor ACP adapter

### Цель

Добавить Cursor как независимый нативный backend, не проводя его через OpenCode.

### Работы

- Cursor executable/version/model discovery;
- `cursor-agent acp` lifecycle;
- ACP handshake/capabilities;
- create/load session;
- workspace binding;
- streaming updates;
- permissions;
- cancel;
- session native ID и lineage;
- model overrides: context/effort/fast;
- modes plan/ask/agent, если ACP подтверждает поддержку;
- crash/restart behavior;
- contract fixtures и live smoke.
- quota/health отображение `Unsupported/Unknown`, если ACP не даёт данных;

### Exit criteria

- ACP является основным transport;
- native session подтверждается и отображается;
- reasoning/speed options берутся из discovery;
- Unknown capability недоступна для отправки и не изображается Supported;
- cancel работает;
- permission request обрабатывается UI;
- ACP failure не вызывает скрытый print-mode fallback;
- minimal read-only live smoke задокументирован.

---

## Phase 7 — Health Center и recovery

### Цель

Сделать ошибки и исключение проблемных routes управляемыми и объяснимыми.

### Работы

- normalized error taxonomy;
- rolling health metrics;
- circuit breaker;
- cooldown/quarantine;
- immediate auth/model-mismatch blocks;
- manual disable;
- connection/model probes;
- recovery audit;
- impacted-session view;
- safe retry recommendations;
- Health Center UI.
- sticky-session stop behavior для unhealthy route;
- pinned probe, `ForcedEnabled` и verified recovery;
- сценарии наблюдаемых визуальных WPF UI-тестов в стиле QuickViewer для Health Center, индикации circuit breaker и recovery диалогов с сохранением скриншотов.

### Exit criteria

- визуальный QuickViewer UI-тест экранов Health Center и диалогов восстановления успешно выполняется и сохраняет скриншоты;
- повторяемая ошибка переводит test route в ожидаемое состояние;
- quarantine route не участвует в routing;
- manual probe имеет собственный execution/evidence;
- forced enable отличается от verified recovery;
- ambiguous completion не ретраится автоматически.
- user cancel/deny/ambiguous не увеличивают breaker;
- cooldown переходит в `ProbeRequired`, а не автоматически в `Healthy`;

---

## Phase 7M — Mirasim backend и вызов моделей через харнесы

### Цель

Подключить установленный Mirasim как отдельный backend LLMWorkGUI для Codex, AGY, Grok и моделей, которые Mirasim предоставляет через эти харнесы. Сохранить существующие маршруты и достоверность session, account, route и terminal evidence. Архитектурная база: ADR-0008; задание: `docs/work/TASK_047_MIRASIM_BACKEND.md`; критерии: `docs/acceptance/MIRASIM_BACKEND_ACCEPTANCE_PLAN.md`.

### Работы

1. Провести Windows protocol spike актуальной версии: host discovery/auth, способ подключения без доступа к внутренним credential-файлам, dynamic harness/model catalog, session/history/watch/stop/answer, terminal/error/route/account evidence. Сохранить sanitized fixtures и capability matrix. Отдельно доказать передачу произвольного prompt без его помещения в argv, URI и логи LLMWorkGUI; до этого product execution имеет `Unsupported`.
2. Добавить отдельный Mirasim backend adapter и UI выбора `Mirasim → harness → model → route mode`. Не проводить эти вызовы через OpenCode и не менять автоматически действующие `star-cliproxy`/Cursor ACP маршруты. User-owned Mirasim host не перезапускать, не обновлять и не завершать из LLMWorkGUI.
3. Подключить create/continue/stream/terminal/cancel/reconcile к существующим Session Manager, Execution Supervisor, writer lock и Health Center. `done+error`, incomplete и transport loss не считать успехом; после неопределённой доставки не повторять prompt автоматически.
4. Проверить observed harness/model/account/route leg отдельно от requested route. Пока account или leg не подтверждён, разрешить только явно обозначенный `ManualOnly`, без автоматической ротации и скрытого fallback. Глобальные `relay.enabled`/`relay.always` и активный аккаунт Mirasim не переключать ради отдельной сессии.
5. Проверить approvals и отмену живыми ограниченными сценариями; `bypassPermissions` не принимать за approval mapping. Показывать пользователю границу raw recording/export Mirasim. Квоты и reset time отображать с реальным источником и freshness.
6. Выполнить минимальные отдельные вызовы Codex Own, AGY, Grok, K3 и GLM через нужные харнесы. Зафиксировать действительные model, route leg, terminal outcome и ограничения Go relay. Наличие модели в каталоге и `relayStatus=ok` не закрывают этот пункт.

### Exit criteria

- Discovery принят только при версии/capability probe и sanitized protocol evidence. Неизвестная новая версия не получает silent fallback.
- Manual execution принят только при безопасном payload transport, native session/turn identity, проверенном terminal outcome, корректных failure/ambiguous/cancel состояниях и видимом WPF сценарии.
- Automatic routing принят только для маршрутов с доказанными pin/observed account и route leg, approval/cancel/recovery контрактами и writer lock. Opaque route остаётся `ManualOnly` с `Not reported` для отсутствующих полей.
- K3 и GLM подтверждены отдельными успешными программными вызовами либо явно отмечены `Unverified/Unavailable`; пользовательское наблюдение в Mirasim UI не подменяет backend evidence.
- Existing backend, незавершённые Phase 7 изменения и пользовательские настройки Mirasim сохранены; build/tests, scoped diff, Windows runtime и независимая приёмка задокументированы.

Этот этап может выполняться после принятой Phase 5R параллельно подготовке Phase 7 **только в изолированном worktree и без второго writer в одном checkout**. Его gate не меняет статус Phase 7 и не снимает обязательные проверки последующих фаз.

---

## Phase 8 — Workflow library и immutable import

### Цель

Хранить и выбирать workflow без изменения валидированного оригинала.

### Работы

- safe ZIP/folder import;
- immutable blob + SHA-256;
- tree/Markdown preview;
- workflow metadata/versioning;
- project binding;
- active/candidate/archived states;
- draft workspace как новый immutable version ID;
- version compare;
- byte-identical export оригинала;
- workflow compatibility report;
- legacy entrypoint configuration;
- immutable scratch isolation и post-operation source hash check;
- package size/file-count/compression limits;
- тестовый импорт предоставленного `WORKFLOW.ZIP`.

### Exit criteria

- Zip Slip/decompression bomb fixtures отклоняются;
- hash исходного архива сохраняется;
- import/export оригинала byte-identical;
- выбор active version не меняет package;
- приложение не переписывает роли или файлы workflow.
- preview/draft/run scratch не находится в blob store или project root;
- active/referenced version не удаляется, rollback меняет только active pointer;

---

## Phase 9 — model-assisted workflow adaptation

### Цель

По явной команде создавать адаптированного кандидата под подключённые providers/models.

### Работы

- sanitized capability catalog;
- выбор source version и adapter route;
- data preview/consent;
- adaptation prompt contract;
- candidate workspace;
- result integrity validation;
- model/provider reference validation;
- full diff и semantic role-binding summary;
- follow-up в той же adaptation session;
- accept/save/reject;
- activation и rollback;
- audit trail.
- pre-send secret scan и cost/quota preview;
- re-validation against current catalog непосредственно перед activation;

### Exit criteria

- без нажатия команды никакой model call/изменение не происходит;
- adaptation не меняет original/active version;
- candidate содержит diff и rationale;
- отсутствующая model/capability даёт blocker;
- activation требует явного подтверждения;
- semantic role/stage/quality/escalation change является blocker вне отдельно подтверждённого expanded scope;
- rollback выбирает любую ранее активную version, source blob остаётся byte-identical.

---

## Phase 10 — расширенная workflow orchestration и visualization

### Цель

Расширить раннюю ролевую Activity/Role timeline до конструктора и исполняемого workflow разработки: от постановки задачи и документов до кода, многоуровневого review, UI-приёмки и итогового результата.

### Milestone 10A — полноценный Workflow run поверх ранней observable projection

- workflow run aggregate поверх уже существующих Sessions/Executions и Phase 1 observable-run projection;
- stages/roles/transitions и role bindings;
- типовые этапы постановки задачи, архитектуры, ТЗ, roadmap, утверждения, реализации, UI-работы, review и приёмки как настраиваемая схема, а не hard-coded сценарий;
- current stage/owner/route/session только из фактических events;
- packets, diffs, validation artifacts и run timeline;
- terminal workflow outcome отдельно от terminal execution outcome.
- раздельные вердикты нескольких reviewer и evidence подтверждений пользователя для переходов между этапами.

### Milestone 10B — supervised legacy entrypoint

- запуск только user-declared entrypoint в scratch-копии;
- единое process tree и checkout writer lock;
- opaque legacy execution с process state/exit code/artifacts;
- `Not reported` вместо синтетических stage/role/route/session;
- запрет legacy entrypoint самостоятельно обходить штатные адаптеры: Codex/AGY доступны только через `star-cliproxy` adapter и общие locks.

### Milestone 10C — declarative nodes

- schema и graph validation primitive nodes из ТЗ;
- prompt/review/writer через OpenCode, Cursor ACP, `star-cliproxy` или Mirasim согласно доказанным возможностям каждого канала;
- user approval/decision nodes;
- роли архитектора, автора ТЗ, кодера, UI-кодера, reviewer разных уровней, тестировщика и утверждающего; пользовательские роли и независимые от них route/model bindings;
- правила переходов между этапами кодирования: обязательные документы, diff/scope, review каждого уровня, тесты, визуальная приёмка и лимиты исправлений;
- смена модели, AGY profile и Codex account context между этапами с новой session, provenance и видимым route-change; никакой silent fallback;
- retry/escalation budgets и запрет retry для `Ambiguous`;
- pause/cancel/resume только там, где capability и семантика подтверждены;
- сценарии наблюдаемых визуальных WPF UI-тестов в стиле QuickViewer для Workflow Studio, Activity/Role timeline и approval dialogs на тестовых данных с сохранением скриншотов.

### Milestone 10D — нативный WPF экран визуализации Workflow (Activity Monitor)

- нативная схема строится из ролей активной версии workflow и фактических переходов выбранного run; цепочка `User → архитектор → кодер → reviewer` служит только примером раскладки, не встроенным обязательным процессом;
- статусы узлов («работает», «завис», «остановлен», «готово», «ждёт») с индикатором у каждого фактически работающего узла, включая допустимые параллельные read-only операции;
- Codex и AGY видны как доказанные маршруты `star-cliproxy` назначенной роли, а не как неизменные визуальные личности;
- боковая панель деталей (drawer) рядом со схемой с отображением источника задачи («← от ...»), меток времени («Получено», «Изменено»), полного не обрезанного текста текущей работы с переносом и истории за последние N минут;
- строгая опора только на штатные доменные данные observable-run projection (`ObservableRunProjection`, `ActivityTimeline`, `RoleTransferEvidence`) без использования внешних PowerShell-скриптов, фоновых HTTP-серверов или парсинга внешних логов;
- честное отображение не переданных или отсутствующих полей как «Not reported» (или «—»);
- QuickViewer UI-сценарии для схемы ролей и боковой панели деталей с сохранением скриншотов.

### Milestone 10E — Workflow Studio и шаблоны документов

- создание workflow с нуля, редактирование этапов/переходов/ролей/моделей, проверка графа и preview до запуска;
- встроенные и пользовательские версионированные шаблоны workflow: сохранить, клонировать, изменять и назначать проекту без изменения активного run или исходного импортированного ZIP;
- шаблоны постановки задачи, архитектуры, ТЗ, roadmap, task/fix packet, review и acceptance report с полями, инструкциями для моделей и критериями полноты;
- генерация draft документа, ручная правка, версия/hash, preview отправляемых данных, secret scan, проверка несколькими моделями и отдельные вердикты;
- явное утверждение конкретного hash/version требуемых документов перед передачей кодеру; конфликтные вердикты направляются в названный схемой узел разрешения либо пользователю;
- в UI отдельно видны редактируемая схема шаблона и фактический граф выполняемого run.

### Exit criteria

- 10A: фикстура проходит от entry до terminal workflow outcome, переходы имеют evidence и нет скрытого следующего stage;
- 10A: пользователь видит активную роль, модель, account и session только когда они фактически известны;
- визуальный QuickViewer UI-тест исполнения workflow и диалогов approval успешно выполняется на видимом рабочем столе и сохраняет скриншоты;
- 10B: legacy entrypoint не изменяет source blob, не обходит writer lock и не получает выдуманных session/route fields;
- 10C: graph validation отклоняет отсутствующие targets, недостижимый terminal и необоснованный бесконечный цикл;
- 10C: terminal workflow outcome отличается от успешного завершения отдельного executor;
- 10D: нативный WPF экран строит схему из ролей активного workflow и наблюдаемых событий run, отображает состояния и индикатор у каждого фактически работающего узла, включая одновременно работающие read-only узлы; открывает панель деталей без скрытия схемы, выводит «Not reported» для несообщённых полей и не использует внешние скрипты/HTTP-сервер/парсинг логов.
- 10E: пользователь создаёт, сохраняет, меняет и повторно запускает собственный workflow; новая версия не меняет прежние run и immutable import;
- 10E: сценарий `задача → архитектура → ТЗ → roadmap → несколько review → утверждение → код/UI → тесты → приёмка` проходит на детерминированных моделях-заглушках; кодер не стартует без единогласного `approve` обязательных reviewer на текущий hash каждого документа и отдельного утверждения по схеме;
- 10E: отсутствующий reviewer, пара `approve`/`reject`, изменённый после review hash и отсутствующий обязательный UI-артефакт или пользовательская визуальная приёмка каждый отдельно блокируют названный схемой переход;
- 10E: отдельные AGY `agy-profile` и Codex `CODEX_HOME` переключения создают новые native sessions с requested/observed route evidence, без переноса сессии или credentials; Mirasim active account, relay и recording не меняются;
- 10E: неподтверждённый или несовпадающий маршрут блокирует автоматический переход, история run сохраняет application-level связь и причину смены.

---

## Phase 11 — UX consolidation

### Цель

Собрать функции в быстрый, компактный и визуально последовательный продукт.

### Работы

- окончательная three-pane компоновка;
- command palette и shortcuts;
- activity center;
- diff/artifact viewers;
- quotas/health compact widgets;
- empty/loading/error states;
- search и filters;
- accessibility;
- 100/150/200% DPI;
- performance profiling;
- большие histories/event streams;
- onboarding без обязательного model call;
- консолидированный прогон полного набора наблюдаемых визуальных WPF UI-тестов в стиле QuickViewer по всем ключевым экранам и диалогам с сохранением скриншотов (Dark/Light, 100/150/200% DPI).
- доведение Workflow Studio, редакторов шаблонов ТЗ/roadmap, матрицы ролей и живой схемы run до единого компактного UX.

### Exit criteria

- основные сценарии выполняются без терминала;
- нет layout clipping при поддерживаемом DPI;
- keyboard navigation покрывает ключевые действия;
- UI остаётся отзывчивым под synthetic event load;
- synthetic load соответствует нормативному профилю §9.2 ТЗ, p95 latency и memory result записаны;
- полнотекстовый поиск проходит на 100 000 redacted events и secret fixtures не индексируются;
- проведён реальный visual acceptance с сохранением скриншотов и полным прогоном QuickViewer визуальных тестов на разблокированном рабочем столе.

---

## Phase 12 — hardening и release candidate

### Цель

Доказать безопасность, восстановление и готовность к ежедневному использованию.

### Работы

- полный threat-model review;
- секрет-скан репозитория и diagnostic bundles;
- DB migration/backup/restore;
- process crash/reboot scenarios;
- provider/CLI version mismatch scenarios;
- long-running execution;
- quota polling soak;
- retention cleanup;
- per-user self-contained installer, upgrade/rollback и сохранение app-data;
- README/Architecture/Security/Diagnostics/Testing;
- полный регрессионный прогон наблюдаемых визуальных WPF UI-тестов в стиле QuickViewer на чистом профиле;
- русскоязычный интерфейс по умолчанию на чистом профиле, включая основные экраны, onboarding, диалоги, уведомления и ошибки; фактический проход по работающему приложению с проверкой тем и масштабов 100/150/200% DPI;
- release acceptance report.
- сквозной приёмочный сценарий разработки от новой задачи до утверждённого результата, включая конфликт двух reviewer, доработку, отдельные переключения AGY profile и Codex `CODEX_HOME`, доказательство новых sessions, отсутствие влияния на Mirasim host и восстановление run; где live call не нужен, используются детерминированные заглушки.

### Exit criteria

- выполнены все acceptance criteria ТЗ;
- на детерминированных заглушках каждый из случаев отдельно блокирует названный схемой переход: отсутствует обязательный reviewer, reviewer выдали `approve` и `reject`, hash документа изменился после review, отсутствует обязательное UI-доказательство;
- на детерминированных заглушках отдельно доказано, что переключения AGY `agy-profile` и Codex `CODEX_HOME` создают новые native sessions без переноса предыдущей сессии и без изменения Mirasim host;
- clean Release build;
- unit/contract/integration/UI suites green;
- полный визуальный регресс QuickViewer UI-тестов пройден с 0 failures, артефакты и скриншоты включены в release acceptance report;
- реальные OpenCode и Cursor smoke явно отделены от mocks;
- secrets отсутствуют в artifacts;
- известные ограничения перечислены;
- rollback/backup проверены;
- installer устанавливает/обновляет приложение на чистом профиле и не изменяет внешние CLI;
- владелец продукта принял визуальный и функциональный результат.
- владелец продукта подтвердил пригодность русскоязычного интерфейса; до этого локализация и Phase 12 остаются открытыми.

---

## Приоритеты поставки

### MVP

Phases 0–4 с ранней Activity/Role timeline из Phase 1 и exit criteria Phase 3 для create/confirm/continue/reset session и live execution status. До подключения реального backend synthetic timeline явно маркируется; после Phase 3 она питается подтверждёнными OpenCode events. До Phase 5 quota slot показывает `Unknown/Never`, до Phase 7 health — только `Basic health`. MVP доказывает OpenCode-first архитектуру, custom provider setup и безопасное хранение секретов, но не заявляет multi-account/quota/circuit-breaker readiness.

### Beta

Phases 5–9, включая отдельный Phase 7M. Beta добавляет multi-account/quota routing, Cursor ACP, Mirasim backend после его собственного protocol gate и неизменяемую workflow library с адаптацией кандидатов.

### 1.0

Phases 10–12. Конструктор и шаблоны workflow разработки, шаблоны документов, полная orchestration поверх ролевой ленты, наблюдение за запущенным процессом, UX, recovery, hardening и release evidence.

---

## Запрещённые сокращения roadmap

- строить UI поверх парсинга TUI;
- хранить API keys в `appsettings.json`;
- считать model slug доказательством доступности;
- считать process PID доказательством session;
- показывать неизвестную квоту как 0% или 100%;
- переключать account/model без события и объяснения;
- менять исходный workflow ради удобства движка;
- выполнять adaptation автоматически;
- объявлять этап готовым только по build;
- откладывать secret handling, session identity и process recovery «на потом».
