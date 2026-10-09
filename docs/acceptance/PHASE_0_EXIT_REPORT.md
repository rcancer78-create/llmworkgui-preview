# Phase 0 Exit Report — Release Gate Phase 0 → Phase 1

- **Статус:** Accepted (нормативный итоговый отчёт Phase 0)
- **Дата:** 2026-09-22
- **Задача:** TASK-006 (Phase 0 — discovery и архитектурные контракты)
- **Ремедиация:** TASK-007 (устранение замечаний независимого ревью Cursor Grok 4.7 High по C11, C12, C13)
- **Gate:** **Gate Phase 0 → Phase 1: OPEN**
- **Нормативные ссылки:** `ROADMAP.md` Phase 0 (`Exit criteria`, жёсткий gate); `TECHNICAL_SPECIFICATION.md` §2.1, §4.3, §5, §6.1-§6.16, §12.2
- **Итоговое решение:** все 14 обязательных критериев Phase 0 имеют статус `MET`; формальная приёмка Phase 0 завершена; первый product-code change Phase 1 разрешён.

## 1. Назначение

Отчёт фиксирует аудит всех обязательных результатов Phase 0, перечень принятых ADR, созданных fixtures, нормативных контрактов и тестов, а также официальное открытие release gate Phase 0 → Phase 1. Критерии ниже нормативны для приёмки; незакрытых критериев нет. По итогам независимого ревью Cursor Grok 4.7 High нормативные артефакты C11-C13 очищены от неподтверждённых предположений (capability states, Cursor approval fallback, checkout lock, BufferOverflow и evidence переходов).

## 2. Аудит обязательных критериев Phase 0

| ID | Criterion | Status | Evidence |
|---|---|---|---|
| `C1` | Solution skeleton, build-контракт и структура `docs/` созданы; clean build без warnings | MET | `LLMWorkGUI.sln`, `Directory.Build.props`, `docs/acceptance/TASK_003_ACCEPTANCE.md` |
| `C2` | Minimum supported Windows и .NET runtime зафиксированы | MET | `docs/adr/ADR-0001-runtime-and-cli-baseline.md` |
| `C3` | Фактические версии CLI и внешних компонентов зафиксированы | MET | `docs/adr/ADR-0001-runtime-and-cli-baseline.md`, `docs/protocols/capabilities/capability-matrix.md` |
| `C4` | OpenCode server API, event stream, sessions, approvals и cancellation исследованы; sanitized fixtures записаны | MET | `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`, `docs/protocols/opencode/server-api-inventory.json`, `docs/protocols/opencode/event-stream-sample.jsonl`, `docs/protocols/opencode/session-schema.json`, `docs/protocols/opencode/session-export-sample.json` |
| `C5` | OpenCode ACP проверен; serve-first transport сохранён, смена transport требует отдельного change proposal | MET | `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`, `docs/protocols/capabilities/capability-matrix.md` |
| `C6` | Cursor ACP handshake доказан; sanitized protocol fixtures записаны | MET | `docs/adr/ADR-0003-cursor-acp-lifecycle.md`, `docs/protocols/cursor/acp-handshake-response.json`, `docs/protocols/cursor/acp-session-new-schema.json`, `docs/protocols/cursor/acp-capabilities.json` |
| `C7` | Codex/AGY multi-account plugins перечислены; отсутствие plugin pin, observed route и безопасной shared topology задокументировано как ограничение | MET | `docs/protocols/capabilities/capability-matrix.md`, `docs/adr/ADR-0004-multi-account-and-quota-capabilities.md`, `docs/protocols/opencode/server-api-inventory.json` |
| `C8` | Определено, какие quota fields provider действительно раскрывает; фиктивные значения запрещены | MET | `docs/protocols/capabilities/quota-capability-inventory.json`, `docs/adr/ADR-0004-multi-account-and-quota-capabilities.md` |
| `C9` | Protocol fixtures и нормативные артефакты не содержат credentials и приватных путей; отсутствие секретов проверено контрактными тестами | MET | `tests/LLMWorkGUI.Backends.ContractTests/SecurityAndStorageContractTests.cs`, `tests/LLMWorkGUI.Backends.ContractTests/OpenCodeProtocolFixtureTests.cs`, `tests/LLMWorkGUI.Backends.ContractTests/CursorAcpProtocolFixtureTests.cs` |
| `C10` | Threat model, data-flow diagram, data-classification defaults и threat boundaries подтверждены | MET | `docs/architecture/THREAT_MODEL.md`, `docs/architecture/DATA_FLOW.md`, `docs/adr/ADR-0005-secret-storage-windows.md` |
| `C11` | Capability matrix составлена в состояниях `Supported/Unsupported/Unknown` без фиктивных fork/resume/quota значений | MET | `docs/protocols/capabilities/capability-matrix.md`, `docs/protocols/cursor/acp-capabilities.json`, `docs/protocols/capabilities/multi-account-routing-contract.json`, `tests/LLMWorkGUI.Backends.ContractTests/CapabilityMatrixContractTests.cs` (ревью Grok: cursor.session.stream/cancel, codex multi-account profiles, agy.viaOpenCode и acp-обмены честно переведены в Unknown) |
| `C12` | Session/Execution/Health transition tables нормативны, сверены с protocol evidence и готовы к кодированию без изменения инвариантов ТЗ | MET | `docs/architecture/STATE_TRANSITION_TABLES.md`, `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`, `docs/adr/ADR-0003-cursor-acp-lifecycle.md` (ревью Grok: BufferOverflow = причина Failed, Idle→Active = старт нового Execution, CoolingDown→ProbeRequired по clear cooldown, lock снимается только по terminal outcome) |
| `C13` | Normalized approval mapping OpenCode/Cursor с обязательным fallback `UnknownHighRisk` и внешний контракт optional legacy entrypoint задокументированы | MET | `docs/protocols/capabilities/approval-mapping.md`, `docs/protocols/capabilities/legacy-entrypoint-boundary.md`, `tests/LLMWorkGUI.Backends.ContractTests/StateAndApprovalContractTests.cs` (ревью Grok: per-kind таблица Cursor снята, любой Cursor approval = UnknownHighRisk без persistent rules и allow-for-execution) |
| `C14` | Architecture и security ADR приняты; для каждого обязательного discovery-вопроса есть проверенный результат либо документированный `Unsupported/Blocked`; формальная приёмка Phase 0 зафиксирована | MET | `docs/adr/ADR-0001-runtime-and-cli-baseline.md`, `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`, `docs/adr/ADR-0003-cursor-acp-lifecycle.md`, `docs/adr/ADR-0004-multi-account-and-quota-capabilities.md`, `docs/adr/ADR-0005-secret-storage-windows.md`, `docs/adr/ADR-0006-workflow-blob-and-version-storage.md`, `docs/acceptance/TASK_004_ACCEPTANCE.md`, `docs/acceptance/TASK_005_ACCEPTANCE.md` |

## 3. Принятые ADR

| ADR | Тема | Статус |
|---|---|---|
| `ADR-0001` | Runtime и CLI baseline (minimum Windows/.NET, версии CLI) | Accepted |
| `ADR-0002` | OpenCode serve: topology и API, missing operations | Accepted |
| `ADR-0003` | Cursor ACP lifecycle | Accepted |
| `ADR-0004` | Multi-account и quota capabilities | Accepted |
| `ADR-0005` | Secret storage Windows (Credential Manager / DPAPI, reference-only URN) | Accepted |
| `ADR-0006` | Workflow blob и version storage (immutable SHA-256, scratch isolation) | Accepted |

## 4. Нормативные артефакты Phase 0

1. Protocol fixtures: `docs/protocols/opencode/server-api-inventory.json`, `docs/protocols/opencode/event-stream-sample.jsonl`, `docs/protocols/cursor/acp-handshake-response.json`, `docs/protocols/cursor/acp-capabilities.json`.
2. Capability и routing: `docs/protocols/capabilities/capability-matrix.md`, `docs/protocols/capabilities/quota-capability-inventory.json`, `docs/protocols/capabilities/multi-account-routing-contract.json`.
3. Architecture и security: `docs/architecture/THREAT_MODEL.md`, `docs/architecture/DATA_FLOW.md`, `docs/architecture/STATE_TRANSITION_TABLES.md`.
4. Approvals и legacy: `docs/protocols/capabilities/approval-mapping.md`, `docs/protocols/capabilities/legacy-entrypoint-boundary.md`.
5. Контрактные тесты: `tests/LLMWorkGUI.Backends.ContractTests/StateAndApprovalContractTests.cs`, `tests/LLMWorkGUI.Backends.ContractTests/CapabilityMatrixContractTests.cs`, `tests/LLMWorkGUI.Backends.ContractTests/SecurityAndStorageContractTests.cs`.
6. Baseline компонентов: `README.md` — таблица исполняемых файлов и версий из `ADR-0001` с явной пометкой, что это discovery evidence Phase 0, а не вечный контракт.

## 5. Документированные ограничения и отложенные работы

1. Legacy entrypoint: внешний контракт зафиксирован, runtime evidence отложено до Phase 8 (Milestone 10B) — допустимое ограничение Phase 0, не блокирующее gate.
2. OpenCode session close/delete не подтверждён; Reset реализуется как новая native session с сохранением истории (Phase 3 перепроверяет `/doc`).
3. Cursor native fork остаётся `Unknown`; фиктивные fork fixtures запрещены.
4. Quota API отсутствует у OpenCode и Cursor; строки Dashboard честно показывают `Unsupported/Unknown`.
5. Shared OpenCode server запрещён до отдельного spike с доказательством credential isolation.

## 6. Открытие gate

**Gate Phase 0 → Phase 1: OPEN.** Все 14 обязательных критериев имеют статус `MET`, артефакты сохранены, контрактные тесты проходят. Замечания независимого ревью Cursor Grok 4.7 High по C11, C12 и C13 устранены: неподтверждённые capabilities честно переведены в `Unknown`, Cursor approvals используют строгий fallback `UnknownHighRisk`, checkout lock и `BufferOverflow` приведены в соответствие ТЗ, baseline компонентов опубликован в `README.md`. Разрешён первый product-code change Phase 1 согласно `ROADMAP.md`.

Phase-gate provenance: модель `grok-4.7-high`; существующий review был оформлен как `INDEPENDENT_HIGH_RISK_REVIEW` до введения обязательного `PHASE_GATE_REVIEW` и после corrective pass считается эквивалентом `PASS` только для Phase 0. Cursor chat/session ID в сохранённых артефактах отсутствует и фиксируется как `UNKNOWN`. Начиная с Phase 1 обязательны явные `PHASE_VERDICT`, session/chat ID и reviewed commit range/baseline.

Принятый baseline Phase 0 зафиксирован локальным commit `4b0f9a419f793253341099878c68e7184ba7c5f1`. Remote и push не создавались.

## 7. Evidence index

- `TECHNICAL_SPECIFICATION.md`
- `ROADMAP.md`
- `README.md`
- `docs/adr/ADR-0001-runtime-and-cli-baseline.md`
- `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`
- `docs/adr/ADR-0003-cursor-acp-lifecycle.md`
- `docs/adr/ADR-0004-multi-account-and-quota-capabilities.md`
- `docs/adr/ADR-0005-secret-storage-windows.md`
- `docs/adr/ADR-0006-workflow-blob-and-version-storage.md`
- `docs/architecture/THREAT_MODEL.md`
- `docs/architecture/DATA_FLOW.md`
- `docs/architecture/STATE_TRANSITION_TABLES.md`
- `docs/protocols/capabilities/capability-matrix.md`
- `docs/protocols/capabilities/quota-capability-inventory.json`
- `docs/protocols/capabilities/multi-account-routing-contract.json`
- `docs/protocols/capabilities/approval-mapping.md`
- `docs/protocols/capabilities/legacy-entrypoint-boundary.md`
- `docs/acceptance/TASK_003_ACCEPTANCE.md`
- `docs/acceptance/TASK_004_ACCEPTANCE.md`
- `docs/acceptance/TASK_005_ACCEPTANCE.md`
