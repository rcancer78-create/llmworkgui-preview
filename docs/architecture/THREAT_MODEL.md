# LLM Work GUI — Threat Model

- **Статус:** Accepted как нормативная модель угроз; release security gate OPEN. Последний проверенный пакет — rc.20261002.3, 4196/4196 тестов; итоговое принятие безопасности отдельно.
- **Дата:** 2026-09-22
- **Ревизия:** 2026-09-25 (TASK-062, Phase 12 Milestone 12A) — покрытие Mirasim, StarCliProxy, Workflow Studio, Activity Center, Pre-Coder Gate и hardening-подсистем Phase 12A (diagnostic bundle, backup/restore, crash/reboot recovery, CLI version mismatch)
- **Задача:** TASK-005 (Phase 0 — discovery и архитектурные контракты)
- **Нормативные ссылки:** `TECHNICAL_SPECIFICATION.md` §2.1, §4.1-4.2, §6.1-6.2, §6.5, §6.9-6.10, §6.13, §6.16, §8, §9.3, §10, §14; `ROADMAP.md` Phase 0
- **Связанные документы:** `docs/architecture/DATA_FLOW.md`, `docs/adr/ADR-0005-secret-storage-windows.md`, `docs/adr/ADR-0006-workflow-blob-and-version-storage.md`

> Актуализация 01.10.2026: rc.7 diagnostic-export lifetime, rc.8 backup/restore и rc.9 retention boundary приняты отдельными source reviews. Static reparse/path checks не являются atomic hostile-retargeting sandbox. Native identity protocol, reboot/live backend crash, свежий Windows user profile и полная release security acceptance остаются OPEN. Audit helpers пересмотрены после Space Bunny MAX: [hash binding и границы scan](../work/REPORT_AUDIT_BINDING_20261001.md). Bounded проверки не меняют нормативные контрмеры ниже.

## 1. Назначение и область

Документ фиксирует нормативную модель угроз приложения LLM Work GUI: границы доверия, классы данных, STRIDE-анализ и обязательные контрмеры. Контрмеры из §5 обязательны для Phase 1+; отклонение от них требует нового ADR.

Актуальный статус аудита: [security gate](../../artifacts/remaining-work-20261002/security-snapshot/STATUS.md).
Формат диагностических маркеров уточнён принятым [ADR-0010](../adr/ADR-0010-diagnostic-redaction-markers.md)
и непосредственно в §5.1.2. Исторические результаты ниже относятся к указанным сборкам.

В область входят: локальное WPF-приложение, управляемые backend-процессы (`opencode serve`, `star-cliproxy` и его Codex/AGY subprocesses, `cursor-agent acp`, optional legacy entrypoint), локальное хранилище и сетевые каналы.

В текущей ревизии область расширена фактически реализованными подсистемами: Mirasim backend (ADR-0008), `star-cliproxy` мост Codex/AGY (ADR-0007), Workflow Studio с Pre-Coder Gate, Activity Center и unified shell (Phases 9–11), а также hardening-контуром Phase 12 Milestone 12A: диагностический bundle, backup/restore SQLite, crash/reboot recovery и валидация версий CLI. Ревизия и её доказательства зафиксированы в §8.

Не входят в область первой версии (§14 ТЗ): мобильные/web-клиенты, облачная синхронизация, собственный relay, биллинг, автоматическое изменение валидированного workflow, автоматический commit/push, одновременное редактирование checkout несколькими writers.

Допущения:

- Windows x64, per-user установка; приложение работает с правами текущего пользователя;
- внешние CLI (`opencode`, `star-cliproxy`, `codex`, `agy`, `agy-profile`, `cursor-agent`) и plugins — внешние необязательные prerequisites и рассматриваются как потенциально недоверенные компоненты;
- значения секретов хранятся в Windows secret storage и не записываются приложением на диск в открытом виде; расшифрованное значение кратковременно существует в памяти для проверки и аутентификации, а передача разрешена только закреплённому backend/provider через его scoped environment или auth header, отдельно от prompt и диагностических данных;
- пользовательский `WORKFLOW.ZIP` — внешние, ранее валидированные данные, но всё равно недоверенный ввод.

## 2. Trust boundaries

| ID | Boundary | Что внутри | Что пересекает границу | Базовое правило |
|---|---|---|---|---|
| `TB-1` | UI | WPF views/view-models, secure input fields, отображение redacted данных | Пользовательский ввод (prompt, path, API key, approvals), clipboard, drag-and-drop | UI не запускает процессы, не читает stdout и не обращается к SQLite напрямую (§4.2) |
| `TB-2` | App Core | Application Services: Project Catalog, Provider/Model Catalog, Routing Engine, Session Manager, Workflow Service, Quota Service, Health Service, Execution Supervisor | Команды UI, решения routing, workflow metadata, secret references | App Core — единственный владелец policy-решений и единственный writer SQLite |
| `TB-3` | Supervisor | Process Supervisor, Execution Watchdog, управление lifecycle, pipes, cancellation | Spawn-параметры процессов, normalized events, terminal outcomes | Supervisor не получает секреты в command line; secrets только через scoped environment конкретного child process |
| `TB-4` | Local Storage | SQLite, blob store, scratch, structured logs, diagnostic bundles, search index | Метаданные, secret URN references, workflow blobs, redacted events | Секреты в таблицах запрещены; хранится только secret reference (§8) |
| `TB-5` | Managed Process | `opencode serve`, `star-cliproxy`/Codex/AGY, `cursor-agent acp`, optional legacy entrypoint и их process tree | stdin/stdout/stderr, HTTP/SSE, JSON-RPC/ACP, файлы workspace | Managed process считается untrusted: вывод нормализуется, не исполняется и проходит redaction до записи |
| `TB-6` | Network | Loopback HTTP/SSE server бэкенда, пользовательский Mirasim HTTP host и внешние provider endpoints | Prompts, attachments, events, approvals, provider responses | Loopback-only по умолчанию; HTTPS по умолчанию; data classification gate перед отправкой наружу |

Поток данных между границами и точный состав каждого потока описаны в `docs/architecture/DATA_FLOW.md`.

## 3. Классификация данных

| Класс | Описание | Default | Правило маршрутизации/обработки |
|---|---|---|---|
| `PublicSource` | Данные, допустимые к отправке любому явно подключённому route | Custom provider по умолчанию имеет `maxDataClass=PublicSource`, пока пользователь явно не изменит trust | Допустим любой явно подключённый route |
| `PrivateSource` | Исходники и сообщения проекта, не предназначенные произвольному получателю | Классом по умолчанию для нового проекта является `PrivateSource` | Допустим только route, которому пользователь явно разрешил private source (§6.5) |
| `Restricted` | Данные, которые нельзя автоматически отправлять внешнему endpoint | Назначается пользователем вручную | Автоматическая отправка внешнему endpoint запрещена; каждый sanitized fragment требует отдельного preview/подтверждения |
| Secrets | API keys, tokens, passwords, OAuth refresh tokens, заголовки с пометкой `authorization`, `api-key`, `token`, `secret`, `password` | — | Не передаются ни при каком уровне классификации; хранятся только в Windows secret storage; в БД/логах/экспорте — только URN reference |

Правила:

1. Каждый проект хранит data classification; routing eligibility gate `data classification проекта не превышает maxDataClass route` обязателен.
2. Credentials и обнаруженные secrets не передаются ни при каком уровне классификации проекта.
3. Restricted fragment не отправляется без отдельного preview/подтверждения, даже если route явно разрешён для проекта.
4. Классификация пересматривается при импорте workflow и при добавлении custom provider; снижение класса требует явного действия пользователя.

## 4. STRIDE анализ

### 4.1. Spoofing

| ID | Угроза | Boundary | Контрмера |
|---|---|---|---|
| `S-1` | Второй экземпляр GUI запускает второй Supervisor и конкурирует за процессы/локальное хранилище | `TB-2`/`TB-3` | Named OS mutex на app-data каталог; single-supervisor guard; второй экземпляр открывается только в режиме просмотра |
| `S-2` | Подмена управляемого backend-процесса (чужой процесс на loopback-порту) | `TB-5`/`TB-6` | Управляемый spawn с проверкой executable/version, выбор свободного loopback-порта на инстанс, проверка version probe перед использованием |
| `S-3` | Подмена provider endpoint (MITM/фиктивный base URL) | `TB-6` | HTTPS по умолчанию; HTTP только для loopback/local network после предупреждения; validation endpoint URL |
| `S-4` | Ложное `Reattached` на чужую native session | `TB-3`/`TB-5` | Reattached только при совпадении native session ID и полного immutable binding (§6.7) |
| `S-5` | Plugin/провайдер под видом нужного account отвечает от имени другого | `TB-5` | `requestedRoute`/`observedRoute` comparison; `RouteMismatch` при отсутствии совпадения; отключение auto-rotation для GUI-managed session |

### 4.2. Tampering

| ID | Угроза | Boundary | Контрмера |
|---|---|---|---|
| `T-1` | Изменение импортированного workflow или его исходного архива | `TB-4` | Immutable content-addressed blobs SHA-256; запись только new version ID; post-operation source hash check; export byte-identical |
| `T-2` | Запись за пределы scratch при preview/draft/adaptation/run | `TB-4` | Scratch вне project root и вне blob store; path traversal prevention; блокировка записи вне scratch |
| `T-3` | Zip Slip, symlink escape и path traversal при импорте ZIP/папки | `TB-4` | ZIP safety: path traversal, symlinks, абсолютные пути, лимиты размера/числа файлов/compression ratio |
| `T-4` | Подмена или повреждение SQLite-состояния (locks, bindings, health) | `TB-4` | Единственный writer App Core; schema migrations с обратимым тестом на копии БД; транзакционность |
| `T-5` | Изменение binding после подтверждения native session (тихая смена account/model) | `TB-2`/`TB-5` | Binding неизменяем; изменение создаёт новую session с ancestry `RouteChange`; pin до отправки prompt; запрет silent fallback |
| `T-6` | Подмена/порча events из backend (malformed protocol/event) | `TB-5` | Нормализация событий, schema validation, `malformed protocol/event` health class, запрет исполнения полученного контента |
| `T-7` | Изменение управляемых логов/аудита после факта | `TB-4` | Structured logs только append; audit transitions не редактируются; диагностический экспорт — redacted копия, оригинал не мутируется |

### 4.3. Repudiation

| ID | Угроза | Boundary | Контрмера |
|---|---|---|---|
| `R-1` | Пользователь/процесс отрицает отправку prompt или approval | `TB-1`/`TB-3` | `clientRequestId`, prompt hash, correlation IDs workflowRunId/sessionId/executionId/processId; audit approvals и terminal outcomes |
| `R-2` | Спор о том, какой route/account фактически использовался | `TB-2`/`TB-5` | Сохранение `requestedRoute` и `observedRoute` для каждого execution; human-readable routing decision с gates и tie-break |
| `R-3` | Невозможно доказать причину health transition или снятия quarantine | `TB-2`/`TB-4` | Health events и audit transitions (180 дней); manual probe, verified recovery и `ForcedEnabled` различимы и записаны |
| `R-4` | Спор о владельце блокировки checkout | `TB-2`/`TB-4` | Lock owner — `executionId`, instance ID и generation процесса; stale lock не снимается автоматически при `Orphaned`/`Ambiguous` |

### 4.4. Information disclosure

| ID | Угроза | Boundary | Контрмера |
|---|---|---|---|
| `I-1` | Secret values попадают в SQLite, JSON, workflow-файлы, логи, crash reports, дампы или export | `TB-4` | Windows Credential Manager/DPAPI CurrentUser; reference-only URN; redaction pipeline до записи/отображения/экспорта |
| `I-2` | Secret values попадают в command line или argv дочернего процесса | `TB-3`/`TB-5` | Запрет секретов в аргументах; передача только через scoped environment конкретного child process |
| `I-3` | Prompt/источники проекта уходят внешнему endpoint вопреки классификации | `TB-6` | Data classification gate до отправки; `Restricted` требует отдельного подтверждения каждого fragment; `PrivateSource` только на разрешённые routes |
| `I-4` | Секреты или private data попадают в полнотекстовый индекс | `TB-4` | В индекс только redacted content; redaction до индексации, отображения и экспорта |
| `I-5` | Loopback server доступен вне машины | `TB-6` | Привязка к loopback, свободный порт на инстанс; публикация на внешнем интерфейсе запрещена по умолчанию |
| `I-6` | Диагностический bundle содержит секреты или приватные пути | `TB-4` | Автоматическая редакция секретов; обязательный preview bundle перед выгрузкой |
| `I-7` | Markdown/HTML из ответа исполняет scripts или тянет remote content | `TB-1`/`TB-6` | Renderer не исполняет scripts и remote content без разрешения |
| `I-8` | Чтение/копирование credential files сторонних CLI ради bridge | `TB-5` | Запрещено; Codex credentials остаются в отдельном `CODEX_HOME`, AGY — у `agy-profile`, proxy keys — в secret store |

### 4.5. Denial of service

| ID | Угроза | Boundary | Контрмера |
|---|---|---|---|
| `D-1` | Decompression bomb / чрезмерный package при импорте workflow | `TB-4` | Default import limits: ZIP до 100 MiB, распакованный package до 500 MiB, ≤ 10 000 файлов, один файл до 20 MiB, compression ratio entry ≤ 100:1 |
| `D-2` | Зависание child process, deadlock на stdout/stderr | `TB-3`/`TB-5` | Чтение stdout/stderr без deadlock; Execution Watchdog; bounded cancellation и process tree kill |
| `D-3` | Бесконечные автоматические retries и расход квоты | `TB-2`/`TB-6` | Bounded retry с audit trail; `Ambiguous` не повторяется автоматически и не расходует workflow retry budget |
| `D-4` | Quota polling storm и деградация provider rate limit | `TB-2`/`TB-6` | Jitter, backoff и per-provider rate limit; UI предотвращает бессмысленный частый polling |
| `D-5` | Рост памяти/UI от бесконечного event stream | `TB-1`/`TB-4` | Paging/virtualization списков; p95 UI event latency ≤ 200 мс; 100 000 events не ломают навигацию |
| `D-6` | Отказ одного adapter завершает GUI или теряет состояние | `TB-2` | Изоляция adapter failure; восстановление projects/sessions/runs/health после перезапуска; reconciliation |

### 4.6. Elevation of privilege

| ID | Угроза | Boundary | Контрмера |
|---|---|---|---|
| `E-1` | Approvals разрешают больше, чем запросил backend (`auto approve`) | `TB-1`/`TB-3` | Auto approve по умолчанию выключен; ответы allow once/allow for execution/deny; persistent rules только через отдельный экран |
| `E-2` | Неизвестный native approval kind трактуется как безопасный | `TB-3` | Неизвестный kind — `UnknownHighRisk`: deny или разовое явное разрешение; persistent rule не применяется |
| `E-3` | Shell/write-capable execution запускается параллельно в одном checkout | `TB-2`/`TB-4` | Checkout writer lock; подтверждённый read-only может идти параллельно; override не разрешает параллельных writers |
| `E-4` | Legacy entrypoint обходит writer lock, пишет в source blob или получает выдуманные route/session | `TB-3`/`TB-4` | Единый process tree и writer lock; scratch-копия; запрет изменения source blob; поля `Not reported`, пока не сообщены по контракту |
| `E-5` | Severity escalation: provider/plugin silently заменяет model/reasoning/speed/mode | `TB-2`/`TB-5` | `Unknown` не означает поддержку; fallback-rule не заменяет backend/model/reasoning/speed/mode; version mismatch не даёт silent fallback |
| `E-6` | Импорт OpenCode config перезаписывает пользовательскую конфигурацию | `TB-4` | Импорт только после preview; запрет перезаписи без явного подтверждения; config preview/diff проходит redaction |

## 5. Контрмеры

### 5.1. Redaction pipeline

1. Redaction — единая обязательная стадия обработки: `identify → classify → replace → verify`. Она применяется до любой записи raw events, config preview/diff, process/server logs, search index, crash report и diagnostic bundle (§6.16).
2. В свободном диагностическом тексте найденные секреты заменяются постоянными непрозрачными маркерами `[REDACTED]`, `***REDACTED***` либо типизированным `[REDACTED:<kind>]`, если вид известен (ADR-0010). Исходные значения, их длина, префикс и хеш в маркер не включаются. Это правило не меняет reference-only URN в структурированных полях и обязательные стадии `identify → classify → replace → verify`; маркер не доказывает полноту обнаружения.
3. Redaction не зависит от того, откуда пришли данные: UI, environment, provider response, plugin output или файлы проекта.
4. Диагностический экспорт показывает preview после redaction; выгрузка без preview запрещена.
5. Шифрование at rest — дополнительная защита и не заменяет redaction.
6. Красные тесты: контрактный класс `SecurityAndStorageContractTests` проверяет отсутствие секретов и приватных путей во всех нормативных артефактах.

### 5.2. Named OS mutex и single-supervisor guard

1. App-data каталог защищён named OS mutex; mutex имя детерминированно выводится из канонического пути app-data каталога.
2. Supervisor запускается только владельцем mutex; второй экземпляр GUI открывается в режиме просмотра и не запускает Supervisor, не запускает процессы и не пишет в SQLite.
3. Mutex не заменяет checkout writer lock и не даёт права на запись в workspace.

### 5.3. Checkout writer lock

1. Scope lock — канонический нормализованный корень checkout, а не display name проекта.
2. Владелец — `executionId`, instance ID приложения и generation управляемого процесса; lock хранится в SQLite и дублируется named OS mutex.
3. Lock берёт любой execution, для которого read-only не доказан capabilities/режимом.
4. Подтверждённые read-only executions могут идти параллельно; одновременные writers в одном checkout запрещены без исключения.
5. Stale lock не снимается автоматически, пока связанный execution имеет `Orphaned` или `Ambiguous`; пользователь получает recovery action с evidence.
6. Override lock в v1 не разрешает параллельных writers.

### 5.4. Path traversal prevention

1. Все пути из недоверенных источников (ZIP import, workflow manifest, backend events) канонизируются и проверяются до любой операции с файловой системой.
2. Запрещены: пустой путь, `..`-сегменты, rooted/абсолютные пути, drive-relative пути, UNC пути, NTFS alternate data stream (символ `:` вне буквы диска), управляющие символы.
3. Итоговый полный путь обязан находиться строго внутри базового каталога с границей разделителя; сравнение на Windows — case-insensitive.
4. Symlink/reparse point внутри импортируемого дерева отклоняются; Zip Slip и decompression bomb проверки не отключаются даже при override лимитов.
5. Запись workflow preview/draft/adaptation/run допускается только в изолированный scratch; запись за его пределы блокируется.

### 5.5. Secret storage boundary

1. Secret values хранятся только в Windows Credential Manager или DPAPI CurrentUser; SQLite и файлы содержат только URN reference `urn:llmworkgui:secret:openai-api-key` (ADR-0005).
2. Значение доступно только внутри процесса, который непосредственно формирует запрос к provider; UI получает только состояние «задан/не задан» и результат проверки соединения без секрета.
3. Секреты запрещены в command line, argv, конфигурационных файлах, prompts, workflow-файлах и любых логах.

### 5.6. Loopback-only и network boundary

1. Управляемый server бэкенда слушает loopback и выбирает свободный порт; публикация на внешнем интерфейсе запрещена по умолчанию.
2. HTTPS — default для внешних endpoints; HTTP допускается только для loopback/local network после предупреждения.
3. Перед отправкой наружу применяется data classification gate; provider responses проходят нормализацию и redaction.

### 5.7. Approval и tool boundary

1. Approvals отображают исходный backend-запрос и нормализованное объяснение; ответы: allow once, allow for execution, deny.
2. Auto approve не включается по умолчанию; неизвестный native kind — `UnknownHighRisk`.
3. Команды показываются пользователю до approval; adapter публикует mapping `native approval kind → normalized kind → поддерживаемые ответы`.

### 5.8. Import и resource limits

1. Импорт workflow проверяет archive safety и лимиты §6.13; превышение требует отдельного override preview.
2. Retry и polling bounded, с jitter/backoff/rate limit и записью в audit trail.
3. Списки и event streams используют paging/virtualization; memory не растёт бесконечно от event stream.

## 6. Остаточные риски

- **Сторонние CLI и plugins.** Поведение `opencode serve`, `star-cliproxy`, Codex/AGY, `agy-profile`, `cursor-agent acp` и plugins вне полного контроля приложения; риск снижается version/capability probe, loopback-only topology, account-context isolation и `UnsupportedVersion` вместо silent fallback.
- **Secrets вне приложения.** Файлы состояния сторонних CLI и их historical logs не контролируются приложением; они не копируются без sanitized preview, но приложение не обещает их очистку.
- **Capability drift.** Версии и API могут измениться; baseline — проверенный snapshot, а не вечный контракт.
- **Redaction не абсолютна.** Новые форматы секретов требуют обновления паттернов; поэтому redaction — обязательная стадия, а не единственная защита: секреты не хранятся в БД в принципе.
- **Restricted data.** Разовое подтверждение fragment снижает, но не исключает риск ошибки пользователя; ответственность фиксируется в audit.

## 7. Связь с контрактными тестами

Инварианты этого документа проверяются классом `tests/LLMWorkGUI.Backends.ContractTests/SecurityAndStorageContractTests.cs`:

- наличие и структура `THREAT_MODEL.md` и `DATA_FLOW.md`;
- STRIDE-секции, trust boundaries `TB-1`–`TB-6`, классы данных и контрмеры §5;
- контракт URN secret references `^urn:llmworkgui:secret:[a-z0-9_-]+$`;
- детерминированное SHA-256 content-addressed blob hashing;
- path traversal prevention (запрет `..`, абсолютных путей вне базового каталога, NTFS alternate data streams);
- отсутствие секретов и приватных путей пользователя в нормативных артефактах.

## 8. Ревизия Phase 12A: покрытие реализованных подсистем

- **Дата ревизии:** 2026-09-25 (TASK-062, Milestone 12A).
- **Область ревизии:** подсистемы, внедрённые после Phase 0: Mirasim (ADR-0008), `star-cliproxy` мост Codex/AGY (ADR-0007), Workflow Studio и Pre-Coder Gate, Activity Center и unified shell, а также hardening-контур Phase 12A.
- **Результат:** новых границ доверия не появилось; все подсистемы укладываются в `TB-1`–`TB-6` и обязаны соблюдать контрмеры §5. Закрытые пробелы: redacted diagnostic bundle с обязательным preview и блокирующим secret-сканом; проверяемый по контрольной сумме backup/restore; startup recovery после сбоя; типизированный version mismatch вместо silent fallback.

### 8.1. Покрытие подсистем

| Подсистема | Границы | Ключевые угрозы | Контрмеры этой ревизии |
|---|---|---|---|
| Mirasim backend (ADR-0008) | TB-2, TB-5, TB-6 | Подмена loopback host, чужой harness/model, утечка prompt внешнему endpoint | Health probe с точной версией; host задаётся пользователем; маршрут только по явно выбранному host; version mismatch даёт типизированное предупреждение |
| StarCliProxy / Codex / AGY (ADR-0007) | TB-2, TB-3, TB-5 | Подмена provider/account, silent fallback на другую модель, утечка credentials сторонних CLI | Изолированные `CODEX_HOME`/`agy-profile`; сравнение `requestedRoute`/`observedRoute`; запрет чтения чужих credential files; capability probe |
| Workflow Studio | TB-1, TB-2, TB-4 | Изменение утверждённой версии, обход Pre-Coder Gate, утечка секретов в draft/prompt | Immutable versions по content hash; Pre-Coder Gate валидирует draft до активации; preview и secret scan перед отправкой |
| Activity Center | TB-1, TB-2, TB-4 | Секреты в полнотекстовом индексе, подмена audit trail, неограниченный рост памяти | Redaction до индексации; append-only журнал; paging/virtualization; p95 ≤ 200 мс |
| Pre-Coder Gate | TB-2, TB-4 | Старт реализации до утверждения документов, конфликтующие вердикты | Явные gates и evidence; конфликт вердиктов блокирует переход; terminal evidence сохраняется в run |
| Diagnostic bundle (Phase 12A) | TB-4 | Секреты или приватные пути в архиве, экспорт без preview | Обязательный preview; redaction pipeline; `SensitiveDataFilter` + `WorkflowSecretScanner`; finding, переживший redaction, блокирует архив; retention 7 дней |
| DB backup/restore (Phase 12A) | TB-4 | Повреждённый snapshot, восстановление повреждённой БД, потеря активной БД | `VACUUM INTO` online snapshot; `PRAGMA integrity_check`; SHA-256 sidecar; rollback-снапшот и автооткат |
| Crash/reboot recovery (Phase 12A) | TB-2, TB-3, TB-4 | Оборванный execution/run навсегда остаётся «живым», stale lock, потеря audit | Quarantine неподтверждённой доставки как Ambiguous; runs без свежего reattach завершаются отдельно; lock требует terminal хронологии и отсутствия pending ownership; health rehydration из append-only audit |
| CLI version mismatch (Phase 12A) | TB-3, TB-5 | Silent fallback на неизвестную версию CLI | Объявленная матрица tested versions; newer → capability probe; older или exact mismatch → блокировка |

### 8.2. Инварианты диагностического bundle

1. Экспорт невозможен без preview, полученного тем же экземпляром сервиса; preview истекает по времени и не переиспользуется после успешного экспорта.
   Уточнение01.10.2026: один preview резервируется на всё время экспорта. Срок действия
   и отмена проверяются после сканирования, при записи ZIP и непосредственно перед
   атомарной публикацией. Неудачная попытка освобождает резервирование; успешная
   публикация потребляет preview. Временный ZIP удаляется при ошибке до публикации.
2. Каждый текстовый файл проходит `SensitiveDataFilter.Redact`/`RedactJson`; ссылка `urn:llmworkgui:secret:openai-api-key` остаётся reference-only metadata и не считается утечкой.
3. `WorkflowSecretScanner` сканирует финальный набор файлов до записи архива; finding вне reference, не сводимый к placeholder, выбрасывает `DiagnosticBundleBlockedException`, и архив не создаётся.
4. Bundle никогда не содержит значений секретов, DPAPI-блобов и приватных ключей; приватные пути пользователя маскируются как `%USERPROFILE%`.
5. Manifest фиксирует состав архива, redaction и результат скана; архивы диагностики удаляются retention-политикой через 7 дней.

### 8.3. Инварианты backup/restore БД

1. Снапшот создаётся согласованно через `VACUUM INTO` и не останавливает активных писателей.
2. У каждого снапшота есть SHA-256 sidecar; restore отказывается работать без sidecar или при несовпадении хэша.
3. `PRAGMA integrity_check` выполняется и для снапшота, и после restore; при провале restore возвращается rollback-снапшот, снятый до замены файла.
4. Перед restore пользователи БД должны быть остановлены. После очистки connection pools
   оставшиеся WAL/SHM/rollback journals запрещают замену: их нельзя удалять, чтобы
   принудительно освободить базу. Windows также отклоняет замену при открытом SQLite handle.
5. Checksum и integrity проверяются на уникальной временной копии рядом с целевой БД;
   публикуется именно проверенная копия. Изменение исходного backup после проверки
   не подменяет restore. До File.Move принимается cancellation; после публикации
   verification и необходимый rollback завершаются без отменённого caller token.
6. Исключение при post-restore verification также запускает rollback. Это не протокол
   координации работающих writers: вызывающий код обязан обеспечить quiescence.

### 8.4. Инварианты crash/reboot recovery

1. Recovery выполняется только primary-инстансом; view-only инстанс не меняет состояние и сообщает об этом в отчёте.
2. Executions с возможной доставкой и неподтверждённым исходом переводятся в `Ambiguous` с append-only audit; поздний подтверждённый terminal commit сохраняется. Незавершённые workflow runs переводятся в `Failed`, кроме runs со свежим подтверждённым reattach; это не доказательство terminal исхода native execution.
3. Сессии `Starting`/`Active` переводятся в консервативный `Ambiguous`; автоматический retry не выполняется.
4. Checkout lock снимается только при подтверждённом terminal execution с корректной хронологией Created/Started/Ended, совпадении project/session, отсутствии ActiveExecutionId и других неподтверждённых executions этой session. `Orphaned` удерживает lock; одного terminal enum недостаточно.
5. Машины состояний здоровья регидратируются из сохранённого snapshot `HealthStates`. Production `SqliteHealthTransitionStore` сохраняет snapshot и append-only health audit в одной SQLite-транзакции. Retention старого audit не удаляет актуальный snapshot и не заменяет его восстановлением по неполному журналу; audit-записи не редактируются, удаление допускается только по retention.

### 8.5. Инварианты версий CLI

1. Каждый adapter объявляет minimum/maximum tested versions; матрица версий является частью контракта.
2. Версия новее maximum tested требует capability probe; результат probe фиксируется как evidence.
3. Версия ниже minimum, exact mismatch, неразбираемый вывод или неизвестный CLI блокируют использование адаптера; silent fallback запрещён.
4. Version mismatch отображается типизированным предупреждением и не меняет binding активной native session.

### 8.6. Верификация секрет-скана

- `SensitiveDataFilter` и `WorkflowSecretScanner` покрыты красными тестами: секреты не появляются в логах, БД, bundles, audit и export.
- Тесты diagnostic bundle проверяют отсутствие raw secret fixtures в архиве, блокировку при пережившем redaction finding и отсутствие приватных путей пользователя.
- Crash recovery пишет только идентификаторы и типизированные причины; raw prompt и файлы workspace в отчёт не попадают.
- Остаточный риск сохраняется по §6: новые форматы секретов требуют обновления паттернов, поэтому redaction — обязательная стадия, а не единственная защита.

### 8.7. Response-origin identity reviewer — проверка 01.10.2026

- Дополнительный spoofing boundary TB-5/TB-6: completion `model`, generic provider/account
  fields и echoed client session — неподтверждённые claims, а не native observations.
- `StarCliProxyObservedEvidence` по умолчанию имеет false provenance flags; отсутствующее
  доказательство и противоречащие chunks не разрешают reviewer success.
- `StarCliProxyReviewReadOnlyChannel.SupportsRoute=false` сохраняется до принятого terminal
  native identity contract. Конфиг, alias, успешный текст и внешнее code review его не заменяют.
- Native identity resolver требует единственного enabled persisted binding; duplicates,
  namespaces, mode variants и смена назначения роли требуют отдельных negative controls.
- Gateway v1.3.0 не сообщает необходимый native tuple. Это dependency blocker, а не
  допустимый fallback: [анализ протокола](../work/PHASE10_REVIEW_ROUTE_PROTOCOL_DEPENDENCY_20261001.md).
- Текущие проверки provenance/resolver/channel входят в Release suites; threat-model gate,
  полный scan triage и live route/account/session proof остаются OPEN.

### 8.8. Границы файловой retention-очистки — 01.10.2026

- Junction в scratch scope раньше позволял удалить соседний каталог за пределами scratch;
  рекурсивная очистка logs/diagnostics также переходила по ссылкам. Воспроизведение
  выполнено только над изолированными synthetic canaries.
- Оба retention-сервиса проверяют reparse points на leaf и всех предках до корня тома.
  Обход выполняется по одному уровню без перехода по ссылкам; unsafe/inaccessible
  workspace сохраняется целиком. Свежие hidden/system элементы учитываются при
  определении возраста scratch. Архивирование через перенаправленный путь запрещено,
  и соответствующие execution events не удаляются из БД.
- Это ограничение статического перенаправления путей. Оно не гарантирует атомарной
  изоляции от hostile concurrent directory retargeting, не доказывает отсутствие
  активного пользователя старого scratch по одному mtime и не меняет SQL TTL policy.
- Regression evidence: `RetentionBoundaryTests`, существующие `RetentionCleanupTests`
  и `RetentionServiceTests`; полный phase/security/release gate остаётся отдельным.
