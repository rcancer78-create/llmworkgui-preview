# LLM Work GUI — Testing Strategy

> Статус 02.10.2026: последний принятый bounded кандидат **1.0.0-rc.20261002.3**; [исправления и проверки кандидата](../work/REPORT_FINAL_CANDIDATE_20261002.md). Fresh frozen full4196/4196, Release0warnings/errors, package install/update/startup/rollback/uninstall PASS, visible129PASS/0FAIL/1NOT_TESTED на8 package-identical modules, Space Bunny MAX PASS. Phase/release/owner gates OPEN; security gate фиксируется в artifacts/remaining-work-20261002/security-snapshot/STATUS.md; frozen rc.11 audit остаётся историческим. Cold-start DEFERRED_BY_OWNER. Новые TEMP/TMP, helpers и review/test artifacts — на D:.

**Статус:** актуально для Release Candidate 1.0 (Phase 12)
**Связано:** ТЗ §12; ROADMAP общие правила 4, 7, 10; `docs/architecture/ARCHITECTURE_RELEASE.md`

Документ описывает таксономию тестирования, команды прогонов, изоляцию профилей и
разделение детерминированных заглушек и реальных live smoke.

По команде владельца 02.10.2026 все новые временные данные и результаты работы
над проектом размещаются на D:. Для сборки и тестов используется
`scripts/Invoke-ProjectChecks.ps1`, назначающий дочерним процессам TEMP/TMP на D:
и сохраняющий TRX/скриншоты на D:. Правило, архивирование нужной evidence и журнал
очистки C: TEMP: [TEMP storage policy](../work/TEMP_STORAGE_POLICY_20261002.md).

---

## 1. Инварианты тестового контура

`VisualUi` — категория23 классов с оконными сценариями, включая смешанные классы с
composition/view-model проверками. Последнее разбиение633 UI cases:209 в этой категории,
424 вне её; группы не пересекаются. Это не209 отдельных screenshot assertions.
Изолированный запуск групп: `scripts/Invoke-ProjectChecks.ps1 -Suites Ui -Filter 'Category=VisualUi'`
и отдельный запуск с `-Filter 'Category!=VisualUi'`. Полный release-прогон использует все
шесть suites без фильтра. Health visual, keyboard и inspector collections исключают
параллельное выполнение во время своих изменений общих WPF resources.

1. **Zero warnings.** `TreatWarningsAsErrors=true` в `Directory.Build.props` для проектов решения;
   тесты не имеют права «сломать» сборку предупреждением.
2. **Никакого расхода модельной квоты.** Автотесты используют mocks/fixtures и изолированные
   реальные локальные ресурсы: SQLite, файловую систему, owned процессы и loopback HTTP.
   Реальные model calls отсутствуют; local resource completion не доказывает productive origin.
3. **Изоляция.** Integration-тесты используют временные каталоги (`TestDirectory`) и
   временные SQLite БД (`TestDatabase`); UI-тесты используют in-memory репозитории либо
   owned временные SQLite/host-каталоги. Они не открывают пользовательскую БД
   `%LOCALAPPDATA%\LLMWorkGUI`. Acceptance-скрипт отдельно сравнивает хеши файлов до/после;
   такое чтение fingerprint не доказывает отсутствие прочих чтений или transient writes.
4. **Детерминизм.** Основной regression не требует внешней сети, установленного backend CLI
   или настоящих аккаунтов: time providers, owned fake process harness и stub stores.
   Проверки OS/process/SQLite действительно используют соответствующие локальные ресурсы.
   Отдельные live no-model probes требуют установленного CLI и имеют собственные receipts;
   его отсутствие нельзя объявлять доказательством успешного native сценария.
5. **Артефакты.** Видимые UI-тесты сохраняют скриншоты; ошибки оставляют диагностируемый
   вывод.

Исторический baseline этого документа: **2,726 / 2,726 green, 0 failed, 0 skipped** (Release). Текущие counts и ограничения — в отчёте 01.10.2026 выше.

Текущий UI-контур сохраняет captures в `LLMWORKGUI_SCREENSHOT_DIR` (по умолчанию TestResults/Screenshots), UiInspector — в его подкаталог `ui-inspector`. Обычный прогон не переписывает `docs/work/screenshots`. Для intentional onboarding viewport/scrolling change используются 24 версионированных эталона `docs/acceptance/visual-baseline/onboarding-20261001`; остальные 90 inspector combinations используют прежние эталоны. Исходные 314 PNG сохранены. Pixel comparison, missing-file failure и one-pixel negative control остаются строгими; технические эталоны не являются owner sign-off. Focus acceptance выполняется в exclusive xUnit collection из-за общего WPF dispatcher/resources и активного input window.

---

## 2. Таксономия

### 2.1. Unit (`LLMWorkGUI.Domain.Tests`, `LLMWorkGUI.Application.Tests`)

- Domain (исторически 502; текущий прогон 576): state machines (Session/Execution/Health + Restore), guards, entities,
  value objects, workflow graph/roles, quota buckets, adaptation domain.
- Application (исторически 533; текущий прогон 1041): routing scoring/tie-break, health breaker/recovery/probe, quota
  scheduler/backoff, reconciliation, retention/long-running/soak contracts, diagnostics
  bundle, crash recovery, CLI version matrix, workflow adaptation parsing/diff, activity
  center search/index, observability projections.
- Характеристики: миллисекунды, без I/O, проверяют инварианты и типизированные отказы.

### 2.2. Contract (`LLMWorkGUI.Backends.ContractTests`)

- Sanitized protocol fixtures: OpenCode server/SSE/sessions/discovery, Cursor ACP
  handshake/session/prompt/stream/permission/cancel, StarCliProxy contract, Mirasim
  protocol, capability matrix и approval mapping.
- Ревизия нормативных документов: `ThreatModelRevisionTests`, `SecurityAndStorageContractTests`
  (STRIDE, TB-1…TB-6, URN-контракт, path traversal, отсутствие секретов и профиль-путей
  в артефактах).
- Contract-тесты фиксируют только реально подтверждённые возможности; отсутствующая
  операция явно помечается `Unsupported`, а не имитируется.

### 2.3. Integration (`LLMWorkGUI.IntegrationTests`)

- Реальный SQLite с production-миграциями: repository CRUD, migrations, retention,
  backup/restore, workflow import/export byte-identical, health event append-only.
- Реальный файловый I/O: blob store, scratch isolation, path traversal, ZIP safety.
- Реальные процессы через `ProcessSupervisor` и fake harness: argv boundaries, stdin
  policy, deadlock/overflow, process-tree termination, watchdogs.
- Composition roots: `AddApplication`/`AddInfrastructure`/`AddWorkflowServices`/
  `AddHardeningServices`/shell graphs.
- Packaging: `Packaging/InstallerLifecycleTests` запускает настоящие PowerShell-скрипты
  на изолированном временном профиле (install/update/rollback/uninstall, сохранение
  app-data, отказ при ошибках, создание ярлыков, неприкосновенность внешних CLI).

### 2.4. QuickViewer Visual UI (`LLMWorkGUI.Ui.Tests`)

- 13 visual-классов с сохранением скриншотов в `tests/LLMWorkGUI.Ui.Tests/Screenshots`:
  unified shell (three-pane, command palette, high-DPI), Providers & Accounts (+modal),
  Quotas, Health Center, Cursor workspace, Workflow library/studio/monitor/approval,
  Activity Center, Diff viewer, Onboarding, consolidated Phase 11 и empty/loading/error
  states.
- Рендеринг идёт на **реальных shipped XAML views** (`UnifiedWorkspaceShellView`,
  `ScreenTemplates.xaml` и т.д.) в STA; используются Dark/Light и 100/200% DPI.
- Test doubles — in-memory stores, fake time/theme/CLI/session services; никакого
  терминала, сети и модельных вызовов.
- Скриншоты — наблюдаемое доказательство для владельца продукта; headless-часть
  (view-model tests) отделена от визуальной.

### 2.5. Soak

- `QuotaPollingSoakTests` (Application) — 50–60 циклов через реальный scheduler с
  периодическими сбоями, backoff до 15-минутного cap, проверка отсутствия блокировки
  UI-потока (watchdog 30 секунд) и освобождения таймеров.
- `LongRunningExecutionTests` — heartbeat/timeout/cancel/failure и освобождение ресурсов.
- `RetentionCleanupTests` — TTL/архивация/неприкосновенность активных данных.

### 2.6. End-to-End

- `EndToEndHardeningScenarioTests` (`Workflows/`) — сквозной сценарий разработки на
  детерминированных заглушках: missing reviewer, конфликт `approve`/`reject`, изменение
  hash после review, отсутствие UI-артефакта, доработка и единогласное approve,
  переключение AGY `agy-profile` и Codex `CODEX_HOME` с новыми native sessions
  (`RequiresNewNativeSession`, `!CarriesPreviousSession`, `!CredentialsTransferred`),
  неизменность Mirasim snapshot, fail/restore run.
- Каждый blocker проверяется отдельно с собственным evidence (`BlockerId`).

---

## 3. Команды

```powershell
# Release build и последовательный полный прогон шести suites, TEMP/TRX/PNG на D:
pwsh -NoProfile -File scripts/Invoke-ProjectChecks.ps1

# Только unit / contract / integration / UI suites
& ./scripts/Invoke-ProjectChecks.ps1 -Suites @('Domain', 'Application')
& ./scripts/Invoke-ProjectChecks.ps1 -Suites @('Backends.Contract')
& ./scripts/Invoke-ProjectChecks.ps1 -Suites @('Integration') -Filter 'FullyQualifiedName~InstallerLifecycleTests'
& ./scripts/Invoke-ProjectChecks.ps1 -Suites @('Ui')
```

Команды требуют SDK из `global.json`; текущий RC проверяется также изолированным
.NET 10 SDK runner с SHA256 исходников/assemblies до и после прогона. `-SkipBuild`
допустим только для уже собранных проверяемых inputs.

Требование к окружению визуального прогона: разблокированный рабочий стол Windows (сессия
интерактивного пользователя), один GUI-драйвер за раз.

---

## 4. Изоляция чистого профиля

| Контур | Изоляция |
|---|---|
| UI-тесты | основной `UiTestHost`: in-memory repositories и layout store; тесты production DI/SQLite и startup harness получают собственный TEMP app-data до запуска приложения; `FakeTimeProvider`/`FakeCliDetectionService` используются по сценарию |
| Integration-тесты | `TestDirectory` (`%TEMP%\LLMWorkGUI.Tests\<guid>`) и `TestDatabase`; удаление в `Dispose` с ретраями |
| Installer-тесты | отдельный временный «профиль»: install/data/shortcut-каталоги, внешние CLI-заглушки; реальные Start Menu/Desktop не трогаются |
| Скриншоты | отдельный evidence-каталог через `LLMWORKGUI_SCREENSHOT_DIR`; исходные fixtures в `tests/LLMWorkGUI.Ui.Tests/Screenshots` сохраняются byte-identical |

Изолированные test directories и stores отделяют прогоны от данных приложения.
Проверка пути выполняется до создания evidence-каталога и запуска harness. До исправления
startup fixture один ранний прогон затронул metadata стандартного app-data; это зафиксировано
в [журнале завершения](../work/PROJECT_CLOSURE_EXECUTION_20261006.md). Требование изоляции
не служит утверждением, что исторические прогоны всегда его соблюдали.
Это не настоящий чистый Windows user profile: его проверка остаётся отдельной.
Артефакты и скриншоты намеренно сохраняются вне исходных reference PNG.

---

## 5. Live smoke и mocks

Live smoke-тесты **явно отделены** от детерминированных mocks:

| Тест | Что доказывает | Без CLI |
|---|---|---|
| `CursorAcpLiveSmokeTests` | discovery + ACP `initialize` handshake; prompt не отправляется | degraded resolution с blocker/guidance |
| `MirasimLiveSmokeTests` | read-only `GET /api/health` и каталог, без turns | типизированный отказ без throw |
| OpenCode integration (`IntegrationTests/OpenCode`) | session/stream/approvals против реального loopback-сервера и записанных sanitized fixtures | полностью детерминирован, без установленного CLI |

Задокументированный Phase 3 live smoke-сценарий OpenCode (`TASK_021_ACCEPTANCE.md`,
`WorkspaceSessionUiTests`) выполняется на детерминированных test doubles. Live smoke
классы Cursor/Mirasim никогда не расходуют модельную квоту и не выполняются без CLI.

---

## 6. Соответствие ТЗ §12

| Раздел ТЗ | Покрытие |
|---|---|
| 12.1 Unit | Domain.Tests + Application.Tests |
| 12.2 Contract | Backends.ContractTests (fixtures, malformed event, crash, Unsupported) |
| 12.3 Integration | IntegrationTests (server lifecycle, handshake, SQLite, recovery) |
| 12.4 UI | Ui.Tests headless + QuickViewer visual |
| 12.5 Soak/performance | QuotaPollingSoakTests, LongRunningExecutionTests, большие индексы Activity Center |
| 12.6 E2E | EndToEndHardeningScenarioTests, InstallerLifecycleTests |

01.10.2026 добавлены `StartupWorkflowRecoveryTests` и `ApplicationRunMarkerTests`:
clean/crash boundary, idempotency, view-only, ownership token и fresh reattachment.
`artifacts/remaining-work-20261001/restart-recovery/probe` запускает настоящий self-contained
exe с child-scoped app-data, seed только synthetic state, kill только созданного GUI PID,
restart и clean reopen. Это отдельная process evidence, не live model/native identity,
Windows reboot или fresh user profile. Финальный текущий прогон пяти suites и сохранённые
failed attempts описаны в [отчёте](../work/REPORT_RESTART_RECOVERY_20261001.md).

`StartupCliResponsivenessTests` проверяет синхронно заблокированный discovery, доступность
Input-priority dispatcher action, публикацию на STA и отмену late success/failure.
Класс использует существующую непараллельную UI collection, поскольку `StaTestRunner`
предоставляет общий dispatcher всему suite; конкурентный visual walk не должен подменять
проверяемую filesystem задержку. Packaged startup timings отдельно отмечают uncontrolled
cache state и не принимают cold-start target по warm restart.
