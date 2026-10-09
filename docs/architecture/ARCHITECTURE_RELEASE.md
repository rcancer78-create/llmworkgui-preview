# LLM Work GUI — Architecture (Release Candidate 1.0)

> Рабочее дерево 06.10.2026: targets .NET 10 по [ADR-0011](../adr/ADR-0011-net10-runtime-and-sqlite-bundle.md). Runtime-описания .NET 8 и результаты RC ниже — исторический срез опубликованных локальных архивов, не приёмка текущего дерева.


> Исторический статус 02.10.2026: последний принятый bounded кандидат **1.0.0-rc.20261002.3**; [исправления и проверки кандидата](../work/REPORT_FINAL_CANDIDATE_20261002.md). Fresh frozen full4196/4196, Release0warnings/errors, package install/update/startup/rollback/uninstall PASS, visible129PASS/0FAIL/1NOT_TESTED на8 package-identical modules, Space Bunny MAX PASS. Phase/release/owner gates OPEN; security gate фиксируется в artifacts/remaining-work-20261002/security-snapshot/STATUS.md; frozen rc.11 audit остаётся историческим. Cold-start DEFERRED_BY_OWNER. Новые TEMP/TMP, helpers и review/test artifacts — на D:.


> Исторический статус 01.10.2026: Phase10/11/12 OPEN. Положительный reviewer route заблокирован отсутствием native identity protocol. [rc.11 evidence](../work/REPORT_OPENCODE_SPOOL_ISOLATION_20261001.md), [полная traceability](../acceptance/RELEASE_TRACEABILITY_20261001.md).

**Статус:** актуально для Phase 12 / Release Candidate 1.0
**Связанное ТЗ:** `TECHNICAL_SPECIFICATION.md` §§4–10
**Roadmap:** `ROADMAP.md` Phases 0–12
**ADR:** `docs/adr/ADR-0001…ADR-0011`

Документ описывает компонентную архитектуру всех подсистем, реализованных в Phases 1–12, и
границы между ними. Детали безопасности, диагностики и тестирования вынесены в
`SECURITY_GUIDE.md`, `DIAGNOSTICS_GUIDE.md`, `TESTING_STRATEGY.md`.

---

## 1. Принципы

1. **Слоистость.** Domain не знает о внешнем мире; Application описывает контракты и
   сценарии; Infrastructure реализует адаптеры; App содержит только presentation/UI.
2. **Evidence-first.** Любое состояние (session, execution, route, account, health)
   считается доказанным только при наличии наблюдаемого evidence. Неизвестные поля
   отображаются как `Not reported`; silent fallback запрещён.
3. **Fail-closed.** Неизвестные capability/approval/route/mismatch ведут к degraded или
   блокировке, а не к оптимистичному продолжению.
4. **Append-only audit.** Health events, evidence переходов, approval decisions и
   recovery-записи не переписываются.
5. **Никаких секретов в артефактах.** Секреты существуют только в DPAPI/Credential
   Manager; наружу — reference-only URN и redaction.
6. **Zero warnings.** `TreatWarningsAsErrors` на проектах решения; актуальный состав определяется `LLMWorkGUI.sln`, включая импортированные Gateway и acceptance tools.

---

## 2. Компонентная схема

```
┌───────────────────────────────────────────────────────────────────────────────┐
│ LLMWorkGUI.App (WPF, net10.0-windows)                                          │
│  Unified three-pane shell · Command Palette · Screens (Ctrl+1..Ctrl+0)        │
│  Workflow Studio · Activity Center · Diff/Artifact viewers · Onboarding       │
│  Health/Quotas/Providers/Workflows/Mirasim/Cursor view models · DI-корень UI  │
└───────────────▲───────────────────────────────────────────────────────────────┘
                │ контракты Application
┌───────────────┴───────────────────────────────────────────────────────────────┐
│ LLMWorkGUI.Application (net10.0)                                               │
│  Routing · Health · Quotas · Accounts · Providers · Processes/Watchdogs       │
│  Executions · Concurrency · Reconciliation · Observability                    │
│  Retention/Lifecycle · Diagnostics (contracts) · Data (contracts)             │
│  Workflows (import/adaptation/run/studio/declarative/legacy/E2E)              │
│  Security (ISecretStore, SecretReference) · Cli                          │
└───────────────▲───────────────────────────────────────────────────────────────┘
                │ реализации контрактов
┌───────────────┴───────────────────────────────────────────────────────────────┐
│ LLMWorkGUI.Infrastructure (net10.0)                                            │
│  SQLite repositories + migrations · Credential Manager / DPAPI fallback · Storage/blobs        │
│  Process supervisor · Backend adapters: OpenCode, Cursor ACP, StarCliProxy,   │
│  Mirasim / NativeGateway · Diagnostics bundle · Backup/restore · Retention/Crash recovery     │
│  Redacting logging · Mock servers · Host bootstrapper                         │
└───────┬──────────────────┬──────────────────┬──────────────────┬──────────────┘
        │                  │                  │                  │
  opencode serve      cursor-agent acp    star-cliproxy      Mirasim host
  (loopback HTTP/SSE) (JSON-RPC stdio)    (loopback HTTP)    (user-owned HTTP)
```

---

## 3. Проекты решения

| Проект | TFM | Роль |
|---|---|---|
| `src/LLMWorkGUI.Domain` | net10.0 | Entities, enums, value objects, state machines, invariants |
| `src/LLMWorkGUI.Application` | net10.0 | Контракты и сценарии; не зависит от Infrastructure |
| `src/LLMWorkGUI.Infrastructure` | net10.0 | SQLite, DPAPI, процессы, backends, diagnostics, logging |
| `src/LLMWorkGUI.App` | net10.0-windows | WPF presentation, view models, темы, навигация, DI UI-графа |
| `src/LLMWorkGUI.Backends.Abstractions` | net10.0 | Общие backend-контракты (OpenCode, Cursor ACP, Mirasim, StarCliProxy) |
| `src/LLMWorkGUI.Backends.OpenCode` | net10.0 | OpenCode serve client/discovery/events/health |
| `src/LLMWorkGUI.Backends.CursorAcp` | net10.0 | Cursor ACP lifecycle, handshake, model policy, turn supervisor |
| `src/LLMWorkGUI.Workflows` | net10.0 | Workflow assembly-маркер/утилиты |
| `tests/LLMWorkGUI.Domain.Tests` | net10.0 | Unit: domain invariants и state machines |
| `tests/LLMWorkGUI.Application.Tests` | net10.0 | Unit: Application-сценарии и сервисы |
| `tests/LLMWorkGUI.Backends.ContractTests` | net10.0 | Contract: fixtures, capability matrix, threat-model ревизия |
| `tests/LLMWorkGUI.IntegrationTests` | net10.0 | Integration: SQLite, процессы, lifecycle, DI, packaging |
| `tests/LLMWorkGUI.Ui.Tests` | net10.0-windows | Headless + QuickViewer visual UI-сценарии |

---

## 4. Domain

- **Entities:** Project, ProviderProfile, BackendInstance, Account, ModelDescriptor, Route,
  Session, ClientRequest, Execution, ProjectLock, ApprovalRule, QuotaSnapshot,
  WorkflowPackage, WorkflowVersion, WorkflowBinding, WorkflowRun, SemanticRoleMapping.
- **State machines:** `SessionStateMachine`, `ExecutionStateMachine`, `HealthStateMachine`
  (с `Restore`/`AccountedErrorClass` для регидратации).
- **Нормативные enum-ы:** `SessionState`, `ExecutionState`, `HealthState`,
  `ReconciliationOutcome`, `ExecutionFailureReason`, `AuthState`, `CapabilityState`,
  `NormalizedApprovalKind`, `QuotaProvenance/Confidence/Unit/Window`, `RoutingPolicy`,
  `WorkflowRole/StageKind/NodeKind/RunState/TerminalOutcome/ReviewVerdict`,
  `AdaptationGoal/BlockerKind`, `DocumentTemplateKind`, `UserApprovalDecision`.
- **Value objects:** SessionBinding, QuotaBucket, UserApprovalEvidence, ReviewerVerdictRecord,
  RoleBindingDefinition, CodingStageTransitionRule, WorkflowGraph/Node/Stage/Template,
  WorkflowDocumentDraft, WorkflowTransitionRecord.
- **Инварианты:** `DomainGuard` валидирует идентификаторы/переходы; недопустимый переход
  выбрасывает `InvalidStateTransitionException`; `ProjectLockConflictException` — конфликт
  checkout lock.

---

## 5. Application

| Подсистема | Ключевые контракты |
|---|---|
| Security | `ISecretStore`, `SecretReference` (`urn:llmworkgui:secret:<id>`) |
| Configuration | `StorageOptions`, `RetentionOptions` + validator |
| Repositories | `IProjectRepository`, `ISessionRepository`, `IExecutionRepository`, `IHealthStateRepository`, `IHealthEventRepository`, `IProjectLockRepository`, `IApplicationSettingsRepository`, `IProviderProfileRepository`, `IApprovalRuleRepository`, `IAccountRepository`, `IQuotaSnapshotRepository`, `IWorkflow*Repository` |
| Accounts | `IAccountBridge`, `IAccountContextManager`, `AccountPinResult`, `AccountAuthProbeResult` |
| Quotas | `IQuotaSourceAdapter`, `IQuotaRefreshScheduler`, `IQuotaPollingSoakRunner`, `QuotaSoakReport` |
| Routing | `IRoutingEngine`, `RoutingDecision`, `CandidateScore`, `RejectedCandidateExplanation`, `BalancedScoringWeights` |
| Health | `IHealthCenterService`, `IHealthProbeService`, `IImpactedSessionService`, `IModelProbeExecutor`, `HealthPolicyProvider`, `StickyRouteTurnGate` |
| Processes | `IProcessSupervisor` (реализация в Infrastructure), `ProcessStartSpecification`, `ProcessStdinPolicy`, `IProtocolProcessSession` |
| Watchdogs | startup/liveness/heartbeat/turn timeout политики |
| Concurrency | named OS mutex, `IApplicationInstanceGuard`, single-supervisor guard, writer lock контракты |
| Executions | execution tracking, bounded retry, audit |
| Reconciliation | `IReconciliationService`, recovery matrix (`Reattached/Orphaned/Ambiguous/BackendMissing`) |
| Observability | `ObservableRunProjection`, `IActivityTimelineService`, `IActivityCenterService`, `IEventSearchIndex`, `IWorkflowRunTimelineService`, `RoleTransferEvidence` |
| Lifecycle | `IRetentionCleanupService`, `ILongRunningExecutionService`, `IAppCrashRecoveryService` |
| Diagnostics | `IDiagnosticBundleService`, `DiagnosticBundleRequest/Preview/Result/Manifest/BlockedException` |
| Data | `IDatabaseBackupService`, integrity/restore результаты |
| Providers | connection test, model refresh/detector, plugin inventory, diagnostic export, `CliVersionValidator` |
| Workflows | import/export/preview/manifest/scratch, binding/activation, adaptation, bindings, `IWorkflowRunService`, `IWorkflowExecutionPlanService`, `IPreCoderGateValidator`, `IWorkflowStudioService`, `ISupervisedLegacyWorkflowRunner`, `IEndToEndWorkflowScenarioRunner` |

---

## 6. Infrastructure

### 6.1 Хранение и БД

- `SqliteConnectionFactory` + `DatabaseMigrator` + последовательная цепочка `Migrations/001_InitialSchema.sql`…`027_*.sql` до schema 27;
  versioned schema, `PRAGMA foreign_keys`, WAL.
- Репозитории — отдельные `Sqlite*Repository` с параметризованными запросами; immutable
  workflow versions (`ON CONFLICT (Id) DO NOTHING`), foreign-key delete protection.
- `AppDataPaths` — единственная точка вычисления путей app-data: `%LOCALAPPDATA%\LLMWorkGUI`
  (`secrets`, `blobs`, `logs`, `diagnostics`, `runs`, `llmworkgui.db`).
- `AtomicFile`, `WorkflowBlobStore` (content-addressed `blobs/sha256/xx/<hex>`),
  `ScratchWorkspaceManager` с отдельными выданными каталогами (`scratch/<scope>/<scopeId>-<GUID>`).

### 6.2 Процессы и transport

- `ProcessSupervisor` — типизированный argv без shell interpolation, async stdout/stderr,
  bounded buffers + spool, graceful cancel, process-tree termination, startup grace window.
- Backend adapters:
  - `Backends.OpenCode`: discovery, managed serve lifecycle, API/SSE client, session
    lifecycle, route verification, health event collector;
  - `Backends.CursorAcp`: executable resolver, ACP handshake validator, JSON-RPC stdio
    transport, process manager, model catalog/selector/mode policy, turn supervisor,
    session lifecycle;
  - `StarCliProxy`: typed client (`/v1/models`, SSE chat completions), managed loopback
    server manager, health, account context manager (CODEX_HOME/agy-profile);
  - `Mirasim`: host discovery/auth, harness/model catalog, turn protocol, health, sessions.
  - `NativeGateway`: native adapters LLMGateway/GrokBot, импорт account/catalog без
    автоматической активации, durable project admission и exact dispatch grant. Библиотечный
    HTTP API не равен project-authorized transport; продуктивные origin/cost и generic workflow
    route не подтверждаются импортом или локальным no-model probe. См. [egress inventory](../work/EGRESS_PATH_INVENTORY_20261006.md).

### 6.3 Диагностика, recovery, retention

- `DiagnosticBundleService` — сбор environment/storage/CLI/schema/health/logs/run output,
  маскирование `%USERPROFILE%`, `SensitiveDataFilter` + `WorkflowSecretScanner`, экспорт
  только после preview (TTL), блокировка при surviving finding.
- `DatabaseBackupService` — `VACUUM INTO`, SHA-256 sidecar, `PRAGMA integrity_check`,
  restore с rollback-снапшотом и безопасным отказом.
- `AppCrashRecoveryService` — терминализация executions/sessions/runs, освобождение stale
  locks только при безопасности, health rehydration.
- `RetentionCleanupService` — TTL-очистка диагностических архивов/scratch/audit с
  архивацией append-only JSONL и запретом удаления active/referenced данных.

### 6.4 Логирование

`RedactingLoggerProvider`/`RedactingLogger` + `RedactedLogState`/`RedactedException`
фильтруют секреты и профиль-пути на уровне structured state; `CapturingLoggerProvider`
используется тестами.

---

## 7. App (WPF)

- `UnifiedWorkspaceShellView` — единый three-pane shell: навигация слева, рабочая
  область центр, инспектор/контекст справа; compact status bar с health/quotas/CLI.
- `ScreenCatalog` — 10 экранов (`Workspace`, `Projects`, `ProvidersAccounts`, `Models`,
  `Quotas`, `Sessions`, `Runs`, `Workflows`, `HealthCenter`, `SettingsDiagnostics`).
- `CommandPaletteViewModel` — поиск/запуск команд, Ctrl+K, полный набор навигации и
  действий.
- Панели/экраны: `WorkspaceViewModel`, `CursorWorkspaceViewModel`,
  `MirasimWorkspaceViewModel`, `WorkflowLibraryViewModel`, `WorkflowStudioViewModel`,
  `WorkflowActivityMonitorViewModel`, `ActivityCenterViewModel`,
  `DiffArtifactViewerViewModel`, `HealthCenterViewModel`, `QuotasViewModel`,
  `ProvidersAccountsViewModel`, `SettingsDiagnosticsViewModel`,
  `HardeningDiagnosticsViewModel`, `OnboardingViewModel`.
- Темы/DPI: `ThemeService`, `ThemeResourceApplier`, Dark/Light/system,
  100/150/200%.
- Layout persistence и Clipboard abstraction — через `ILayoutPersistenceService` /
  `IClipboardService` (тестируемость без UI).

---

## 8. Состояния, evidence и gates

- **Session lifecycle:** create → confirm (native ID) → continue/reset; parent/child
  ancestry append-only; `Orphaned`/`Ambiguous` не маскируются.
- **Execution:** startup/liveness отдельно от session confirmation и turn timeout;
  terminal outcome отдельно от workflow outcome.
- **Health:** rolling metrics + circuit breaker; cooldown → `ProbeRequired`, не `Healthy`;
  manual probe — собственный execution; `ForcedEnabled` без проверки отличается от
  `VerifiedRecovery`.
- **Pre-Coder Approval Gate:** обязательные документы (ТЗ/roadmap/architecture и т.п.),
  единогласные `approve` reviewer на текущий hash, пользовательское approve, опциональный
  UI-artifact; конфликт вердиктов, missing reviewer, hash mismatch и missing UI artifact
  блокируют переход отдельно и доказуемо.
- **Account switching:** AGY `agy-profile` и Codex `CODEX_HOME` переключаются под writer
  lock, только когда нет живых связанных executions; новая native session без переноса
  credentials и без влияния на Mirasim host.

---

## 9. Упаковка и жизненный цикл

- `scripts/Publish-LLMWorkGUI.ps1` — self-contained win-x64 publish + `version.json`.
- `scripts/LLMWorkGUI.Packaging.psm1` — install/update/rollback/uninstall примитивы,
  `install-state.json`, `.rollback` snapshot, атомарные staging-переключения.
- Per-user пути, отсутствие elevation/реестра, сохранение app-data, опциональное
  `-RemoveUserData`. Решение зафиксировано в `ADR-0009`.

---

## 10. Покрытие фаз

| Phase | Подсистема |
|---|---|
| 0 | ADR-0001…0006, capability matrix, fixtures, threat model, transition tables |
| 1 | Solution skeleton, Domain/DI/SQLite/DPAPI, shell, observable projection |
| 2 | Process supervisor, watchdogs, writer lock, reconciliation |
| 3 | OpenCode adapter (serve/SSE/sessions/approvals/cancel) |
| 4 | Providers & Accounts UI, connection test, plugin inventory, model refresh |
| 5/5R | Accounts/quota/routing; refactor Codex/AGY на star-cliproxy |
| 6 | Cursor ACP adapter, model overrides, approvals, cancel, live smoke |
| 7/7M | Health Center/recovery; Mirasim backend |
| 8 | Workflow library: import/export, immutable blobs, preview, binding |
| 9 | Adaptation engine: catalog, prompt contract, secret scan, diff, activation |
| 10 | Workflow run aggregate, legacy runner, declarative nodes, monitor, Studio |
| 11 | UX consolidation: unified shell, palette, activity center, diff viewers, onboarding |
| 12 | Hardening: threat review, secret scan, diagnostics, backup/restore, crash recovery, version matrix, long-running/soak/retention/E2E, installer, release acceptance |

Уточнение rc.20261001.5: `App` владеет `ApplicationRunMarker` рядом с конкретной SQLite
базой и снимает его только после успешного graceful host stop. `HostBootstrapper` получает
сигнал interrupted lifetime; session/execution reconciliation предшествует узкому workflow
recovery. Broad diagnostic recovery не вызывается автоматически. Fresh Reattached может
сохранить linked run; stale persisted flag не может. Secondary instance не меняет marker
или workflow recovery. [Проверки и границы](../work/REPORT_RESTART_RECOVERY_20261001.md).

Уточнение rc.20261001.6: optional CLI discovery выполняется после показа shell и yield
ниже render/input priority. `CliStatusViewModel` запускает detection на worker, поскольку
Task-returning locator синхронно обращается к filesystem/PATH. UI publication возвращается
на исходный dispatcher; отменённый GUI lifetime не публикует запоздалый snapshot/failure.
Migration, native reconciliation, crash recovery и theme initialization остаются до `Show`.
[Проверки и замеры](../work/REPORT_STARTUP_RESPONSIVENESS_20261001.md).
