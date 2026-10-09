# LLM Work GUI — техническое задание

**Статус:** реализация Phases 1–12 проходит завершение и корректирующее whole-review; текущий RC использует schema 27 и .NET 10. Регрессионные PASS не заменяют открытые native/security/owner acceptance gates. Mirasim имеет отдельный протокольный gate.
**Версия:** 1.6
**Дата:** 2026-10-06
**Целевая платформа:** Windows 10/11 x64
**Рабочее имя продукта:** LLM Work GUI

Phase 1 является жёстко зависимой от Phase 0: product-code реализация не начинается до получения, документирования и приёмки обязательных capability/API/protocol результатов. Документированный `Unsupported` считается допустимым результатом; непроверенное предположение — нет.

---

## 1. Назначение

Создать современное настольное Windows-приложение — единый рабочий интерфейс для запуска и наблюдения за coding-моделями через независимые backend adapters: OpenCode, NativeGateway/LLMGateway для нативных клиентов, `star-cliproxy` как legacy-канал Codex/AGY, нативный Cursor Agent ACP и Mirasim как отдельный канал для поддерживаемых им харнесов и моделей.

Программа должна объединить в одном интерфейсе:

- проекты и рабочие каталоги;
- провайдеров, модели и аккаунты;
- точный выбор модели, reasoning effort и поддерживаемого speed-режима;
- сессии и явное подтверждение фактически используемой backend-сессии;
- несколько разрешённых пользователем аккаунтов Codex/AGY через NativeGateway/LLMGateway: Codex изолируется отдельными `CODEX_HOME`, Antigravity использует per-profile API-key переменные; legacy `star-cliproxy`/`agy-profile` остаётся отдельным последовательным каналом. Реальная приёмка двух аккаунтов требует самостоятельного подтверждения и не выводится из наличия адаптера;
- явный выбор харнеса и модели в Mirasim, в том числе Codex, AGY, Grok и доступных моделей Go, только по фактически обнаруженному каталогу и подтверждённому маршруту;
- лимиты, остатки, время сброса и достоверность данных о квоте;
- текущий статус всех запущенных моделей;
- устойчивое управление процессами, ошибками, health-state и временным исключением неисправных маршрутов;
- хранение и запуск пользовательских workflow;
- создание моделью адаптированной копии workflow под фактически доступные модели и провайдеры;
- проектирование собственных workflow разработки: от идеи и постановки задачи через проектирование, ТЗ, дорожную карту, независимую проверку и утверждение документов до реализации, UI-работы, ревью и приёмки;
- редактируемые и сохраняемые шаблоны workflow, ТЗ и других документов с явными инструкциями для назначенных моделей;
- наглядное отображение схемы workflow и фактического выполнения каждого запущенного процесса.

Единый вызов моделей и управление полным циклом разработки — два равноправных назначения продукта. Workflow не обязан быть импортированным ZIP: пользователь может создать его с нуля, сохранить как шаблон, клонировать и изменять без правки уже запущенной версии.

Программа не является новым LLM-провайдером и не должна самостоятельно эмулировать закрытые API сервисов.

---

## 2. Главная архитектурная граница

### 2.1. Backend-каналы

1. **OpenCode Backend** — отдельный канал для:
   - поддерживаемых OpenCode-native провайдеров, кроме Codex и AGY;
   - пользовательских OpenAI-compatible провайдеров по `baseUrl` и API key;
   - OpenCode plugins, не выполняющих переключение аккаунтов/профилей AGY или Codex.
2. **StarCliProxy Backend** — локальный OpenAI-compatible gateway [`star-cliproxy`](https://github.com/starhunt/star-cliproxy) для Codex и Gemini Antigravity. Он запускает соответствующие CLI как provider subprocesses; OpenCode в этом маршруте не участвует.
3. **Cursor Native Backend** — отдельный канал через `cursor-agent acp` и ACP по `stdio`.
4. **Mirasim Backend** — отдельный канал к установленному пользовательскому Mirasim host для обнаруженных харнесов и моделей. Mirasim владеет запускаемыми через него CLI и собственными session; LLMWorkGUI не объявляет такой ход `star-cliproxy` или Cursor ACP execution.
   **NativeGateway** — дополнительная интеграция локальной библиотеки LLMGateway для обнаруженных native adapters и GrokBot в общем composer. Импорт каталога не активирует запросы автоматически. Локальный account/catalog не доказывает внешний account/model origin. Project dispatch проходит durable execution/session admission, checkout/capacity и одноразовый grant точного запроса до фактического adapter start; возможная доставка без terminal proof сохраняет `Ambiguous` и владельца. Секреты и subprocess credentials не становятся UI route evidence. Generic workflow channel остаётся `Unsupported`; полный productive gate не заменяется embedded HTTP или no-model lifecycle проверкой. Подробные транспортные границы: [egress inventory](docs/work/EGRESS_PATH_INVENTORY_20261006.md).
5. Для маршрута `star-cliproxy` AGY account context выбирается через [`agy-profile`](https://github.com/haclongkim/agy-profile), а Codex account context — отдельным абсолютным `CODEX_HOME`. Смена context требует новой backend session и выполняется до запуска execution под общим lock. Для Mirasim действует отдельный контракт §6.11b.
6. Плагины, providers и configuration OpenCode для Codex/AGY запрещены. Прямой вызов Codex/AGY приложением после Phase 5R также запрещён: CLI process принадлежит выбранному `star-cliproxy` либо пользовательскому Mirasim host, к которому подключён adapter.

### 2.2. Workflow

Переданный `WORKFLOW.ZIP` является внешним, ранее валидированным пользовательским workflow.

Обязательные правила:

- не перепроектировать его в рамках разработки программы;
- не изменять импортированный оригинал;
- не переносить его конкретные роли и модели в hard-coded бизнес-логику программы;
- не считать его единственным возможным workflow;
- сохранять байты исходного архива и его SHA-256;
- исполнять выбранную пользователем активную версию;
- адаптировать workflow только по явной команде пользователя через выбранную модель;
- адаптация всегда создаёт новую версию-кандидат;
- до активации показывать полный diff, объяснение замен и нерешённые соответствия;
- активировать кандидат только после явного подтверждения пользователя;
- обеспечивать откат к любой ранее активной версии.

---

## 3. Термины и доменная модель

- **Provider** — OpenCode provider/plugin, нативный Cursor backend, Codex/AGY provider внутри `star-cliproxy` либо харнес внутри Mirasim.
- **Provider profile** — конфигурация подключения к provider: endpoint или CLI, параметры запуска и secret reference; для маршрута `star-cliproxy` AGY использует профиль `agy-profile`, Codex — отдельный каталог `CODEX_HOME`, без копии credentials; для Mirasim хранится ссылка на выбранный host/harness без копирования его credentials.
- **Account** — отдельная учётная запись внутри provider profile.
- **Model descriptor** — обнаруженная модель и её фактические capabilities.
- **Route** — конкретная комбинация backend, provider profile, account, model и вариантов выполнения.
- **Backend instance** — управляемый процесс и transport (`opencode serve`, `star-cliproxy`, `cursor-agent acp`) либо подключённый пользовательский Mirasim host; пользовательский host приложение не завершает и не обновляет.
- **Session** — локальная запись приложения, связанная с native session ID backend.
- **Execution** — один запуск или один turn в рамках session.
- **Client request** — уникальная попытка отправки пользовательского prompt с `clientRequestId` и hash нормализованного payload.
- **Quota snapshot** — снимок лимита с источником, временем и уровнем доверия.
- **Health state** — накопленное состояние работоспособности backend instance, provider profile, account, route или session.
- **Workflow package** — импортированный набор файлов workflow.
- **Workflow version** — неизменяемая ревизия package. Draft и candidate не изменяют существующую version: они сохраняются как новый version ID.
- **Workflow run** — исполнение конкретной версии workflow.
- **Role binding** — сопоставление логической роли workflow конкретному route или правилу выбора route.
- **Development workflow template** — версионированная пользовательская схема этапов, ролей, переходов, правил проверки и выпуска артефактов; отличается от запущенного workflow run.
- **Document template** — версионированный шаблон ТЗ, дорожной карты, задания, review или иного артефакта с полями, критериями полноты и инструкциями для модели-получателя.

Ключевой принцип: `Role`, `Provider`, `Account`, `Model`, `Route` и `Session` — разные сущности. Нельзя использовать model slug как идентификатор аккаунта, роли или сессии.

---

## 4. Обязательный технологический базис

### 4.1. Клиент

- C# и актуальный LTS .NET, доступный на старте реализации; целевой baseline — `.NET 10`.
- WPF desktop application, `net10.0-windows`, x64.
- Рабочее дерево переведено на SDK `10.0.401`, `net10.0` / `net10.0-windows` по
  [ADR-0011](docs/adr/ADR-0011-net10-runtime-and-sqlite-bundle.md). Полные runtime и package проверки
  фиксируются отдельно; неизменяемые архивы прежних RC сохраняют свой .NET 8 baseline.
- MVVM без code-behind бизнес-логики.
- Microsoft Generic Host для DI, конфигурации, логирования и lifecycle.
- Асинхронные операции с `CancellationToken` во всех backend-вызовах.
- SQLite для локального каталога, состояния, истории и индексов.
- Секреты — Windows Credential Manager или DPAPI; запрещено хранить API keys в SQLite, JSON, логах или workflow-файлах.

Допускается заменить конкретную библиотеку UI/MVVM/SQLite после короткого ADR, но нельзя менять WPF и Windows-first направленность без согласования.

### 4.2. Процессная архитектура

```text
WPF UI
  └─ Application Services
      ├─ Project Catalog
      ├─ Provider/Model Catalog
      ├─ Routing Engine
      ├─ Session Manager
      ├─ Workflow Service
      ├─ Quota Service
      ├─ Health Service
      └─ Execution Supervisor
          ├─ Process Supervisor
          │   ├─ managed opencode serve
          │   ├─ managed star-cliproxy → codex/agy CLI provider
          │   ├─ managed cursor-agent acp
          │   └─ optional supervised legacy entrypoint
          ├─ connected user-owned Mirasim host (Codex/AGY/Grok harnesses)
          └─ Execution Watchdog
              ├─ OpenCode API / event stream
              ├─ star-cliproxy OpenAI-compatible API / SSE
              ├─ Cursor ACP turns over stdio
              └─ Mirasim host protocol / normalized events
```

UI не должна напрямую запускать процессы, читать stdout или обращаться к SQLite.

### 4.3. Prerequisites и поставка runtime

- Release-сборка приложения поставляется self-contained для Windows x64 и не требует отдельной установки .NET Desktop Runtime.
- OpenCode, `star-cliproxy`, Codex/AGY CLI и Cursor Agent являются внешними необязательными prerequisites: отсутствие компонента переводит только соответствующий backend в понятное degraded-состояние.
- Приложение не устанавливает и не обновляет OpenCode, `star-cliproxy`, Cursor Agent, Codex, `agy`, `agy-profile`, Node/Bun или provider plugins без отдельной команды пользователя.
- Если выбранный безопасный Markdown renderer требует WebView2, installer проверяет runtime и показывает официальный способ установки; допускается renderer без WebView2 после ADR.
- Точный список обнаруживаемых executable, минимальных версий и runtime dependencies фиксируется Phase 0 и публикуется в `README.md`.

---

## 5. Проверенный локальный capability snapshot

Снимок служит исходной точкой, но не заменяет runtime discovery. Версии и capabilities обязаны обнаруживаться заново.

На 2026-09-22 обнаружены:

- OpenCode `1.18.31`: `serve`, `acp`, `run`, `providers`, `models`, `stats`, `export`, `import`, `session`, plugins, resume и fork;
- Cursor Agent `2026.09.15-d2fe57e`: `acp`, model discovery, `--resume`, `--continue`, modes `plan`/`ask`, parameterized model overrides;
- Codex CLI `0.155.0-alpha.9.2`: `app-server`, `exec --json`, resume/fork/review; после Phase 5R запускается только provider adapter'ом `star-cliproxy` с выбранным `CODEX_HOME`;
- AGY CLI `1.1.23`: model discovery, `--conversation`, `--continue`, effort `low|medium|high`, `plan|accept-edits`, JSON/stream-JSON; после Phase 5R запускается только provider adapter'ом `star-cliproxy` после выбора `agy-profile`;
- `star-cliproxy`: публично заявлены Codex/AGY providers, OpenAI-compatible API, SSE и Codex CLI/app-server modes; конкретная версия и Windows multi-account topology должны быть подтверждены live-spike Phase 5R.

Нельзя привязывать реализацию к указанным версиям. При старте adapter должен записать фактические version/capabilities и корректно перейти в `UnsupportedVersion`, если обязательный контракт отсутствует.

---

## 6. Функциональные требования

### 6.1. Проекты

Пользователь может:

- добавить существующую папку проекта;
- указать отображаемое имя;
- видеть путь, Git branch, dirty-state и наличие обязательных project instructions;
- назначить default workflow и default route policy;
- открыть несколько независимых рабочих sessions внутри одного проекта;
- удалить проект только из каталога приложения, не удаляя файлы с диска;
- открыть папку проекта, терминал или внешний diff средствами Windows shell. Это действие не запускает управляемый Codex/AGY execution. Пользовательские внешние процессы не участвуют в lock-системе приложения; внешние записи необходимо координировать отдельно и не совмещать с управляемым writer в том же checkout.

Перед запуском writer приложение обязано получить устойчивый checkout writer lock. Правила v1:

- scope lock — канонический нормализованный корень checkout, а не display name проекта;
- владелец — `executionId`, instance ID приложения и generation управляемого процесса;
- lock хранится в SQLite и дублируется named OS mutex для защиты от второго экземпляра GUI;
- lock берёт любой execution, для которого read-only не доказан capabilities/режимом: OpenCode writer, Cursor agent/edit, legacy entrypoint, shell/write-capable execution;
- подтверждённые `plan`/`ask` и другие доказанно read-only executions могут идти параллельно;
- после аварии stale lock не снимается автоматически, пока связанный execution имеет `Orphaned` или `Ambiguous`; пользователь получает recovery action с evidence;
- второй экземпляр приложения с тем же app-data каталогом может открыться только в режиме просмотра и не запускает Supervisor;
- override lock в v1 не разрешает параллельных writers: пользователь должен завершить/reconcile прежний execution либо выбрать другой checkout/worktree.

Одновременные управляемые приложением writers в одном checkout запрещены без исключения. Несколько read-only sessions и writers в разных checkout/worktree разрешены. Это coordination boundary управляемых executions, а не защита файловой системы от пользовательских или сторонних процессов.

### 6.2. Provider profiles

Экран Providers должен поддерживать:

- обнаружение установленного OpenCode и Cursor Agent;
- выбор executable вручную;
- просмотр версии и результата capability probe;
- запуск и остановку управляемого OpenCode server;
- добавление OpenCode-compatible provider по `baseUrl`;
- ввод API key через защищённое поле;
- необязательные headers, organization/project identifiers и timeout; значения headers/query/environment с именем или пользовательской пометкой `authorization`, `api-key`, `token`, `secret`, `password` всегда являются secrets;
- проверку соединения без вывода секрета;
- импорт существующей OpenCode configuration только после preview;
- запрет перезаписи пользовательского OpenCode config без явного подтверждения;
- plugin inventory и состояние plugin: installed/enabled/error/update-available/unsupported;
- диагностический экспорт с автоматической редакцией секретов.

Все endpoint URL валидируются. По умолчанию разрешён HTTPS; HTTP разрешается только для loopback/local network после предупреждения.

Config preview/diff проходит redaction **до** отображения, логирования и записи в audit. Secret values хранятся только как ссылки на Windows secret storage и никогда не передаются в command line.

### 6.3. Каталог моделей и capabilities

Для каждой модели хранить:

- backend и provider identifiers;
- provider-native model ID;
- display name;
- account availability;
- supported reasoning values;
- supported speed/fast values;
- supported modes: chat/plan/ask/agent/edit/review;
- tool and attachment capabilities;
- context limit, если provider сообщает его достоверно;
- provenance: provider-reported, plugin-reported, user-defined;
- capability state: `Supported`, `Unsupported`, `Unknown`, `Stale`, `Error`;
- discovery time и freshness;
- enabled/disabled state;
- health summary.

UI должна показывать только `Supported` значения reasoning/speed. `Unknown` не означает поддержку. Нельзя молча преобразовывать `high` в другое значение. В v1 отсутствие требуемой model/reasoning/speed/mode capability блокирует запуск: fallback-rule не может заменить backend, model, reasoning, speed или mode.

Для parameterized models сохранять базовый model ID и overrides раздельно, а не одной непрозрачной строкой.

### 6.4. Аккаунты и multi-account

Для Codex и AGY необходимо поддержать несколько явно разрешённых пользователем account contexts через `star-cliproxy`. AGY profile выбирается `agy-profile` одного Windows-пользователя. Codex profile задаётся отдельным абсолютным `CODEX_HOME`, независимо авторизованным пользователем. Ни один из механизмов не использует OpenCode. Маршруты Mirasim не наследуют доказанность account context от `star-cliproxy`: их account/route pin и observed evidence проверяются отдельно по §6.11b.

Каждый account имеет:

- стабильный локальный ID и display name;
- provider/profile-native ID, если доступен;
- auth state;
- manual priority;
- enabled flag;
- health state;
- quota snapshots по bucket/model family;
- cooldown и `disabledUntil`;
- текущие session bindings;
- лимит одновременных executions;
- пользовательские reserve thresholds.

Credentials AGY остаются в хранилище `agy`/`agy-profile`; Codex credentials остаются внутри выбранного `CODEX_HOME`; proxy secrets хранятся как secret references. Приложение не изменяет глобальные `HOME`/`USERPROFILE`, не копирует `auth.json`, AGY profiles или токены и не выводит их в UI/логи/arguments.

#### Контракт Codex/AGY account context bridge

- Routing Engine приложения — единственный компонент, выбирающий account/route для GUI-managed session.
- Transport bridge обращается к Codex/AGY только через `star-cliproxy`; прямой CLI runner может существовать лишь как временный migration code или диагностический spike, но не как product route.
- Для AGY bridge использует `agy-profile list/current/switch`; выбор выполняется под общим account/checkout lock после завершения связанных executions. `-Force`, `next`, `random` и перенос credentials запрещены.
- Для Codex bridge выбирает заранее созданный абсолютный `CODEX_HOME`. Каждый context авторизуется отдельно; изменение context и перенос native session между каталогами запрещены.
- Bridge подтверждает provider, account context, native session/conversation ID, model/variant и фактический route по evidence proxy/CLI. Если account доказать нельзя, automatic modes запрещены и route остаётся opaque/ManualOnly.
- Для каждого execution сохраняются `requestedRoute` и `observedRoute`. Их несовпадение завершает execution как `RouteMismatch`, запрещает продолжение binding и не считается success.
- Phase 5R фиксирует контракт `star-cliproxy`, Codex `CODEX_HOME`, `agy-profile` и sanitized fixtures. Запрещено читать undocumented credential files ради реализации bridge.

### 6.5. Routing engine

Поддержать режимы:

- `Pinned` — точный account/route;
- `SessionSticky` — выбрать immutable binding один раз и держать до Reset/Close; при потере eligibility только предложить replacement session;
- `QuotaFirst` — максимальный безопасный остаток;
- `PriorityFirst` — пользовательский порядок с учётом eligibility;
- `Balanced` — квота + здоровье + нагрузка + latency;
- `ManualOnly` — никогда не переключаться автоматически.

Приоритет источников binding:

1. явный выбор пользователя для создаваемой session;
2. binding логической роли активной workflow version;
3. default policy проекта;
4. глобальная default policy приложения.

Источник выбора сохраняется в routing decision и показывается пользователю.

Eligibility gates применяются до scoring:

- route enabled;
- authentication valid;
- model доступна этому account;
- требуемые reasoning/speed/mode поддерживаются;
- route не quarantined;
- quota gate проходит строго по mode-specific правилам §6.6;
- concurrency slot доступен;
- data classification проекта не превышает `maxDataClass` route.

Минимальная data-sensitivity model v1:

- `PublicSource` — допустим любой явно подключённый route;
- `PrivateSource` — допустим только route, которому пользователь явно разрешил private source; это default для нового проекта;
- `Restricted` — автоматическая отправка внешнему endpoint запрещена; каждый sanitized fragment требует отдельного preview/подтверждения.

Custom provider по умолчанию имеет `maxDataClass=PublicSource`, пока пользователь явно не изменит trust. Credentials и обнаруженные secrets не передаются ни при каком уровне.

Настраиваемый score применим только после gates. Default `Balanced` score:

```text
0.30 * trustedQuotaScore
+ 0.25 * healthScore
+ 0.15 * priorityScore
+ 0.10 * loadScore
+ 0.10 * latencyScore
+ 0.10 * reserveScore
```

Каждая компонента нормализована в диапазон `0..100`; формулы нормализации, веса и использованные snapshot IDs сохраняются с routing decision. Default weights редактируются пользователем, но сумма должна равняться 1. Для `QuotaFirst` сортировка идёт по fresh trusted remaining, для `PriorityFirst` — по manual priority. Общий tie-break: manual priority, затем trusted remaining, затем стабильный меньший account ID.

Компоненты определяются так: `trustedQuotaScore` — remaining/total конкретного fresh bucket; `healthScore` — deterministic mapping health state + recent success/error; `priorityScore` — нормализованный manual priority; `loadScore` — свободные concurrency slots; `latencyScore` — обратная нормализация EMA внутри provider; `reserveScore` — расстояние от hard reserve с учётом близости reset. Стоимость/«дорогой резерв» учитывается только отдельной явно включённой policy и при достоверных cost данных, а не скрытым коэффициентом. Session affinity является gate: подтверждённая sticky session не пересчитывает route.

Каждый автоматический выбор должен иметь human-readable explanation: выбранный route, источник policy, отклонённые кандидаты и gates, quota snapshot ID, health evidence и tie-break.

Session binding — кортеж `(backend, providerProfile, account, model, reasoning, speed, mode)`. После подтверждения native session ни одно поле binding не меняется. Любое изменение создаёт новую local/native session с ancestry `RouteChange` и видимой discontinuity. Если backend не поддерживает native fork, действие называется `New session`, а не `Fork`.

Failover правила:

- до отправки первого prompt автоматические режимы могут выбрать другой eligible account;
- после подтверждения session или возможной доставки prompt автоматическая пересылка запрещена;
- `Pinned` и `ManualOnly` никогда не выполняют auto-failover;
- `SessionSticky` при auth invalid, quarantine, hard-reserve violation или отсутствии concurrency slot останавливает следующий turn и предлагает создать replacement session; prompt не отправляется до подтверждения пользователя;
- failover не меняет backend, provider profile, model, reasoning, speed или mode;
- `Ambiguous` execution никогда не пересылается автоматически и не расходует workflow retry budget.

### 6.6. Квоты и остатки

Dashboard обязан содержать строку для каждого подключённого account/model family, даже если provider не раскрывает квоту.

Состояния значения:

- `ExactProviderReported`;
- `PluginReported`;
- `LocallyCalculated`;
- `Estimated`;
- `Stale`;
- `Unsupported`;
- `Unknown`;
- `Error`.

Показывать:

- used/remaining и единицы;
- окно лимита;
- reset time с timezone;
- источник;
- время последнего успешного обновления;
- confidence;
- следующий refresh;
- ошибку последнего refresh без секретов.

Нельзя отображать выдуманные проценты. Для provider без quota API допускается ручной лимит и локальный usage counter, но он явно помечается как локальный/оценочный.

Числовые hard reserve и automatic quota scoring используют только `ExactProviderReported` и `PluginReported` snapshots в пределах TTL. Default TTL — 5 минут, если provider не сообщает собственную актуальность; значение настраивается per source. `LocallyCalculated` и `Estimated` могут участвовать только после явного opt-in пользователя и никогда не ранжируются выше fresh trusted snapshot. `Unknown`, `Unsupported`, `Stale` и `Error` не имеют числового значения и не изображаются как 0% или 100%.

`QuotaFirst` и `Balanced` по умолчанию отклоняют route без fresh trusted quota. `Pinned`, `ManualOnly` и `PriorityFirst` могут использовать явно выбранный route с предупреждением о недостоверной quota. Hard reserve сравнивается в единицах конкретного bucket; отсутствующий bucket не равен нулю. Dashboard и Router обязаны ссылаться на один `quotaSnapshotId`.

Quota polling должен иметь jitter, backoff и per-provider rate limit. Пользователь может выполнить `Refresh now`, но UI должна предотвращать бессмысленный частый polling.

### 6.7. Сессии

Для каждой логической сессии хранить:

- local session ID;
- полный immutable binding `(backend, providerProfile, account, model, reasoning, speed, mode)`;
- project/workspace;
- native backend session ID;
- время создания и последнего события;
- session state и reconciliation outcome;
- continuation/fork ancestry;
- workflow run и role, если применимо;
- active execution;
- close/reset reason.

Session и Execution имеют разные state machines.

#### Session state machine

| From | To | Trigger/guard | Side effects |
|---|---|---|---|
| — | `Draft` | создана local session, native ID отсутствует | binding выбран, prompt ещё не отправлен |
| `Draft` | `Starting` | начато создание native session | создаётся первый Execution и `clientRequestId` |
| `Starting` | `Active` | получен и провалидирован native session ID и observed binding | session считается подтверждённой |
| `Active` | `Idle` | нет нетерминального Execution | разрешён `Continue` |
| `Idle` | `Active` | начат новый Execution на неизменном binding | native session сохраняется |
| `Starting`/`Active`/`Idle` | `Ambiguous` | prompt мог быть доставлен, но terminal evidence/session liveness неизвестны | auto-retry запрещён |
| `Starting`/`Active`/`Idle` | `Orphaned` | backend/process исчез и доставку prompt можно исключить либо native session нельзя reattach | требуется recovery decision |
| любое нетерминальное | `Closed` | явный Reset/Close либо подтверждённо невосстановимый binding | история сохраняется, новый turn создаёт новую session |
| `Ambiguous`/`Orphaned` | `Active`/`Idle` | reconciliation вернул `Reattached` и совпал полный binding | записывается recovery evidence |
| `Ambiguous`/`Orphaned` | `Closed` | пользователь закрывает либо backend подтвердил отсутствие session | причина фиксируется |

`Starting` не означает доставку prompt. `Active` допустим только после native session evidence. Reset закрывает старую session; следующая отправка создаёт новую `Draft` session.

#### Execution state machine

| From | To | Trigger/guard |
|---|---|---|
| — | `Queued` | пользователь или workflow создали execution |
| `Queued` | `Starting` | получены checkout/route/concurrency locks |
| `Starting` | `SessionConfirmed` | native session ID и binding подтверждены |
| `SessionConfirmed` | `Running` | prompt принят transport/backend либо получено первое execution event |
| `Running` | `WaitingApproval` | backend запросил approval |
| `WaitingApproval` | `Running` | approval разрешён |
| `Starting`/`SessionConfirmed`/`Running`/`WaitingApproval` | `Cancelling` | пользователь/timeout запросили cancel |
| любое нетерминальное | `Succeeded` | получено нормализованное terminal success event |
| любое нетерминальное | `Failed` | получено однозначное terminal failure event до/после доставки |
| любое нетерминальное | `TimedOut` | turn hard timeout; delivery status известен |
| `Cancelling` | `Cancelled` | backend подтвердил cancel/termination |
| любое нетерминальное | `Ambiguous` | prompt мог быть доставлен, но terminal evidence отсутствует |
| любое нетерминальное | `RouteMismatch` | observed route не совпал с requested route |

После cancel/timeout session возвращается в `Idle` только если backend подтверждает её жизнеспособность; иначе session становится `Ambiguous`. User cancel, approval deny и `Ambiguous` не увеличивают circuit breaker и не расходуют workflow retry budget.

#### Identity, retry и reconciliation

- Каждый turn имеет UUID `clientRequestId`, canonical `promptHash`, `localSessionId`, requested route и, если backend предоставляет, native request ID.
- Автоматический повтор одного `clientRequestId` запрещён. Ручной повтор создаёт новый Execution со ссылкой `retryOfExecutionId`, показывает риск двойного расхода/действия и требует подтверждения.
- `Ambiguous` устанавливается, в частности, при обрыве после отправки, timeout после первого model event, выходе transport без terminal event и restart backend в середине turn.
- После перезапуска GUI reconciliation возвращает один из исходов: `Reattached`, `Orphaned`, `Ambiguous`, `BackendMissing`. `Reattached` разрешён только при совпадении native session ID и полного immutable binding.
- `Continue` использует ту же native session и неизменный binding.
- `Fork` показывается только когда backend capability подтверждает native fork. Иначе доступно `New session` с ancestry, но не ложный fork.
- Второй нетерминальный Execution в одной session запрещён, пока capability probe и отдельная policy явно не подтвердят backend queue.

### 6.8. Выполнение и live status

Для каждого execution отображать:

- точное Execution state из §6.7;
- requested и observed route;
- reasoning/speed;
- elapsed time;
- время последней активности;
- текущий stage/tool/command/file, если backend сообщает;
- число tool calls;
- usage, если backend сообщает;
- stdout/stderr/event stream в диагностической панели;
- pending approval;
- итог и exit classification.

Execution Supervisor состоит из двух уровней.

**Process Supervisor** управляет долгоживущими `opencode serve`, управляемым `star-cliproxy`, `cursor-agent acp` и отдельным legacy entrypoint:

- запускает процессы без видимого консольного окна;
- передаёт executable и каждый аргумент раздельно через типизированную модель запуска (`ProcessStartInfo.ArgumentList` или эквивалент), не собирает shell-команду интерполяцией строк;
- не создаёт вложенные `powershell.exe -Command`/`pwsh -Command`; дополнительный shell допускается только как явно объявленная capability конкретного adapter с фиксированным скриптом, именованными параметрами и тестами quoting;
- для неинтерактивных запусков закрывает stdin либо подключает его только к явному protocol transport; отсутствие обязательного аргумента не должно переводить child process в скрытое ожидание пользовательского ввода;
- читает stdout/stderr без deadlock;
- имеет process startup timeout, liveness, restart policy и orphan reconciliation;
- не применяет turn hard timeout к живому server/ACP process;
- завершает только своё подтверждённое дерево процессов;
- никогда не пишет heartbeat в stdin протокола.

Нормализованная спецификация процесса хранит отдельно executable, argument list, working directory, stdin policy, environment allowlist, ожидаемый protocol/session evidence и безопасное отображение команды. Секреты не попадают в отображение или журнал. Произвольный shell text, `Invoke-Expression` и повторный разбор уже сформированной командной строки запрещены.

**Execution Watchdog** управляет одним turn:

- имеет отдельные session-confirmation timeout, hard timeout и cancellation;
- считает завершением только нормализованное terminal event;
- отслеживает process liveness и transport/event activity раздельно;
- трактует тишину модели как наблюдение, а не автоматический failure;
- различает process started, session confirmed, prompt delivery и terminal outcome;
- классифицирует живой процесс без session/protocol evidence при закрытом stdin как startup/orchestration failure, а подтверждённое ожидание интерактивного ввода как `InteractiveInputWait`; такой исход не считается model failure и не расходует model retry budget;
- не запускает дубликат автоматически при неизвестной доставке.

События после redaction непрерывно сбрасываются в run directory, а UI получает bounded in-memory window. При невозможности записывать события и заполнении буфера фиксируется причина `BufferOverflow`; отдельного состояния Execution с именем `BufferOverflow` нет. `Failed` допустим при доказанном отсутствии dispatch либо подтверждённом terminal failure. После возможной доставки без terminal evidence используется `Ambiguous`, ownership сохраняется, автоматический retry запрещён. Чтение transport не останавливается молча, terminal events не отбрасываются. Graceful cancel выполняется первым, затем допускается bounded forced termination только соответствующего execution/process tree с последующей reconciliation session.

### 6.9. Approvals и права

Поддержать нормализованные запросы:

- read file;
- write/edit file;
- shell command;
- network/MCP/tool;
- расширение workspace;
- destructive/high-risk action.

UI показывает исходный запрос backend и нормализованное объяснение. Возможные ответы: allow once, allow for execution, deny. Постоянное правило создаётся только через отдельный экран с подтверждением.

Режим `auto approve` не включается по умолчанию. Настройки OpenCode и Cursor не должны автоматически считаться эквивалентными: adapter переводит только доказанно совместимые capabilities.

Каждый adapter публикует mapping `native approval kind → normalized kind → поддерживаемые ответы`. Неизвестный native kind отображается как `UnknownHighRisk` и требует deny либо разового явного разрешения; он не попадает под постоянные правила. Persistent approval rule содержит backend/provider scope, project/path scope, operation, срок действия и автора. Создание, изменение и удаление таких правил выполняется только на экране Approval Rules и записывается в audit.

### 6.10. Health и quarantine

Health ведётся раздельно для:

- executable/backend;
- provider profile;
- account;
- model route;
- native session.

Нормализованные классы ошибок:

- executable missing/version incompatible;
- startup/session creation;
- authentication/refresh;
- quota/rate limit;
- network/timeout;
- provider 4xx/5xx;
- model unavailable/mismatch;
- malformed protocol/event;
- tool/permission;
- shell composition/quoting;
- unexpected interactive input wait;
- workspace conflict;
- user cancellation;
- unknown/ambiguous completion.

Circuit breaker:

- `Healthy`;
- `Degraded`;
- `CoolingDown`;
- `QuarantinedAuto`;
- `DisabledManual`;
- `ProbeRequired`;
- `Recovering`.
- `ForcedEnabled`.

Default test policy: `N=3` одинаковые учитываемые ошибки в rolling window `W=15 минут` переводят route в `CoolingDown` на `D=5 минут`, затем в `ProbeRequired`, но не в `Healthy`. Параметры настраиваются per provider, а эти значения используются как нормативные defaults для тестов.

| From | To | Trigger |
|---|---|---|
| `Healthy` | `Degraded` | одиночная учитываемая transient error или ухудшение latency/error rate |
| `Healthy`/`Degraded` | `CoolingDown` | достигнут threshold N/W |
| `CoolingDown` | `ProbeRequired` | истёк cooldown D либо пользователь запросил clear cooldown; действие не подтверждает recovery |
| `ProbeRequired` | `Recovering` | пользователь подтвердил отдельный pinned probe |
| `Recovering` | `Healthy` | probe успешно подтвердил auth, model и минимальный turn/operation |
| `Recovering`/`ProbeRequired` | `QuarantinedAuto` | probe failed либо повторена однородная ошибка |
| любое | `DisabledManual` | пользователь выключил route |
| `DisabledManual` | `ProbeRequired` | пользователь снова включил route |
| `CoolingDown`/`ProbeRequired`/`QuarantinedAuto` | `ForcedEnabled` | пользователь принудительно разрешил route без успешного probe |
| `ForcedEnabled` | `ProbeRequired` | пользователь потребовал проверку; состояние больше не допускает обычный turn |
| `ForcedEnabled` | `Healthy` | подтверждённый pinned probe установил auth, model и успешный минимальный turn/operation |
| `ForcedEnabled` | `CoolingDown` | учитываемая ошибка достигла threshold либо немедленный auth block |
| `ForcedEnabled` | `Recovering` | пользователь подтвердил отдельный pinned probe |

Auth failure немедленно блокирует все routes account. Model mismatch блокирует только конкретный route. User cancel, approval deny и `Ambiguous` не увеличивают breaker. Из account cooldown и route health применяется более строгое состояние.

`Degraded` остаётся sticky после обычного успешного turn и истечения rolling window: это не proof recovery.
Для него и `QuarantinedAuto` явное disable/enable переводит route в `ProbeRequired`, затем подтверждённый
pinned probe — в `Recovering` и при успехе в `Healthy`. Другой явный путь из quarantine —
`ForcedEnabled` → `Recovering` через подтверждённый model probe. Прямой probe из `QuarantinedAuto`
не допускается текущим admission-контрактом; quarantine eligibility exception действует для уже
допущенного probe, а не создаёт отсутствующий переход. Результат без admission authority не меняет health.
«Более строгое» означает совместное выполнение gates provider/account/model/route, а не порядок enum:
account auth block/cooldown нельзя обойти force-enable конкретного route. Отдельный opt-in для
automatic routing не заменяет успешный probe и не отменяет account gates.

Probe — отдельный pinned Execution, не продолжает пользовательскую session, освобождён от quarantine eligibility gate, показывает возможный расход и требует подтверждения. Пока route в `CoolingDown`, `QuarantinedAuto` или `DisabledManual`, следующий turn существующей sticky session останавливается с объяснением; replacement не создаётся автоматически.

Действия пользователя:

- disable/enable;
- `Test connection`;
- `Probe model` с preview возможного расхода;
- clear cooldown;
- return to routing pool после успешной проверки;
- оставить route выключенным.

Нельзя вернуть route в `Healthy` только нажатием кнопки без проверки. `ForcedEnabled` визуально отличается от `Healthy` и участвует в automatic routing только после отдельного opt-in пользователя.

### 6.11. OpenCode integration

Основной transport — управляемый локальный `opencode serve`. Phase 0 ADR не выбирает другой primary transport, а фиксирует server API, недостающие операции и безопасную topology.

Default topology OpenCode — отдельный backend instance на provider profile. OpenCode обслуживает только OpenCode-native/custom providers; Codex и AGY не добавляются в его provider/plugin configuration и не запускаются этим adapter.

Adapter должен:

- выбрать свободный loopback port;
- не публиковать server на внешнем интерфейсе по умолчанию;
- обнаружить version;
- получить provider/model/session inventory;
- создать и продолжить session;
- отправить prompt и attachments;
- получать streaming events;
- нормализовать tool calls, approvals, usage и errors;
- поддержать cancellation;
- экспортировать session для диагностики;
- корректно пережить restart server;
- сохранять requested и observed account/model/variant evidence для каждого turn;
- поддержать разрешённые OpenCode plugins без предположения, что все plugins имеют одинаковый quota API; любые plugins/providers Codex или AGY не загружать для управляемых приложением маршрутов.

Если необходимая операция отсутствует в server API, разрешён узкий CLI fallback через machine-readable output. Такой execution явно помечается `DiagnosticCliFallback`, виден пользователю и не продолжает обычную server session. Парсинг TUI/ANSI запрещён. Замена serve-first transport требует отдельного change proposal.

Нельзя использовать OpenCode как transport, plugin host или account bridge для Codex/AGY и читать undocumented credential files ради получения токенов.

### 6.11a. star-cliproxy integration для Codex/AGY

Adapter запускает отдельный локальный `star-cliproxy` instance либо подключается к явно настроенному loopback instance, проверяет health/model catalog и обращается к его OpenAI-compatible API. Для Codex/AGY обязательны streaming, bounded timeout, cancellation, provider/model/session evidence и owned-process-tree termination. HTTP 2xx, exit code, живой PID или частичный stream сами по себе не означают успешный execution. Никакого fallback через OpenCode.

Для AGY account selection adapter использует документированный интерфейс [`agy-profile`](https://github.com/haclongkim/agy-profile) под тем же Windows-пользователем. `list/current/switch` выполняются только под единым account/writer lock и при отсутствии живых связанных executions. `-Force`, `next`, `random`, перенос DPAPI-профилей, копирование credential-файлов и чтение/логирование токенов запрещены. После switch обязательны новый proxy/native session и повторная проверка active profile.

Для Codex каждый account имеет отдельный абсолютный `CODEX_HOME`. Каталог создаётся и авторизуется пользователем независимо; приложение не копирует `auth.json` и не меняет глобальные `HOME`/`USERPROFILE`. Выбранный `CODEX_HOME` передаётся только owned proxy/CLI process environment. Если актуальный `star-cliproxy` не позволяет доказанно изолировать несколько contexts в одном процессе, используется отдельный managed instance/config/loopback port на account.

`star-cliproxy` и `agy-profile` — сторонние проекты. Их доступность, версия и фактический Windows-контракт проверяются до включения. Если provider/account/route невозможно подтвердить без секрета, automatic multi-account routing отключается, а UI явно показывает ограничение. Квоты отображаются только при независимом подтверждённом источнике; неизвестное значение не выводится как число.

### 6.11b. Mirasim integration

Mirasim подключается как самостоятельный backend к явно выбранному loopback host. Runtime probe получает версию, auth/transport capability, доступные харнесы и каталоги моделей. Route включает `Mirasim instance + harness + model + route leg + account context`, а session хранит Mirasim session key и native turn/task ID. UI отличает установленный харнес от модели: K3, GLM и DeepSeek могут быть моделями внутри выбранного харнеса, но не объявляются отдельными backend без собственного protocol evidence.

Для product prompt требуется канал, передающий содержимое без секретов и пользовательского текста в process arguments, URI и логах LLMWorkGUI. Команда `ui-cli prompt --text` с prompt в argv допускается только для несекретного диагностического probe. Пока безопасный поддерживаемый канал не подтверждён, доступны discovery и status, а выполнение имеет `Unsupported`. Приложение не читает внутренние credential/token-файлы Mirasim и не закрепляет путь к упакованному `server.cjs` как постоянный API.

Adapter создаёт, продолжает и наблюдает сессии, нормализует stream, terminal outcome, errors, usage, approvals и cancellation. `phase=done` с `error` либо `incomplete=true` не является успехом. После обрыва после отправки применяется `Ambiguous` без автоматического повтора; cancel требует terminal confirmation и reconciliation. Неподтверждённые `account` или фактический route leg показываются `Not reported`; такой route остаётся `ManualOnly` и не участвует в automatic multi-account или silent fallback. Доказанное несовпадение requested/observed route даёт `RouteMismatch`.

LLMWorkGUI не меняет глобальные `relay.enabled`, `relay.always`, активный аккаунт или recording Mirasim ради отдельного хода. Состояние relay `ok`, платный план и список моделей не доказывают доступность конкретной модели. K3 и GLM принимаются как доступные для автоматического выбора только после отдельных минимальных live calls через выбранный харнес с подтверждённым результатом. Настройка `bypassPermissions` не считается approval capability: обязательный request/reply проверяется отдельно. Mirasim может записывать сырые запросы и ответы по собственным правилам; UI раскрывает эту границу данных и не изменяет её без действия пользователя. Подробнее — ADR-0008 и план приёмки Mirasim backend.

### 6.12. Cursor native integration

Cursor запускается как `cursor-agent acp` и управляется по ACP через `stdio`.

Adapter обязан:

- выполнять initialize/handshake и проверку protocol capabilities;
- создавать/загружать session;
- передавать workspace;
- отправлять prompt и получать streaming updates;
- отображать permission requests;
- поддерживать cancel;
- сохранять native chat/session ID;
- поддерживать доступные Cursor modes;
- получать model list отдельным discovery-вызовом;
- хранить parameterized model overrides отдельно;
- не использовать `--force`/`--yolo` без явно выбранной пользователем policy;
- не извлекать Cursor credentials;
- при падении ACP помечать session как orphaned/failed, не запускать новый turn вслепую.

CLI print-mode допустим только как диагностический fallback, видимый пользователю. Он не должен незаметно заменять ACP.

Если Cursor/ACP не предоставляет quota API, соответствующие строки dashboard имеют `Unsupported` либо `Unknown`; quota не выводится из косвенных признаков. Health Cursor строится из executable, ACP handshake, protocol events и turn outcomes. Effort/speed/modes, не подтверждённые per-model discovery, имеют state `Unknown` и недоступны для отправки.

### 6.13. Workflow library

Пользователь может:

- импортировать ZIP или папку;
- увидеть дерево файлов и README/entrypoint;
- проверить archive safety: path traversal, symlinks, запрещённые абсолютные пути и чрезмерный размер;
- сохранить оригинал как immutable blob;
- присвоить имя, tags и описание;
- выбрать активную version для проекта;
- клонировать version в draft workspace; сохранение draft всегда создаёт новый immutable version ID;
- сравнить любые две версии;
- экспортировать выбранную version без внутренних секретов приложения.

Workflow manifest приложения хранится отдельно от файлов пользователя и содержит:

- workflow ID/version ID;
- original hash;
- source type;
- entrypoints;
- declared roles;
- bindings;
- compatibility report;
- creation/activation metadata.

Отсутствие formal manifest внутри импортируемого workflow не должно мешать хранению и просмотру. Исполнение legacy workflow может использовать user-defined entrypoint и mapping.

Blob каждой version доступен только для чтения. Preview, draft, adaptation и run выполняются на отдельной scratch-копии вне project root и вне blob store. После операции приложение повторно проверяет hash source blob; запись за пределами scratch блокируется. Уже начатый Workflow run навсегда привязан к конкретному version ID, даже если active pointer проекта изменён.

Default import limits v1: ZIP до 100 MiB, распакованный package до 500 MiB, не более 10 000 файлов, один файл до 20 MiB, compression ratio одного entry не более 100:1. Limits настраиваются администратором приложения; превышение требует отдельного override preview и никогда не отключает Zip Slip/symlink/path проверки.

### 6.14. Адаптация workflow моделью

Команда `Адаптировать под подключённые модели` запускается только пользователем.

Adaptation работает в scratch вне project root и blob store, не получает project root как writable workspace и не берёт checkout writer lock. Исходная version открывается только для чтения.

До отправки показывается preview данных:

- выбранная source workflow version;
- модель-адаптер и её endpoint/account;
- список передаваемых файлов;
- результат статического secret scan до отправки; найденные файлы по умолчанию исключены;
- sanitized provider/model capability catalog;
- quota не передаётся целиком, если она не нужна; credentials никогда не передаются;
- цель адаптации: экономия токенов, качество, скорость или пользовательский баланс;
- оценка расхода/стоимости либо явное `Unknown`, состояние quota и reserve threshold выбранного route.

Модель получает задачу:

- сохранить назначение и структуру workflow;
- менять только model/provider bindings и необходимые команды запуска/config artifacts;
- назначить доступные модели на существующие роли executor/reviewer/escalation с учётом capabilities и выбранной цели адаптации;
- не менять роли, этапы, правила качества и escalation semantics без отдельного разрешения;
- объяснить каждую замену;
- отметить невозможные сопоставления;
- не активировать результат.

Результат сохраняется как candidate version. Приложение выполняет:

- проверку целостности package;
- schema/format validation, где доступно;
- проверку ссылок на реально обнаруженные models/capabilities;
- статический поиск секретов;
- полный file diff;
- semantic summary: role → old route → new route;
- список warnings/blockers.

Изменение назначений исполнителя/reviewer/escalation model является допустимым binding change. Изменение самих ролей, stages, quality gates или escalation semantics является blocker, пока пользователь отдельно не выбрал расширенный scope адаптации. Перед активацией compatibility проверяется заново против текущего provider/model/account/health catalog. Удалённые routes, недоступные capabilities, найденные secrets и semantic blockers не позволяют обычную активацию; каждый сознательно принимаемый blocker требует отдельного подтверждения.

Кнопки: `Принять и активировать`, `Сохранить кандидатом`, `Запросить доработку в той же сессии`, `Отклонить`. Rollback переставляет active pointer на любую ранее активную version и не изменяет blobs. Оригинал остаётся неизменным.

### 6.15. Workflow execution

Движок не должен hard-code конкретный приложенный workflow. Declarative node имеет `nodeId`, `type`, typed inputs/outputs, route/role binding, timeout, permission intent, retry budget, success/failure transitions и artifact contract. Граф проходит validation: один entry node, существующие targets, достижимый terminal outcome, отсутствие необоснованных бесконечных циклов.

Поддерживаемые primitive nodes:

- prompt/model turn;
- read-only review;
- writer execution;
- approval gate;
- condition;
- retry с бюджетом;
- escalation;
- user decision;
- validation command;
- artifact/diff collection;
- terminal outcome.

Declarative `prompt`, `review` и `writer` nodes обращаются к OpenCode adapter, Cursor ACP adapter, `star-cliproxy` adapter или Mirasim adapter только по проверенным capabilities выбранного маршрута. Единый интерфейс выбора модели охватывает все доказанные маршруты; наличие модели в каталоге само по себе не разрешает автоматическое выполнение. Retry создаёт новый Execution, сохраняет ссылку на предыдущий и соблюдает запрет автоматического повтора `Ambiguous`.

Legacy workflow допускается исполнять только при наличии явно выбранного пользователем entrypoint. Legacy adapter не вызывает Codex или AGY в обход `star-cliproxy` adapter: он запускает entrypoint как один supervised opaque process в scratch-копии под checkout writer lock, а все дочерние процессы входят в его process tree. Legacy execution не получает выдуманные native session, route, role или stage. Пока entrypoint не сообщает поле по проверенному контракту, UI показывает `Not reported`, process state, exit code и найденные artifacts.

#### Workflow разработки и редактор схем

Пользователь может создать workflow с нуля, открыть встроенный или собственный шаблон, клонировать и редактировать его, сохранить новую immutable version, назначить её проекту и запустить. В редакторе задаются этапы, роли, входные и выходные артефакты, критерии готовности, условные переходы, повторные проверки, лимиты повторов, правила эскалации, подтверждения пользователя и терминальные исходы. Черновик проходит проверку схемы до запуска; изменение шаблона не меняет уже запущенный run. Импортированный `WORKFLOW.ZIP` остаётся отдельной неизменяемой версией и не превращается в обязательный встроенный шаблон.

Первоклассный сценарий разработки покрывает цепочку `идея/запрос → постановка задачи → архитектура → ТЗ → дорожная карта → проверка документов → утверждение → пакеты реализации → код/UI → многоуровневое ревью → тесты/визуальная приёмка → итог`. Этапы редактируемы: пользователь может добавить или убрать шаг, но движок не перескакивает через обязательный gate активной версии. Документ сначала создаётся в draft, затем проверяется назначенными моделями по явно заданным критериям. Утверждение документа и начало реализации требуют указанных в схеме подтверждений; вердикты нескольких reviewer хранятся раздельно вместе с evidence, а не сворачиваются в один безымянный `PASS`. Конфликтующие вердикты направляются в заданный шаг разрешения или пользователю.

Роли задаются независимо от моделей: архитектор, постановщик/автор ТЗ, кодер, UI-кодер, reviewer первого и последующих уровней, тестировщик и утверждающий — встроенные варианты; допускаются пользовательские роли. Для каждого узла указываются роль, требуемые capabilities, основной route и разрешённые fallback routes. Назначение модели на роль можно менять до старта и между этапами; работающая native session сохраняет immutable binding. При смене модели или account context прежняя native session остаётся на исходном аккаунте: она не переносится и не считается fork. После общего lock и проверки отсутствия связанных живых executions по §6.11a run создаёт новую backend session и только application-level ссылку на исходный этап; событие хранит причину, `requestedRoute` и `observedRoute`. Их несовпадение даёт `RouteMismatch`, а не успех. Смена AGY profile и Codex `CODEX_HOME` подчиняется §6.4 и §6.11a; это правило не разрешает менять активный аккаунт, relay или recording пользовательского Mirasim host (§6.11b). Автоматическое переключение допускается только при доказанных account/route capabilities и явной policy, без silent fallback и без повтора `Ambiguous`.

Правила перехода между этапами кодирования включают обязательные артефакты (например, утверждённые ТЗ/roadmap и task packet), результаты проверок, review verdict по каждому уровню, тестовые и UI evidence, scope/diff checks, лимит итераций и условия возврата на исправление. Переход выполняется только по явному условию схемы; пропущенный, неизвестный или противоречивый результат блокирует переход и показывает причину. Writer-узлы одного checkout сериализуются общим lock. Пользователь видит и может подтвердить требуемые решения; отмена и восстановление не создают скрытый новый запуск.

Каждая проверка документа хранит отдельную запись `(reviewerRole, route, documentHash, verdict, evidence)`; `verdict` равен `approve`, `reject`, `request-changes` или `missing`. Проверка неполна, если хотя бы для одного обязательного reviewer активной схемы нет вердикта на текущий hash. Конфликт — любые вердикты на этот hash, не являющиеся единогласным `approve`; большинство голосов не преодолевает `reject`, `request-changes` или `missing`. Следующий узел при конфликте явно назван схемой: шаг разрешения либо пользователь. Утверждение документа фиксирует его hash/version и подтверждение назначенного схемой утверждающего; это отдельное действие, не ответ на tool approval из §6.9. UI evidence — отдельная запись run с hash визуального артефакта или явным подтверждением визуальной приёмки пользователем. Отсутствие такой записи блокирует переход, где UI evidence обязательно.

Шаблоны документов задают структуру, обязательные поля, исходные данные, инструкции модели-получателю, критерии проверки, версию и provenance. Минимальный набор: постановка задачи, архитектурное решение, ТЗ, roadmap, task/fix packet, review и acceptance report. Каждое сохранение шаблона и каждой редакции созданного документа создаёт новый immutable ID и content hash. Этап run хранит точный `DocumentTemplateVersionId` и hash своего draft; утверждение закрепляет тот же hash. Поздняя правка общего шаблона не меняет draft, утверждённый документ или прежний run; удаление используемой версии запрещено. При генерации приложение показывает preview фактически передаваемого контекста и маршрут модели, проводит secret scan, сохраняет draft и историю версий; промпт или шаблон не получает credentials. Документ можно править вручную и повторно направить на проверку. После правки новый hash снова требует предусмотренной схемой проверки.

Визуализация содержит два связанных вида: редактируемую схему шаблона и наблюдаемую схему конкретного run. Для активного run показываются завершённые, текущие, ожидающие, остановленные и заблокированные узлы, фактическая роль/модель/account/session, передачи между ними, артефакты, вердикты, причины переходов и ожидаемое действие пользователя. Данные берутся из `WorkflowRun`/`ObservableRunProjection` и доказанных backend events; неизвестные поля отображаются как `Not reported`. История остаётся доступной после завершения; визуализация не выводит успех workflow из одного успешного execution.

### 6.16. История, артефакты и поиск

Хранить:

- prompts и responses;
- normalized и raw backend events;
- workflow packets;
- diffs и changed-file lists;
- validation results;
- approvals;
- routing decisions;
- health transitions;
- quota snapshots;
- session lineage.

Поддержать полнотекстовый поиск по локальной истории с фильтрами project/provider/model/account/workflow/status/date. В полнотекстовый индекс попадает только redacted content.

Redaction выполняется до любой записи raw events, config preview, process/server logs, search index, crash report и diagnostic bundle. Шифрование at rest является дополнительной защитой и не заменяет redaction. Приложение не обещает очистить сторонние historical logs, созданные до подключения; Phase 0 документирует расположение/политику управляемых OpenCode/Cursor logs и не копирует их без sanitized preview.

Default retention v1:

- project/session/workflow metadata и пользовательские messages — до явного удаления пользователем;
- raw backend events — 30 дней;
- process/server logs — 14 дней;
- diagnostic bundles — 7 дней;
- quota snapshots — 90 дней, с downsampling данных старше 30 дней;
- health/audit transitions — 180 дней;
- immutable workflow versions — до явного удаления, запрещённого для active или referenced run version.

Значения настраиваются раздельно. Cleanup транзакционен, не удаляет active/referenced artifacts и оставляет audit record без чувствительного payload.

---

## 7. GUI/UX

### 7.1. Основной layout

Компактный трёхпанельный интерфейс:

```text
┌ Projects / Workflows ┬ Conversation / Run ┬ Context / Status ┐
│ projects, sessions   │ messages, stages    │ route, quota     │
│ pinned workflows     │ tool calls, diff    │ session, health  │
└──────────────────────┴─────────────────────┴──────────────────┘
```

Требования:

- информационно плотный desktop UI без огромных заголовков и пустых hero-блоков;
- dark/light/system theme;
- keyboard-first navigation;
- scalable 100–200% DPI;
- виртуализация длинных списков и event streams;
- состояния не кодируются только цветом;
- все основные действия доступны без модальных цепочек;
- destructive/high-risk действия требуют ясного подтверждения.

### 7.2. Основные экраны

1. **Workspace** — чат/run, activity, diff, approvals.
2. **Projects** — каталоги, branches, defaults, locks.
3. **Providers & Accounts** — setup, discovery, auth state, plugins.
4. **Models** — capabilities, reasoning/speed, route availability.
5. **Quotas** — все accounts/buckets, freshness и reset.
6. **Sessions** — bindings, native IDs, continue/fork/reset.
7. **Runs** — активные и завершённые executions.
8. **Workflows & Activity Monitor** — import, versions, diff, binding, adaptation, редактор схемы и визуализация фактических ролей и переходов выбранного run. Текущие состояния и бегунок показываются у каждого реально работающего узла, включая параллельные read-only узлы; модель/account/session без доказанного route показываются как `Not reported`. Codex и AGY отображаются как маршруты `star-cliproxy`, а не как фиксированные визуальные роли. Панель деталей строится из observable-run данных без внешних HTTP/скриптов.
9. **Health Center** — failures, quarantine, probes и recovery.
10. **Settings/Diagnostics** — paths, retention, logs, redacted export.

### Язык интерфейса для релиза 1.0

- Русский — язык пользовательского интерфейса по умолчанию при первом запуске и после установки на чистом профиле. Отдельный переключатель языков для 1.0 не требуется.
- На русском отображаются навигация, заголовки и подписи, кнопки, подсказки, onboarding, пустые/загрузочные/ошибочные состояния, диалоги подтверждения, уведомления и пользовательские сообщения об отказах во всех доступных экранах, включая Workflow Studio, Activity Monitor, настройки и установочный сценарий там, где он показывает текст пользователю.
- Имена моделей, провайдеров, CLI, технические идентификаторы, пути, команды, имена файлов, коды ошибок, протокольные поля и цитируемые данные пользователя не переводятся. Их пояснения и доступные пользователю действия формулируются по-русски.
- Текст не должен обрезаться в поддерживаемых темах и масштабах 100/150/200% DPI; важные действия и сообщения должны оставаться понятными без английских подписей. Проверка включает реальные основные экраны и диалоги работающего приложения, а не только снимки из тестовых fixture.

### 7.3. Всегда видимая информация активного route/execution

- provider/account/model;
- reasoning/speed;
- local и native session IDs;
- session confirmation;
- running state;
- quota summary и freshness;
- health indicator;
- project/workspace;
- workflow role/stage;
- причина автоматического routing decision.

До реализации Phase 5 слот quota показывает честное `Unknown` с freshness `Never`; до Phase 7 health indicator отражает только executable/process/protocol state и помечается `Basic health`. Заглушки не изображаются числом или полноценным circuit-breaker state.

### 7.4. Поэтапная поставка наблюдаемости ролей

Базовая наблюдаемость исполнителей и ревьюеров не откладывается до полной workflow orchestration:

- **Phase 1:** доменная observable-run projection и компактная Activity/Role timeline на synthetic fixture. Минимальные поля: роль, display label, execution state, последняя активность, route/session references и evidence source. Synthetic данные всегда визуально помечены и не смешиваются с backend evidence;
- **Phase 3:** timeline подключается к реальным OpenCode session/execution/event stream; отображаются подтверждённые model/account/session, tool calls, approvals, activity и terminal outcome;
- **Phase 6:** тот же контракт наблюдаемости подключается к Cursor ACP без отдельной несовместимой UI-модели;
- **Phase 10:** добавляются полноценный WorkflowRun aggregate, stages/transitions, packets, diffs, validation artifacts, retry/escalation semantics и terminal workflow outcome. Нативный WPF экран строит граф из ролей активной версии workflow и доказанных переходов выбранного run; у каждого реально работающего узла есть свой индикатор, неизвестные route/model/account/session показаны как `Not reported`. Панель деталей использует доменные observable-run данные без внешних HTTP/PS1/скрейпинга журналов.

Ранняя timeline является наблюдаемой проекцией и не запускает следующий этап самостоятельно. До появления фактического события неизвестные role/route/session поля показываются как `Not reported`; PID, ожидаемое назначение роли или synthetic fixture не считаются подтверждением работы конкретной модели.

---

## 8. Хранилище

Минимальные таблицы/aggregates:

- `Projects`;
- `ProviderProfiles`;
- `BackendInstances`;
- `Accounts`;
- `Models`;
- `ModelCapabilities`;
- `Routes`;
- `RoutingPolicies`;
- `ProjectLocks`;
- `QuotaSnapshots`;
- `HealthStates`;
- `HealthEvents`;
- `Sessions`;
- `SessionLinks`;
- `Executions`;
- `ClientRequests`;
- `ExecutionEvents`;
- `Approvals`;
- `ApprovalRules`;
- `WorkflowPackages`;
- `WorkflowVersions`;
- `DocumentTemplateVersions`;
- `DocumentRevisions`;
- `WorkflowBindings`;
- `WorkflowRuns`;
- `Artifacts`;
- `ApplicationSettings`.

Использовать schema migrations. Каждая migration обратимо тестируется на копии БД. Секреты в таблицах запрещены: хранится только secret reference.

`DocumentTemplateVersions` и `DocumentRevisions` содержат immutable ID и content hash. Версия шаблона или редакция документа, на которую ссылается этап run, review либо утверждение, не удаляется и не перезаписывается; защита ссылок не слабее, чем для активной или используемой `WorkflowVersion`.

Large raw logs и workflow blobs можно хранить как файлы в app data с hash/index в SQLite. Запись должна быть atomic: temp file → flush → rename.

---

## 9. Нефункциональные требования

### 9.1. Надёжность

- UI не зависает во время discovery, streaming и quota refresh.
- Авария одного adapter не завершает GUI.
- После перезапуска восстанавливаются проекты, sessions, runs и health-state.
- Неоднозначный backend outcome не маркируется success.
- Все retry bounded и записываются в audit trail.
- Запуск процесса не зависит от shell interpolation: пустые обязательные аргументы, пробелы, кавычки, `$`, Unicode и длинные Windows paths либо передаются без изменения как отдельные аргументы, либо отклоняются до старта с детерминированной ошибкой.
- Неинтерактивный child process не может бесконечно ожидать ввод из унаследованного stdin; отсутствие protocol/session evidence ограничено startup timeout и классифицируется отдельно от сбоя модели.

### 9.2. Производительность

- cold start до рабочего shell: целевой показатель ≤ 3 секунд без ожидания сетевых probes;
- probes выполняются в фоне;
- UI event latency: обычно ≤ 200 мс после получения события;
- 100 000 сохранённых execution events не должны ломать навигацию;
- lists используют paging/virtualization;
- memory не должна бесконечно расти от event stream.

Нормативный synthetic profile: 8 одновременных executions, 50 events/second суммарно в течение 30 минут, 100 000 уже сохранённых events и сообщения до 256 KiB. При этом p95 UI event latency после получения события ≤ 200 мс, UI остаётся интерактивным, а память после завершения и GC не растёт линейно от полного потока.

### 9.3. Безопасность

- никакие credentials не попадают в prompts, logs, crash reports или export;
- process arguments никогда не содержат секреты;
- environment secrets передаются только нужному child process;
- loopback servers не слушают `0.0.0.0` по умолчанию;
- URL и file paths валидируются;
- ZIP import защищён от Zip Slip и decompression bomb;
- HTML/Markdown rendering не исполняет scripts и remote content без разрешения;
- команды показываются пользователю до approval;
- redacted diagnostic bundle имеет preview.

### 9.4. Совместимость и обновления

- adapters имеют declared minimum/maximum tested versions;
- unknown newer version сначала проходит capability probe;
- version mismatch не вызывает silent fallback;
- DB и workflow metadata versioned;
- обновление внешних CLI не выполняется автоматически приложением без команды пользователя.

Release 1.0 поставляется per-user Windows x64 installer; предпочтительный формат — подписанный MSIX, а если Phase 0/12 докажет несовместимость с управлением child processes или protocol transports, используется подписанный bootstrapper/installer с ADR. Приложение поставляется self-contained. Portable build допускается только как отдельный diagnostic artifact. Auto-update приложения не входит в v1; installer поддерживает upgrade и rollback без удаления пользовательской БД. Внешние CLI не включаются в пакет и не обновляются installer.

---

## 10. Логирование и телеметрия

Локальные structured logs:

- correlation IDs: workflowRunId/sessionId/executionId/processId;
- provider/account/model без секретов;
- process lifecycle;
- routing decision;
- health transition;
- quota refresh;
- protocol parse errors;
- user approvals.

Телеметрия продукта по умолчанию выключена. Любая будущая внешняя телеметрия требует opt-in и отдельного документа data inventory.

Retention настраивается отдельно для категорий из §6.16. Очистка не должна удалять active/referenced workflow version или ломать session index.

---

## 11. Ошибки и пользовательские сообщения

Каждая ошибка должна содержать:

- что не удалось;
- какой route/backend затронут;
- могла ли модель получить запрос;
- создана ли native session;
- безопасно ли повторить;
- что приложение сделало с health-state;
- одно рекомендуемое следующее действие;
- ссылку на redacted details.

Запрещены сообщения вида `Something went wrong` без контекста и бесконечные автоматические retries.

---

## 12. Тестирование

### 12.1. Unit

- capability normalization;
- reasoning/speed validation;
- routing eligibility и scoring;
- quota normalization/freshness;
- health transitions/circuit breaker;
- session state machine;
- workflow version immutability;
- ZIP safety;
- secret redaction;
- protocol event normalization.

### 12.2. Contract tests

Для каждого adapter — записанные sanitized fixtures только для capabilities, реально подтверждённых capability matrix:

- handshake;
- model list;
- session creation;
- streaming response;
- approval request;
- cancel;
- auth error;
- quota/rate limit;
- malformed event;
- process crash;
- resume/fork либо fixture явного `Unsupported`.

Отсутствующая backend operation скрыта/disabled в UI и не имитируется. Contract tests не должны расходовать реальную квоту.

### 12.3. Integration

- managed OpenCode server lifecycle;
- реальная session confirmation;
- Cursor ACP handshake;
- restart/reconciliation;
- multiple account routing на test doubles;
- SQLite migration/backup/restore;
- workflow import/adaptation candidate flow;
- process tree cancellation.

### 12.4. UI

#### 12.4.1. Headless / CI-Safe UI Tests
- WPF view-model unit tests, data bindings, converters, state machines, navigation logic;
- запуск без отображения окон и без необходимости в интерактивном рабочем столе;
- быстрый детерминированный прогон в CI-пайплайне.

#### 12.4.2. Наблюдаемый набор визуальных WPF UI-тестов (Visual QuickViewer UI Suite)
- **Интерактивный рабочий стол**: приложение и окна запускаются и реально видимы на разблокированном рабочем столе Windows (interactive desktop session);
- **Наблюдаемость для пользователя**: пользователь воочию видит навигацию по экранам (Three-pane shell, Workspaces, Providers & Accounts, Quotas Dashboard, Health Center, Workflow Studio), открытие и закрытие диалогов, смену светлой и тёмной тем, масштабирование DPI (100%, 150%, 200%);
- **Динамические состояния**: визуальная индикация состояний моделей, провайдеров, аккаунтов, очередей и выполнения workflow (Activity/Role timeline);
- **Детерминированные test doubles**: по умолчанию задействуются детерминированные моки, стабы и локальные fixtures без расходования реальных модельных квот;
- **Скриншоты и артефакты ошибок**: автоматизированные сценарии сохраняют скриншоты ключевых этапов сценариев, а при возникновении сбоев сохраняют скриншот экрана ошибки, дамп UI-дерева и журнал событий;
- **Изоляция от headless/CI**: визуально наблюдаемые тесты строго отделены от быстрых headless/CI-safe тестов отдельной категорией (например, `Category=VisualUi`) или специализированной командой runner;
- **Инкрементальное обновление и регресс**: каждая крупная UI-фаза (Phases 1, 4, 5, 7, 10, 11) обновляет и дополняет сценарии, а финальные фазы (Phase 11 и Phase 12) выполняют полный визуальный регресс всего приложения;
- **Локальный запуск и взаимное исключение**: доступна простая локальная команда запуска; действует строгий запрет на одновременную работу двух GUI-драйверов во избежание конфликтов фокуса ввода и оконных дескрипторов.

### 12.5. Реальные smoke tests

Выполнять только после unit/contract/integration gates и с отдельным test project:

- OpenCode: один минимальный turn, session ID, stream, cancel/resume;
- Cursor ACP: handshake и один минимальный read-only turn;
- multi-account: без расходного запроса там, где quota API позволяет;
- custom provider: локальный mock OpenAI-compatible endpoint.

Отчёт отличает `MODEL_CLAIMED`, `ADAPTER_OBSERVED` и `INDEPENDENTLY_VERIFIED`.

---

## 13. Acceptance criteria релиза 1.0

1. Приложение запускается на чистой Windows user profile после документированной установки prerequisites.
2. OpenCode обнаруживается, управляемый server запускается на loopback, модель и provider inventory отображаются.
3. Пользователь может подключить OpenAI-compatible provider по base URL/key без появления key в файлах и логах.
4. Доступные reasoning/speed options соответствуют discovery; неподдерживаемое значение невозможно отправить молча.
5. Не менее двух accounts одного provider проходят deterministic routing на test doubles; live AGY multi-account claim допускается только после проверки `agy-profile` и native `agy` account/session evidence на двух разрешённых пользователем аккаунтах при последовательных, непараллельных запусках. Отсутствие таких аккаунтов отмечается как непроверенный live-критерий.
6. Dashboard показывает все accounts/model buckets и честный provenance/freshness, включая `Unsupported/Unknown`.
7. OpenCode session создаётся, подтверждается native ID, продолжается и явно сбрасывается.
8. Cursor работает через ACP, показывает native session и поддерживает cancel.
9. Все активные executions видны одновременно с route, model, elapsed, last activity и состоянием.
10. Повторяющиеся учитываемые ошибки переводят route по таблице health-state; manual probe, verified recovery и `ForcedEnabled` различаются и фиксируются в audit trail.
11. `WORKFLOW.ZIP` импортируется без изменения байтов, hash совпадает.
12. Команда адаптации создаёт отдельного кандидата, показывает diff и не меняет активную версию до подтверждения.
13. Исходный workflow можно в любой момент экспортировать byte-identical.
14. После аварийного закрытия матрица `Starting`, `Running`, `WaitingApproval`, `Cancelling`, server restart и ACP crash даёт ожидаемые `Reattached/Orphaned/Ambiguous/BackendMissing` без слепого повтора.
15. Любое изменение account/model/reasoning/speed/backend после подтверждения binding создаёт новую session/discontinuity; observed route mismatch блокирует success.
16. Два writer-capable executions в одном checkout не запускаются; подтверждённый read-only execution может работать параллельно.
17. Один supervised workflow fixture проходит от entry до terminal outcome; UI показывает stage/role/route/session только когда они действительно сообщены, а terminal workflow outcome отличается от результата отдельного executor.
18. Candidate повторно валидируется при активации; source workflow blob остаётся byte-identical после preview, adaptation, run и rollback.
19. Полнотекстовый поиск работает по нормативному набору истории и не индексирует известные secret fixtures.
20. Все тесты (unit, contract, integration, headless UI), а также отдельный наблюдаемый набор визуальных WPF UI-тестов в стиле QuickViewer с сохранением скриншотов и артефактов на разблокированном рабочем столе, performance profile и redacted diagnostic export успешно пройдены и приложены к release report.
21. Пользователь создаёт workflow разработки с нуля или из шаблона, сохраняет изменённую версию и запускает её; изменение шаблона не меняет исходный архив и уже работающий run.
22. Сквозной сценарий от постановки задачи через архитектуру, ТЗ, roadmap, проверку несколькими моделями и утверждение до кода, UI-работы, review и приёмки выполняется с сохранением версий документов, отдельных вердиктов и причин переходов.
23. Неутверждённый документ, неполный review, отсутствующий test/UI evidence или конфликт вердиктов блокирует предусмотренный схемой переход; смена модели, AGY profile или Codex account создаёт новую подтверждённую session и видна в истории.
24. Workflow Studio показывает редактируемый шаблон, а Activity Monitor — фактическое состояние запущенного run с ролями, моделями, передачами, артефактами и честными `Not reported` для неизвестных полей.
25. При первом запуске на чистом профиле пользовательский интерфейс по умолчанию на русском: основные экраны, onboarding, навигация, диалоги, уведомления и ошибки проверены в работающем приложении; технические идентификаторы и пользовательские данные не переводятся.

---

## 14. Не входит в первую версию

- мобильные и web-клиенты;
- облачная синхронизация между компьютерами;
- собственный облачный relay;
- биллинг или продажа доступа к моделям;
- автоматическая покупка/продление подписок;
- извлечение OAuth tokens из чужих credential stores;
- гарантированная точная квота там, где provider её не предоставляет;
- автоматическое изменение валидированного workflow;
- автоматический commit/push как функция продукта; Git-фиксация процесса разработки регулируется §17;
- скрытая замена модели/provider/account;
- одновременное редактирование одного checkout несколькими writers.

---

## 15. Требуемая структура solution

Рекомендуемый baseline:

```text
LLMWorkGUI.sln
src/
  LLMWorkGUI.App/                 WPF composition root
  LLMWorkGUI.Application/         use cases/services
  LLMWorkGUI.Domain/              entities/state machines/policies
  LLMWorkGUI.Infrastructure/      SQLite, secrets, files, processes
  LLMWorkGUI.Backends.Abstractions/
  LLMWorkGUI.Backends.OpenCode/
  LLMWorkGUI.Backends.CursorAcp/
  LLMWorkGUI.Workflows/
tests/
  LLMWorkGUI.Domain.Tests/
  LLMWorkGUI.Application.Tests/
  LLMWorkGUI.Backends.ContractTests/
  LLMWorkGUI.IntegrationTests/
  LLMWorkGUI.Ui.Tests/
docs/
  adr/
  protocols/
  acceptance/
```

Запрещена циклическая зависимость adapters → UI. Domain не зависит от WPF, SQLite и конкретных CLI.

---

## 16. Обязательные документы реализации

- `README.md` с установкой и первым запуском;
- `ARCHITECTURE.md`;
- ADR по OpenCode transport/API;
- ADR по Cursor ACP transport;
- `SECURITY.md` и threat model;
- `DATA_STORAGE.md`;
- `PROVIDER_PLUGIN_GUIDE.md`;
- `WORKFLOW_PACKAGE_GUIDE.md`;
- `DIAGNOSTICS.md`;
- `TESTING.md`;
- release acceptance report с фактическими evidence.

---

## 17. Правила для модели-реализатора

- Сначала выполнить Phase 0 из `ROADMAP.md`; не начинать весь продукт одним большим diff.
- Не менять это ТЗ без отдельного change proposal.
- Не изменять и не «улучшать» пользовательский workflow.
- Не считать mock/fixture доказательством реальной совместимости CLI.
- Не считать запущенный процесс подтверждением model session.
- Не скрывать unsupported quota/capability.
- Не добавлять fallback, которого нет в утверждённой policy.
- Не хранить secrets в репозитории.
- После каждого этапа предоставить scoped diff, тесты, ограничения и обновление roadmap evidence.
- Зеленый build не заменяет runtime и визуальную проверку.
- Исходный контракт Phase 0–12 требовал отдельный read-only `PHASE_GATE_REVIEW` через Cursor Agent на точной модели `grok-4.7-high`: phase-scoped diff, exit criteria, acceptance evidence, тесты, ADR и риски; закрытие Phase требует `PHASE_VERDICT: PASS`. Для текущего завершения проекта пользователь явно поручил полное ревью в Grok 4.6 High и Space Bunny через OpenCode. Применяются эти заданные модели и транспорт с проверяемыми receipts; результаты полного ревью не подменяют отсутствующие phase/owner/native acceptance доказательства.
- DeepSeek, Muse, Grok и Codex не выполняют `git add/commit/push`. Gemini может создать task commit только после полного `ACCEPT`, проверки scope/baseline, `git diff --check` и required checks.
- После успешного phase gate Gemini создаёт phase baseline commit либо annotated tag, записывает hash, Grok session/chat ID, verdict и reviewed baseline в `PROJECT_STATE` и phase acceptance report. Незавершённые, заблокированные, зависшие и ожидающие review изменения не коммитятся.
- `push`, создание remote, публикация и переписывание истории выполняются только по отдельной явной команде пользователя.

---

## 18. Источники и baseline проверки

Перед реализацией перепроверить актуальные контракты:

- OpenCode CLI/server/ACP документацию и фактический установленный `--help`;
- Cursor Agent CLI/ACP документацию и handshake;
- документацию и исходный код [`star-cliproxy`](https://github.com/starhunt/star-cliproxy), включая provider config, lifecycle, API, streaming и environment isolation;
- официальную документацию Codex CLI, включая `CODEX_HOME`, app-server/exec и session semantics;
- документацию стандартного `agy` CLI и [`agy-profile`](https://github.com/haclongkim/agy-profile) с проверкой зафиксированной версии/commit и фактического `help`;
- документацию разрешённых quota providers, не переключающих аккаунты AGY/Codex;
- фактические provider terms и ограничения передачи данных.

Команды и версии из capability snapshot являются наблюдением от 2026-09-22, а не вечным API-контрактом.
