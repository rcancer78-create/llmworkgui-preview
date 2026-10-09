# LLM Work GUI — Security Guide

> Статус 02.10.2026: последний принятый bounded кандидат **1.0.0-rc.20261002.3**; [исправления и проверки кандидата](../work/REPORT_FINAL_CANDIDATE_20261002.md). Fresh frozen full4196/4196, Release0warnings/errors, package install/update/startup/rollback/uninstall PASS, visible129PASS/0FAIL/1NOT_TESTED на8 package-identical modules, Space Bunny MAX PASS. Phase/release/owner gates OPEN; security gate фиксируется в artifacts/remaining-work-20261002/security-snapshot/STATUS.md; frozen rc.11 audit остаётся историческим. Cold-start DEFERRED_BY_OWNER. Новые TEMP/TMP, helpers и review/test artifacts — на D:.


> Исторический статус 01.10.2026: security acceptance релиза OPEN. Замечания Space Bunny/OpenCode MAX к scan-time binding, ZIP occurrences и header-only private-key approval исправлены; 17/17 изолированных regression controls PASS. Повторный reviewer прочитал39/39 chunks и1350/1350 групп первого2638-source/2663-finding frozen snapshot и дал DISPOSITION_VERDICT/SECURITY_SCOPE_VERDICT PASS. Он не воспроизводил hashes/запуск checks; PASS ограничен переданными disposition material и механизмом. Source/line/container SHA256 проверяются при triage; private-key решение связано с целым source. Неизвестные payloads, stale metadata, unresolved/skipped и duplicate approvals блокируют приёмку. Binary/OCR, credential stores, .env, неизвестные encoding/credential formats и настоящий user bundle вне scope. [Audit report](../work/REPORT_AUDIT_BINDING_20261001.md). Исторический frozen rc.10 2772-source/3378-finding closure snapshot и419 новых dispositions рассмотрены отдельно: [snapshot/result](../../artifacts/remaining-work-20261001/secret-scan-closure/SNAPSHOT.md).

**Статус:** актуально для Release Candidate 1.0 (Phase 12)
**Нормативная модель угроз:** `docs/architecture/THREAT_MODEL.md`
**ADR:** ADR-0005 (secret storage), ADR-0007 (star-cliproxy boundary), ADR-0008 (Mirasim), ADR-0009 (installer)

Документ объясняет, как устроена безопасность продукта на практике: где живут секреты, как
работает redaction, какие gates защищают выполнение кода и данные, и как эти инварианты
проверяются тестами.

---

## 1. Модель доверия (кратко)

| Boundary | Что внутри | Что пересекает | Инвариант |
|---|---|---|---|
| `TB-1` UI | WPF views/view-models | пользовательский ввод, clipboard, drag&drop | UI не запускает процессы, не читает stdout и не пишет в SQLite напрямую |
| `TB-2` App Core | Application Services | команды UI, policy-решения, secret references | единственный владелец policy и единственный writer SQLite |
| `TB-3` Supervisor | Process Supervisor/Watchdog | spawn-параметры, normalized events | секреты не попадают в command line; scoped environment только для конкретного child |
| `TB-4` Local Storage | SQLite, blobs, scratch, logs, bundles, search index | метаданные, URN, workflow blobs, redacted events | секреты в таблицах запрещены; только reference |
| `TB-5` Managed Process | `opencode serve`, `cursor-agent acp`, star-cliproxy/Codex/AGY, legacy entrypoint | stdio/HTTP/JSON-RPC/workspace files | managed process untrusted: вывод нормализуется, не исполняется, проходит redaction |
| `TB-6` Network | loopback servers и внешние provider endpoints | prompts, events, approvals | loopback-only по умолчанию, HTTPS по умолчанию, classification gate перед отправкой |

Классы данных: `PublicSource`, `PrivateSource` (default нового проекта), `Restricted`
(ручное назначение, пофрагментное подтверждение), `Secrets` (никогда не передаются).
Маршрутизация учитывает gate `data classification проекта ≤ maxDataClass route`.

---

## 2. Секреты: хранение и обращение

**Хранение.** `CredentialManagerSecretStore` (реализация `ISecretStore` и
`ISecretPayloadManager`) поверх `ICredentialManagerApi` / `WindowsCredentialManagerApi`:

- первичное хранилище — per-user generic credentials текущего пользователя в Windows
  Credential Manager (`CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`) с
  детерминированным target `LLMWorkGUI/secret/<identifier>`, где `<identifier>` —
  идентификатор URN; owner и kind остаются reference-only metadata в SQLite;
- значение в credential blob — строго UTF-8 без завершающего нуля; target, user name и
  comment значения не содержат;
- fallback — существующий DPAPI payload `{secrets directory}/{identifier}.secret`
  (`LLMWGUS1`, `LLMWorkGUI.SecretStore.v1`, `DataProtectionScope.CurrentUser`) в
  `%LOCALAPPDATA%\LLMWorkGUI\secrets`; используется только при доказанной
  недоступности Credential Manager (`50` или незагружаемый API) и только для ссылки без
  authority marker `{identifier}.cmref`;
- marker создаётся до `CredWrite`, поэтому существующая запись Credential Manager всегда
  авторитетна, а DPAPI-файл не является fallback; при неудачной записи marker удаляется
  обратно, а при невозможности — операция завершается без fallback (ADR-0005 §1.1);
- атомарная запись (`AtomicFile.WriteAllBytesAsync`), одна операция пишет ровно один
  backend, удаление убирает обе репрезентации и marker;
- возвращается только `SecretReference` вида `urn:llmworkgui:secret:<id>`
  (`^urn:llmworkgui:secret:[a-z0-9_-]+$`); чтение значения возможно только в процессе,
  который непосредственно формирует provider-запрос.

**Ограничения.** Credential Manager ограничивает generic credential 2560 байтами
(`CRED_MAX_CREDENTIAL_BLOB_SIZE`); превышение даёт типизированную ошибку без записи, а не
усечение и не откат к DPAPI. Падение процесса между созданием marker и `CredWrite` делает
секрет временно нечитаемым (ссылка становится `Missing` и значение вводится заново), но
не воскрешает старое значение.

**Обращение.**

- В SQLite, логах, crash-отчётах, диагностических пакетах, prompts, workflow-файлах и
  argv процесса хранится только URN или `[REDACTED]`.
- UI видит состояние «задан/не задан» и результат connection test без значения секрета.
- Environment secrets передаются только конкретному child process через scoped
  environment; `ProcessStartSpecification` не допускает секретов в аргументах.
- Reading/copying credentials сторонних CLI (`auth.json`, agy profiles) запрещено:
  Codex-аккаунт изолируется отдельным абсолютным `CODEX_HOME`, AGY — через `agy-profile`;
  переключение — только под общим account/writer lock и только без живых executions
  (ADR-0007, TOCTOU-free select→verify→launch).

---

## 3. Redaction pipeline

Единая обязательная стадия `identify → classify → replace → verify`, применяемая **до**
записи raw events, config preview/diff, process/server logs, search index, crash report и
diagnostic bundle.

`SensitiveDataFilter` (Infrastructure/Security) заменяет на `[REDACTED]`:

- private key blocks (`-----BEGIN … PRIVATE KEY-----`);
- `authorization`-строки и `Bearer`-токены;
- JSON-парные строковые значения для чувствительных ключей;
- OpenAI-ключи (`sk-…`), GitHub (`ghp_/gho_/ghu_/ghs_/ghr_`), AWS (`AKIA…`),
  Slack (`xox…`), Cursor (`crsr_…`), Google API keys, JWT;
- generic assignments `api_key|token|secret|password = "…"`;
- `RedactJson`/`RedactJsonNode` обходят вложенные JSON-структуры.

`RedactDiagnostic` разбирает целый JSON object/array, включая экранированные и повторяющиеся
имена свойств и числовые/object/array значения чувствительных полей. Чистый JSON сохраняет
исходное форматирование. JSON внутри строкового значения и JSON с произвольным текстовым
префиксом обрабатываются текстовыми шаблонами; это не гарантия очистки любой сериализации.
Обрезанный private-key блок без END удаляется до конца сообщения.

Чувствительные имена определяются без учёта регистра по подстрокам `apikey`, `api_key`,
`api-key`, `secret`, `password`, `passwd`, `authorization`, `credential`, `private_key`,
`privatekey`, `access_key`, `accesskey`, а также отдельному слову `token`, ограниченному
небуквенными символами. Поэтому общий фильтр маскирует и `token_count`/`token_limit`;
типизированная модель квот сохраняет числовые показатели отдельно от диагностического текста.
Допустимый формат маркеров согласован с THREAT_MODEL через принятый
[ADR-0010](../adr/ADR-0010-diagnostic-redaction-markers.md).

`RedactingLoggerProvider`/`RedactingLogger` применяют фильтр к structured log state и
исключениям (`RedactedLogState`, `RedactedException`) в пределах описанных шаблонов и
структурного разбора. Новые записи Activity Center проходят очистку до хранения и индексации.
Успешная maintenance при запуске также очищает прежние Title/Description и перестраивает
их логическое FTS-представление. Ошибка maintenance явно предупреждает, что в старом
индексе могли остаться исходные значения; возвращаемые поля очищаются отдельно.
Это не стирание старых страниц SQLite/WAL/FTS shadow segments или ранее созданных backup.

**Диагностика.** Diagnostic bundle собирается только после preview; экспорт возможен лишь
для preview того же экземпляра сервиса (TTL 15 минут). Если после redaction остаётся
finding вне secret-reference, `DiagnosticBundleBlockedException` блокирует создание
архива. Manifest фиксирует redaction policy и список файлов.

---

## 4. Approval gates

### 4.1. Native approvals

- Auto approve выключен по умолчанию; ответы берутся из capability mapping конкретного адаптера. OpenCode поддерживает `once`/`reject`; `allow for execution` нельзя предлагать без подтверждённого native mapping или для UnknownHighRisk/HighRiskDestructive.
- Неизвестный native approval kind нормализуется как `UnknownHighRisk`: deny или разовое
  явное разрешение; persistent rule не применяется.
- Команды и исходный backend-запрос отображаются пользователю до approval; mapping
  `native kind → normalized kind → supported answers` ведётся в
  `docs/protocols/capabilities/approval-mapping.md`.

### 4.2. Pre-Coder Approval Gate

`PreCoderGateValidator` (Application/Workflows/Studio) не пропускает переход к кодеру,
пока не выполнены **все** условия:

1. обязательные документы присутствуют;
2. по каждой обязательной reviewer-роли существует verdict на **текущем** hash
   документа (`DocumentHash == ContentHash`);
3. отсутствует конфликт `approve` и `reject/request-changes` на текущем hash;
4. последний verdict каждой роли — `Approve`;
5. есть пользовательское approval на тот же hash (`UserApprovalEvidence`);
6. при `RequiresUiArtifact` — обязательный UI-артефакт;
7. при `RequiresUserVisualAcceptance` — пользовательская визуальная приёмка.

Каждое из нарушений даёт отдельный типизированный blocker (missing reviewer, conflicting
verdicts, hash mismatch, missing required document, missing user approval, missing UI
artifact, missing visual acceptance), что позволяет доказывать их изоляцию в E2E-тестах.

---

## 5. Процессы, locks и целостность

- **Единственный supervisor.** App-data каталог защищён named OS mutex; второй экземпляр
  GUI работает view-only и не пишет в SQLite и не запускает процессы.
- **Checkout writer lock.** Scope — канонический корень checkout; владелец —
  `executionId` + instance ID + generation; подтверждённые read-only executions идут
  параллельно, параллельные writers запрещены. Stale lock не снимается, пока execution
  `Orphaned`/`Ambiguous`.
- **Process launch.** Типизированный argv без shell interpolation; запрещены вложенные
  `powershell -Command` и произвольный shell text в штатных путях; пустые аргументы,
  пробелы, кавычки, `$`, Unicode и длинные пути передаются байт-точно или отклоняются до
  старта.
- **Immutability.** Workflow blobs content-addressed SHA-256; версии неизменяемы
  (`ON CONFLICT (Id) DO NOTHING`); export оригинала byte-identical; active pointer
  меняется без перезаписи package.
- **Path safety.** `InputSanitizer` запрещает `..`-сегменты, rooted/UNC/ADS/управляющие
  символы, symlink/reparse escape; запись preview/draft/adaptation/run — только в
  изолированный scratch. ZIP import ограничен по размеру (100 MiB архив, 500 MiB
  распаковано, ≤ 10 000 файлов, ≤ 20 MiB/файл, ratio ≤ 100:1) и защищён от Zip Slip и
  decompression bomb динамическим контролем распаковки.
- **Version mismatch.** `CliVersionValidator` не допускает silent fallback: newer →
  `CapabilityProbeRequired`; older/exact mismatch/unparseable → `BlocksSilentFallback`.
- **Route evidence.** `requestedRoute` и `observedRoute` сохраняются отдельно; mismatch →
  `RouteMismatch` и не считается успехом; смена account/model создаёт новую session.

---

## 6. Сеть и внешние границы

- Managed servers слушают loopback на свободном порту инстанса; публикация на внешнем
  интерфейсе запрещена по умолчанию.
- Внешние endpoints — HTTPS по умолчанию; HTTP только для loopback/local network после
  предупреждения. URL валидируется; invalid URL/certificate/auth дают разные понятные
  ошибки.
- Перед отправкой наружу применяется data classification gate; `Restricted` fragment
  требует отдельного preview/подтверждения.
- Ответы провайдеров и backend-события нормализуются; Markdown/HTML renderer не исполняет
  scripts и remote content без разрешения.
- Mirasim host, его relay/active account и recording не изменяются приложением;
  недоказанные account/route leg остаются `ManualOnly` с `Not reported`.

---

## 7. Инсталлятор и жизненный цикл

- Установка per-user в `%LOCALAPPDATA%\Programs\LLMWorkGUI` без UAC, без записи в
  реестр и без изменения внешних CLI (ADR-0009).
- App-data (`%LOCALAPPDATA%\LLMWorkGUI`) не удаляется и не перезаписывается при
  install/update/rollback; uninstall удаляет её только по явному `-RemoveUserData`.
- Обновление атомарно: staging → активация → откат при ошибке; точка отката
  `<install>.rollback` позволяет вернуться к предыдущей версии без потери данных.
- Операции жизненного цикла отказываются работать при запущенном приложении
  (`APP_RUNNING`).
- До подписи bootstrapper-а распространяйте дистрибутив с контрольными хэшами; подпись —
  обязательный шаг перед GA.

---

## 8. Верификация в тестах

| Инвариант | Где проверяется |
|---|---|
| Секреты не попадают в БД/логи/экспорт | `CredentialManagerSecretStoreTests`, `WindowsCredentialManagerApiTests`, `DpapiSecretStoreTests`, `SensitiveDataFilterTests`, `SecurityAndStorageContractTests` |
| Credential Manager авторитетен, DPAPI только как доказанный fallback, `Revoked` выигрывает | `CredentialManagerSecretStoreTests`, `SecretCredentialManagerCompositionTests` |
| Неаутентифицированный запрос при недоступном секрете не отправляется | `ProviderSecretGateTests`, `ProviderSecretGateWithCredentialManagerTests` |
| Diagnostic bundle redaction и блокировка | `DiagnosticBundleServiceTests`, `HardeningDiagnosticsViewModelTests` |
| Backup/restore integrity и безопасный отказ | `DatabaseBackupServiceTests` |
| Crash recovery и удержание Ambiguous/Orphaned locks | `AppCrashRecoveryTests` |
| Path traversal / ZIP safety | `InputSanitizerTests`, `SafeArchiveValidatorTests`, `WorkflowImportExportIntegrationTests` |
| Writer lock / single supervisor | `Concurrency`/`Executions` integration tests, `CursorWorkspaceViewModelTests` (writer lock) |
| Pre-Coder Gate blockers | `EndToEndHardeningScenarioTests`, `WorkflowStudioViewModelTests` |
| Version mismatch без silent fallback | `CliVersionValidatorTests` |
| Установка не трогает внешние CLI и app-data | `InstallerLifecycleTests` |
| Отсутствие секретов/профиль-путей в нормативных документах | `SecurityAndStorageContractTests.SecurityArtifacts_ContainNoSecretsOrUserProfilePaths` |

---

## 9. Операционные рекомендации

1. Не храните секреты в переменных окружения, передаваемых всему GUI-процессу; вводите их
   через UI — они уйдут в Credential Manager (DPAPI только как доказанный fallback).
2. Перед отправкой приватных источников внешнему провайдеру проверяйте classification и
   pre-send preview (Adaptation UI).
3. Не включайте auto approve; для high-risk команд отвечайте allow-once.
4. Перед диагностическим экспортом всегда просматривайте preview после redaction.
5. Для восстановления после сбоя запускайте crash recovery через Settings & Diagnostics,
   а не ручное вмешательство в SQLite.
6. Храните точку отката (`<install>.rollback`) до подтверждения стабильности новой
   версии.

Исторический rc.11 имеет отдельный [byte-bound audit](../../artifacts/remaining-work-20261001/secret-scan-spool/SNAPSHOT.md). Прежние2772/3378 относятся к frozen rc.10 snapshot; новые bytes автоматически не принимаются. Raw OpenCode spool по умолчанию выключен; при включении действуют [caller-managed retention и ограничения](OPENCODE_EVENT_SPOOL.md). Текущий [security gate](../../artifacts/remaining-work-20261002/security-snapshot/STATUS.md) остаётся OPEN.
