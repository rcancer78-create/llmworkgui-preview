# LLM Work GUI — Diagnostics & Recovery Guide

> Статус 02.10.2026: последний принятый bounded кандидат **1.0.0-rc.20261002.3**; [исправления и проверки кандидата](../work/REPORT_FINAL_CANDIDATE_20261002.md). Fresh frozen full4196/4196, Release0warnings/errors, package install/update/startup/rollback/uninstall PASS, visible129PASS/0FAIL/1NOT_TESTED на8 package-identical modules, Space Bunny MAX PASS. Phase/release/owner gates OPEN; security gate фиксируется в artifacts/remaining-work-20261002/security-snapshot/STATUS.md; frozen rc.11 audit остаётся историческим. Cold-start DEFERRED_BY_OWNER. Новые TEMP/TMP, helpers и review/test artifacts — на D:.


> Исторический статус 01.10.2026: текущий [rc.11 protocol/lifecycle](../work/REPORT_OPENCODE_SPOOL_ISOLATION_20261001.md); [rc.9 retention](../work/REPORT_RETENTION_BOUNDARY_20261001.md), [rc.8 backup/restore](../work/REPORT_BACKUP_RESTORE_20261001.md), [rc.7 diagnostic-export](../work/REPORT_DIAGNOSTIC_EXPORT_20261001.md) приняты bounded проверками. Настоящий reboot ещё не выполнен. Installer smoke использует synthetic isolated data, не новый Windows user profile. Restore остаётся service API.

**Статус:** актуально для Release Candidate 1.0 (Phase 12)
**Связано:** ТЗ §9.3, §10; ROADMAP Phase 12; `docs/architecture/THREAT_MODEL.md` §5.1, §6

Руководство описывает сбор диагностики, redaction preview, backup/restore базы данных,
восстановление после сбоев и retention-очистку. Preview/export, backup, integrity check,
ручной recovery и cleanup доступны из **Settings & Diagnostics** (Ctrl+0) через
`HardeningDiagnosticsViewModel`. Restore БД предоставлен сервисом; отдельной команды
restore в этом экране пока нет.

---

## 1. Где лежат данные

| Каталог | Содержимое | Управление |
|---|---|---|
| `%LOCALAPPDATA%\LLMWorkGUI` | SQLite `llmworkgui.db` (+ WAL/SHM), настройки, сессии | installer не удаляет без `-RemoveUserData` |
| `…\LLMWorkGUI\secrets` | DPAPI fallback payloads, authority/revocation markers | `CredentialManagerSecretStore` и `DpapiSecretStore`; основной storage — Windows Credential Manager |
| `…\LLMWorkGUI\blobs` | content-addressed workflow blobs `sha256/xx/<hex>` | immutable |
| `…\LLMWorkGUI\logs` | structured redacted logs | retention/ротация |
| `…\LLMWorkGUI\diagnostics` | диагностические архивы `*.zip` + sidecar | TTL 7 дней |
| `…\LLMWorkGUI\runs` | run directories `executionId` (stdout/stderr, spool) | вне project checkout |
| `…\LLMWorkGUI\scratch` | `preview`/`draft`/`adaptation`/`run` scratch scopes | TTL 7 дней |

Приложение не пишет в project checkout, кроме самого checkout-контента под writer lock.

---

## 2. Diagnostic bundle

### 2.1. Состав

`DiagnosticBundleService` собирает категории (`DiagnosticBundleCategories`):

- `environment` — runtime, OS, версии обнаруженных CLI (без секретов);
- `configuration` — безопасная конфигурация приложения;
- `database` — schema/метаданные БД (не значения секретов);
- `logs` — redacted application logs (лимиты по умолчанию: до 200 файлов,
  до 512 KiB на файл; настраивается в `DiagnosticBundleRequest`);
- `run-logs` — stdout/stderr завершённых runs с лимитами;
- `audit` — redacted health audit;
- `manifest` — опись архива и redaction policy.

### 2.2. Процесс

1. `PreviewDiagnosticBundleCommand` строит `DiagnosticBundlePreview` (файлы, категории,
   размеры, SHA-256, redaction notes) и сохраняет redacted bytes в приватном snapshot
   экземпляра сервиса. Preview живёт 15 минут; экспорт читает именно эти bytes,
   а не обновлённые исходные логи/SQLite после просмотра.
2. `CreateDiagnosticBundleCommand` создаёт архив **только** при наличии валидного preview
   того же экземпляра; экспорт без preview невозможен.
3. Если после redaction остаётся секрет вне secret-reference,
   `DiagnosticBundleBlockedException` блокирует экспорт.
4. Готовый `manifest` фиксирует список файлов, размеры, policy и timestamp.
5. Существующий файл никогда не перезаписывается.
6. Preview действует15мин и допускает один успешный экспорт. Параллельный экспорт
   того же preview отклоняется. Отмена или истечение срока до атомарной публикации
   не оставляют готового ZIP; временный файл удаляется. После ошибки можно повторить
   попытку с ещё действующим preview, после истечения срока нужен новый preview.

Данные внутри bundle маскируют `%USERPROFILE%`-пути; применяются `SensitiveDataFilter` и
`WorkflowSecretScanner`. Всегда просматривайте preview перед передачей архива наружу.

---

## 3. Backup, integrity check и restore БД

`DatabaseBackupService`:

- **Создание backup** (`CreateDatabaseBackupCommand`): согласованный online snapshot через
  `VACUUM INTO`, SHA-256 sidecar рядом с snapshot, `PRAGMA integrity_check`.
- **Проверка целостности** (`VerifyDatabaseIntegrityCommand`): `PRAGMA integrity_check`
  активной БД, результат — `DatabaseIntegrityReport` (ok/ошибки/размер/время).
- **Restore**: сначала остановите всех пользователей БД. Сервис копирует backup в
  уникальный временный файл рядом с целевой БД и проверяет его sidecar-хэш и integrity.
  После rollback-снапшота текущей БД публикуется именно проверенная копия. Connection
  pools очищаются; оставшиеся WAL/SHM/rollback journals запрещают замену и не удаляются
  принудительно. На Windows открытые SQLite handles также препятствуют замене файла.
- **Отмена и откат**: отмена действует до File.Move. После замены проверка integrity
  и необходимый rollback завершаются независимо от caller token. При неуспешной
  проверке или исключении во время неё сервис восстанавливает предыдущий snapshot
  и сообщает об ошибке. Rollback snapshot сохраняется; если сам откат встретил
  ошибку ввода-вывода, нельзя считать базу восстановленной — нужна отдельная проверка.
- **Безопасный отказ**: повреждённый snapshot, отсутствующий sidecar или checksum mismatch
  не трогают активную БД.

Backup-файлы — обычные файлы; секреты в них отсутствуют (БД хранит только URN), но
обращайтесь с ними как с приватными данными и не передавайте без redaction.

---

## 4. Crash/reboot recovery

Автоматический startup recovery после rc.5 выполняется только у primary-инстанса,
если marker предыдущего запуска указывает на прерывание. После reconciliation сервис
`RecoverWorkflowRunsAfterRestartAsync` завершает незаконченные workflow runs, кроме
run со свежим подтверждением Reattached/live process/matching binding/native session.
Он не переписывает native executions, sessions и checkout locks. Обычный clean reopen
сохраняет workflow. [Проверка rc.5](../work/REPORT_RESTART_RECOVERY_20261001.md).

Отдельная команда `RecoverInterruptedWorkCommand` на экране диагностики вызывает
более широкий ручной `AppCrashRecoveryService.RecoverAsync`:

- executions с возможной доставкой и неподтверждённым исходом переводятся в
  `Ambiguous` с append-only audit `CrashRecovery`; поздний подтверждённый terminal
  commit проверяется повторно перед записью и не перезаписывается recovery;
- незавершённые workflow runs — в `Failed`, кроме runs со свежим подтверждённым reattach;
- сессии в `Starting`/`Active` — в консервативный `Ambiguous`;
- checkout lock освобождается только при подтверждённом terminal execution с корректной
  хронологией Created/Started/Ended, совпадении project/session, отсутствии ActiveExecutionId
  и других неподтверждённых executions этой session; `Orphaned` удерживает lock.
  Одного terminal enum или отсутствия процесса недостаточно;
- машины состояний здоровья регидратируются из append-only журнала аудита;
- view-only инстанс (второй GUI) ничего не мутирует и помечает отчёт
  `SkippedAsViewOnly`.

`AppCrashRecoveryReport` содержит списки Interrupted executions/sessions/runs,
Released/Retained locks, число rehydrated health scopes и warnings.

**Рекомендация:** после аварийного завершения сначала запустите recovery, затем проверьте
Health Center и только потом продолжайте runs.

---

## 5. Long-running execution и quota soak

- `ILongRunningExecutionService` отслеживает heartbeat, обнаруживает
  `HeartbeatOverdue` (3 пропущенных интервала) и `TimedOut`, поддерживает управляемую
  отмену и `ReapStaleExecutions`; `RunAsync` использует linked cancellation с bounded
  timeout и всегда освобождает таймеры (`ActiveExecutionCount == 0`).
- `IQuotaPollingSoakRunner` (`RunQuotaSoakCommand`) прогоняет ≥ 50 циклов через реальный
  `IQuotaRefreshScheduler` (jitter/backoff/rate-limit), замеряет латентность, память,
  in-flight обновления и outstanding-таймеры; отчёт `QuotaSoakReport` показывает
  cycles/successes/failures, backoff-конверт и утечки.

---

## 6. Retention cleanup

`RetentionCleanupService` (`RunRetentionCleanupCommand`) применяет `RetentionPolicy`:

| Категория | TTL | Действие |
|---|---|---|
| Диагностические архивы `*.zip` (+ sidecar) | 7 дней | удаление |
| Брошенные scratch workspaces | 7 дней | удаление |
| Execution events терминальных прогонов | 30 дней | архивация в append-only `diagnostics/archive/audit-*.jsonl`, затем удаление |

Гарантии:

- активные runs и свежие bundles не трогаются; scratch сохраняется, если время
  изменения каталога или любого вложенного элемента свежее cutoff, включая hidden/system;
- `RetentionCleanupService` не архивирует и не удаляет HealthEvents; отдельный
  `RetentionService` применяет настроенный TTL health/audit (по умолчанию 180 дней),
  quota snapshots (90 дней, downsampling после 30) и process/server logs (14 дней);
- active/referenced workflow versions и immutable blobs не удаляются;
- при недоступной БД cleanup завершается warning-ом, а не падением.

Оба сервиса (`RetentionCleanupService` и `RetentionService`) пропускают reparse points
и пути с такими предками, включая предков настроенного app-data. Обход каталогов не
переходит по junction/symlink; scratch с небезопасным или недоступным вложением целиком
сохраняется. Пропуски учитываются в отчёте. Если `diagnostics/archive` перенаправлен,
архив не записывается и execution events остаются в БД. Обычные старые файлы рядом
с пропущенной ссылкой по-прежнему очищаются.

Это проверка существующих путей, не атомарная защита от процесса, который одновременно
подменяет каталог между проверкой и удалением. Возраст scratch определяется по mtime;
отдельного lease активного workspace этот механизм не проверяет.

Отчёт `RetentionCleanupReport` перечисляет удалённые/сохранённые элементы по классам.

---

## 7. Логи

- Structured logs с correlation IDs (`workflowRunId`/`sessionId`/`executionId`/
  `processId`), provider/account/model без секретов, process lifecycle, routing decision,
  health transition, quota refresh, protocol parse errors и user approvals.
- Запись проходит `RedactingLoggerProvider`; секреты и профиль-пути заменяются до
  сохранения.
- Телеметрия по умолчанию выключена; внешняя телеметрия требует opt-in и отдельного data
  inventory.

---

## 8. Быстрый чек-лист при инциденте

1. Settings & Diagnostics → **Verify database integrity**.
2. Проверьте Health Center (Ctrl+9): quarantine/cooldown/probe evidence.
3. Если приложение падало — **Recover interrupted work** и просмотрите отчёт.
4. Соберите **диагностический пакет**: preview → export.
5. Перед изменениями сделайте **backup БД**.
6. Для деградации CLI — Settings & Diagnostics: CLI status, version assessment
   (`CapabilityProbeRequired`/`BlocksSilentFallback`); silent fallback не выполняется.
7. Для расхождения route/account — смотрите requested/observed evidence и `RouteMismatch`
   в Activity Center.

## Уточнение 01.10.2026: аварийный и штатный lifetime

Начиная с rc.20261001.5 primary GUI создаёт `<database>.running`. Успешный graceful
shutdown удаляет только свой marker после остановки host. После kill/неуспешного shutdown
следующий startup выполняет narrow workflow recovery после native reconciliation.
Он завершает interrupted workflows, сохраняя native Reattached/Ambiguous evidence и locks.
После clean exit workflow сохраняется для явного ручного продолжения. View-only instance
marker не меняет. Отсутствие marker у предыдущей версии не доказывает аварийный lifetime.
Ручная **Recover interrupted work** остаётся более широкой операцией; она не нужна для
автоматического workflow recovery нового crash. [Actual packaged process evidence](../work/REPORT_RESTART_RECOVERY_20261001.md)
не принимает настоящий OS reboot или live native/backend crash.

В rc.20261001.6 shell открывается до завершения необязательной проверки PATH. Начальное
«Статус CLI бэкендов ещё не проверялся» корректно, пока worker выполняет discovery;
после завершения status обновляется на UI-потоке. Медленный filesystem probe не блокирует
навигацию, а закрытие окна отменяет публикацию его результата. Это улучшение отзывчивости
не подтверждает cold-start≤3с: [актуальные замеры](../work/REPORT_STARTUP_RESPONSIVENESS_20261001.md).
