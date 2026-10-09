# Optional supervised legacy entrypoint — внешний контракт и границы

- **Статус:** Accepted (нормативный контракт Phase 0; runtime evidence отложено до Phase 8)
- **Дата:** 2026-09-22
- **Задача:** TASK-006 (Phase 0 — discovery и архитектурные контракты)
- **Нормативные ссылки:** `TECHNICAL_SPECIFICATION.md` §6.8, §6.13, §6.15, §6.16, §10; `ROADMAP.md` Phase 0, Phase 10 Milestone 10B
- **Связанные артефакты:** `docs/adr/ADR-0006-workflow-blob-and-version-storage.md`, `docs/architecture/DATA_FLOW.md`, `docs/architecture/STATE_TRANSITION_TABLES.md`, `docs/architecture/THREAT_MODEL.md`
- **Контрактные тесты:** `tests/LLMWorkGUI.Backends.ContractTests/StateAndApprovalContractTests.cs`

## 1. Назначение и discovery status

1. Документ фиксирует внешний контракт optional supervised legacy entrypoint: запуск, terminal outcome, artifacts и поля `Not reported`, не меняя содержимое workflow (§6.15).
2. В Phase 0 package с user-declared entrypoint не предоставлен; runtime evidence запуска не получено. `ROADMAP.md` Phase 0 явно допускает отложить это evidence до Phase 8 без блокировки остальных результатов Phase 0.
3. Контракт ниже нормативен уже сейчас; факты исполнения не заявляются. До появления проверенного evidence legacy entrypoint не считается `Supported`.
4. Отложенные evidence-пункты перечислены в §8 и не маскируются предположениями.

## 2. Scope и запреты

1. Legacy workflow исполняется только при явно выбранном пользователем entrypoint; приложение не выбирает entrypoint автоматически и не изменяет workflow.
2. Приложение само не вызывает прямые Codex CLI/AGY CLI: entrypoint запускается как один supervised opaque process, а все дочерние процессы входят в его process tree.
3. Приложение не переписывает роли, stages, файлы и manifest workflow; source blob всегда открывается только для чтения.
4. Приложение не извлекает stage/role/route/session из вывода entrypoint без проверенного контракта; всё неподтверждённое показывается как `Not reported`.
5. Приложение не выполняет model calls для legacy run и не проксирует трафик entrypoint.

## 3. Контракт запуска

1. Workspace запуска — изолированная scratch-копия `<app-data>/scratch/run/<id>`; она создаётся вне project root/checkout и вне blob store, с path traversal guard.
2. Source blob открывается read-only; после операции выполняется post-operation source hash check (SHA-256): расхождение — failure, а не warning.
3. До старта берётся checkout writer lock: scope — канонический нормализованный корень checkout; владелец — `executionId`, instance ID приложения и generation управляемого процесса.
4. Lock освобождается только по terminal outcome либо по завершённой reconciliation. Stale lock не снимается автоматически, пока execution имеет `Orphaned` или `Ambiguous`; второй writer в том же checkout запрещён без исключений.
5. Жизненный цикл процесса принадлежит Process Supervisor: запуск без видимой консоли, неблокирующее чтение stdout/stderr, process startup timeout, liveness и orphan reconciliation.
6. Завершение — graceful cancel первым, затем bounded forced termination только своего подтверждённого дерева процессов; turn hard timeout не применяется к живому процессу, heartbeat в stdin протокола не пишется.
7. Секреты не попадают в command line/argv: только scoped environment конкретного child process; любой вывод проходит redaction до записи.

## 4. Terminal outcome и artifacts

| Field | Source | Нормативное правило |
|---|---|---|
| `processState` | Process Supervisor | spawned/running/exited/terminated; подтверждается lifecycle evidence |
| `exitCode` | процесс | показывается как есть; при forced termination без exit code — `Not reported` |
| `terminationReason` | Process Supervisor | graceful cancel, forced termination, crash или normal exit |
| `startedAt`, `endedAt`, `elapsed` | Process Supervisor | монотонные значения из lifecycle events |
| `artifacts` | scratch-копия после terminal outcome | перечисляются найденные artifacts; отсутствие artifact не считается успехом |
| `sourceHashBefore`, `sourceHashAfter` | blob store | обязаны совпасть; расхождение завершает run failure-статусом |

1. Terminal workflow outcome отличается от успешного завершения отдельного executor: exit code 0 не доказывает успех workflow.
2. Terminal outcome фиксируется в audit с correlation IDs и redacted payload.
3. `Succeeded` для legacy run не изобретает native session, route, role или stage.

## 5. Поля `Not reported`

Пока entrypoint не сообщает поле по проверенному контракту, UI показывает ровно `Not reported` и не синтезирует значение.

| Field | Default value | Condition when reported |
|---|---|---|
| `nativeSessionId` | `Not reported` | entrypoint reports native session ID through verified contract (Phase 8) |
| `requestedRoute` | `Not reported` | entrypoint reports requested route through verified contract (Phase 8) |
| `observedRoute` | `Not reported` | entrypoint reports observed route through verified contract (Phase 8) |
| `role` | `Not reported` | entrypoint reports role through verified contract (Phase 8) |
| `stage` | `Not reported` | entrypoint reports stage through verified contract (Phase 8) |
| `usage` | `Not reported` | entrypoint reports usage through verified contract (Phase 8) |
| `quota` | `Not reported` | entrypoint reports quota snapshot through verified contract (Phase 8) |
| `approvals` | `Not reported` | opaque process handles own approvals; GUI mapping только после проверенного контракта (Phase 8) |

## 6. Process Supervisor reconciliation

| Outcome | Guard | Результат legacy run |
|---|---|---|
| `Reattached` | process tree и run directory подтверждены, owner lock и generation совпали | run продолжает наблюдаться; terminal outcome ожидается |
| `Orphaned` | процесс завершился без terminal record, artifacts в scratch доступны | run помечается `Orphaned`; требуется recovery decision |
| `Ambiguous` | terminal evidence отсутствует, доставка/завершение не подтверждены | run помечается `Ambiguous`; auto-retry запрещён; lock не снимается автоматически |
| `BackendMissing` | executable или managed instance отсутствует | run помечается `Orphaned`; recovery action с evidence |

1. Reconciliation выполняется до снятия writer lock и до повторного запуска entrypoint.
2. Автоматический повтор legacy run запрещён; ручной повтор создаёт новый run и новую scratch-копию.

## 7. Запрещённые сокращения

1. Запуск entrypoint в project checkout или в blob store.
2. Изменение source blob или обход post-operation source hash check.
3. Параллельные writers в одном checkout и снятие stale lock до reconciliation.
4. Синтетические session/route/role/stage значения вместо `Not reported`.
5. Прямой вызов Codex CLI/AGY CLI приложением в обход supervised entrypoint.

## 8. Отложенное evidence до Phase 8

Следующие пункты помечены `UNVERIFIED_PHASE_8` и обязаны быть проверены в Phase 8 Milestone 10B; Phase 0 не заявляет их выполненными:

1. реальный запуск предоставленного entrypoint в scratch-копии;
2. terminal outcome, exit code и список artifacts реального run;
3. поведение checkout writer lock при параллельном writer;
4. post-operation source hash check после run;
5. reconciliation outcomes `Reattached`/`Orphaned`/`Ambiguous`/`BackendMissing` на реальном процессе.

## 9. Контрактные тесты

Инварианты этого документа проверяются классом `tests/LLMWorkGUI.Backends.ContractTests/StateAndApprovalContractTests.cs`:

- изоляция scratch `<app-data>/scratch/run/<id>` и read-only source blob;
- checkout writer lock, single supervised process tree и Process Supervisor reconciliation;
- terminal outcome поля и полный набор `Not reported` полей;
- запрет прямого вызова Codex CLI/AGY CLI и явное отложение evidence до Phase 8.

## 10. Evidence index

- `TECHNICAL_SPECIFICATION.md`
- `ROADMAP.md`
- `docs/adr/ADR-0006-workflow-blob-and-version-storage.md`
- `docs/architecture/DATA_FLOW.md`
- `docs/architecture/STATE_TRANSITION_TABLES.md`
- `docs/architecture/THREAT_MODEL.md`
- `docs/protocols/capabilities/capability-matrix.md`
