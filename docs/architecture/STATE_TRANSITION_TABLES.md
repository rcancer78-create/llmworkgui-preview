# LLM Work GUI — State Transition Tables: Session, Execution, Health

- **Статус:** Accepted (нормативный артефакт Phase 0)
- **Дата:** 2026-09-22
- **Задача:** TASK-006 (Phase 0 — discovery и архитектурные контракты)
- **Нормативные ссылки:** `TECHNICAL_SPECIFICATION.md` §6.7, §6.8, §6.9, §6.10, §6.11, §6.12, §6.15; `ROADMAP.md` Phase 0, Phase 1
- **Связанные артефакты:** `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`, `docs/adr/ADR-0003-cursor-acp-lifecycle.md`, `docs/protocols/opencode/event-stream-sample.jsonl`, `docs/protocols/opencode/server-api-inventory.json`, `docs/protocols/cursor/acp-capabilities.json`, `docs/protocols/capabilities/capability-matrix.md`, `docs/architecture/DATA_FLOW.md`
- **Контрактные тесты:** `tests/LLMWorkGUI.Backends.ContractTests/StateAndApprovalContractTests.cs`

## 1. Назначение и нормативные правила

1. Таблицы нормативны: Session Manager, Execution Supervisor/Watchdog и Health Service в Phase 1+ реализуют ровно перечисленные состояния и переходы. Любое иное состояние или переход запрещены.
2. Формулировка ТЗ «любое нетерминальное состояние» раскрывается терминальными статусами соответствующей машины; инварианты ТЗ при этом не меняются.
3. Переход выполняется только при выполнении Trigger/guard. Guard детерминирован и не зависит от эвристик UI, текста prompt или предположений о поведении backend.
4. Каждый переход имеет side effects и попадает в audit без секретов (correlation IDs: `sessionId`, `executionId`, `clientRequestId`).
5. `Starting` не означает доставку prompt. `Active` допустим только после native session evidence.
6. Автоматический повтор prompt при `Ambiguous` запрещён. User cancel, approval deny и `Ambiguous` не увеличивают circuit breaker и не расходуют workflow retry budget.
7. Terminal evidence — только нормализованное terminal event реального протокола (OpenCode SSE, `star-cliproxy` SSE/provider result, Cursor ACP) после redaction. HTTP-подтверждение отправки запроса terminal evidence не является.
8. Если состояние или поле не подтверждено evidence из §5, UI показывает честное `Unknown`/`Not reported` и не изображает успех.

## 2. Session state machine

### 2.1. Состояния

| State | Terminal | Описание |
|---|---|---|
| `Draft` | нет | Локальная session создана, native ID отсутствует, prompt не отправлен; binding выбран. |
| `Starting` | нет | Начато создание native session; first Execution и `clientRequestId` созданы. |
| `Active` | нет | Native session ID и observed binding подтверждены; session допускает turn. |
| `Idle` | нет | Нетерминального Execution нет; разрешён `Continue` на неизменном binding. |
| `Ambiguous` | нет | Prompt мог быть доставлен, terminal evidence или session liveness неизвестны; auto-retry запрещён. |
| `Orphaned` | нет | Backend/process исчез и доставку prompt можно исключить либо native session нельзя reattach; требуется recovery decision. |
| `Closed` | да | Session закрыта явным Reset/Close или подтверждённым отсутствием session; история сохранена. |

### 2.2. Переходы

| From | To | Trigger/guard | Side effects | Native evidence |
|---|---|---|---|---|
| — | `Draft` | создана local session, native ID отсутствует | binding выбран; prompt не отправлен; создаётся local session record | native запрос не отправлялся |
| `Draft` | `Starting` | начато создание native session | создаётся первый Execution и `clientRequestId`; requested route зафиксирован | `POST /session` |
| `Starting` | `Active` | получен и провалидирован native session ID в формате выбранного протокола и immutable binding; `ses_...` относится только к OpenCode | session считается подтверждённой; native ID записан | OpenCode `session.updated`/`GET /session/{sessionID}`; ACP `session/new`; Mirasim подтверждённый `sessionKey` и binding |
| `Active` | `Idle` | нет нетерминального Execution | разрешён `Continue` на том же native ID и binding | `session.idle` |
| `Idle` | `Active` | начат новый Execution на том же неизменном binding | native session сохраняется; новый `clientRequestId` | старт нового Execution на том же binding (не `prompt_async`) |
| `Starting`, `Active`, `Idle` | `Ambiguous` | prompt мог быть доставлен, terminal evidence или session liveness неизвестны | auto-retry запрещён; требуется recovery decision; audit | обрыв SSE/transport после отправки; restart сервера; выход ACP-процесса без terminal event |
| `Starting`, `Active`, `Idle` | `Orphaned` | backend/process исчез и доставку prompt можно исключить либо native session нельзя reattach | требуется recovery decision; lock не освобождается автоматически | `BackendMissing`; `GET /session/{sessionID}` не подтверждает session; process exit |
| любое нетерминальное | `Closed` | явный Reset/Close либо backend подтвердил отсутствие session | история сохраняется; следующий turn создаёт новую `Draft`; close/reset reason фиксируется | user Reset/Close; native session отсутствует в `GET /session` |
| `Ambiguous`, `Orphaned` | `Active` | reconciliation вернул `Reattached` и совпал полный immutable binding | записывается recovery evidence; binding не меняется | `GET /session/{sessionID}`; ACP `session/load` при `loadSession=true` |
| `Ambiguous`, `Orphaned` | `Idle` | reconciliation вернул `Reattached`, terminal evidence подтверждает завершение turn | записывается recovery evidence; `Continue` разрешён | terminal event в SSE; ACP terminal update; `session.idle` |

### 2.3. Reconciliation outcomes

| Outcome | Guard | Session result | Execution result |
|---|---|---|---|
| `Reattached` | native session ID и полный immutable binding совпали; backend жив | `Ambiguous`/`Orphaned` → `Active`/`Idle` | terminal evidence → соответствующий terminal state; иначе `Ambiguous` |
| `Orphaned` | доставку prompt можно исключить либо native session нельзя reattach | `Starting`/`Active`/`Idle` → `Orphaned` | возможная доставка без terminal evidence → `Ambiguous`; доказанный отказ до dispatch → `Failed`/`Rejected`; подтверждённый terminal failure evidence → `Failed` |
| `Ambiguous` | prompt мог быть доставлен, terminal evidence отсутствует | `Starting`/`Active`/`Idle` → `Ambiguous` | `Ambiguous`; auto-retry запрещён |
| `BackendMissing` | executable или managed instance отсутствует; session evidence недоступно | `Starting`/`Active`/`Idle` → `Orphaned`; `Ambiguous`/`Orphaned` сохраняются | отсутствие backend само по себе не исключает прежнюю доставку: без terminal evidence → `Ambiguous`; отдельное доказательство отсутствия dispatch допускает `Failed`/`Rejected` |

### 2.4. Инварианты Session

1. Reset закрывает старую session; следующая отправка создаёт новую `Draft` session, а не переиспользует `Closed`.
2. Второй нетерминальный Execution в одной session запрещён, пока capability probe и отдельная policy явно не подтвердят backend queue.
3. `Fork` показывается только при подтверждённой native fork capability; иначе доступно `New session` с ancestry, но не ложный fork.
4. `Continue` использует ту же native session и неизменный binding (§6.7).
5. `Reattached` запрещён без совпадения native session ID и полного binding.

## 3. Execution state machine

### 3.1. Состояния

| State | Terminal | Описание |
|---|---|---|
| `Queued` | нет | Execution создан пользователем или workflow; ожидает checkout/route/concurrency locks. |
| `Starting` | нет | Locks получены; процесс/session стартуют. |
| `SessionConfirmed` | нет | Native session ID и binding подтверждены. |
| `Running` | нет | Prompt принят transport/backend либо получено первое execution event. |
| `WaitingApproval` | нет | Backend запросил approval; turn ждёт ответа. |
| `Cancelling` | нет | Пользователь или timeout запросили cancel; ожидается terminal evidence. |
| `Succeeded` | да | Получено нормализованное terminal success event и observed route evidence. |
| `Failed` | да | Получено однозначное terminal failure event до/после доставки. |
| `TimedOut` | да | Turn hard timeout; delivery status известен. |
| `Cancelled` | да | Backend подтвердил cancel/termination. |
| `Ambiguous` | да | Prompt мог быть доставлен, terminal evidence отсутствует; auto-retry запрещён. |
| `RouteMismatch` | да | Observed route не совпал с requested route; success не засчитан. |

### 3.2. Переходы

| From | To | Trigger/guard | Side effects | Native evidence |
|---|---|---|---|---|
| — | `Queued` | пользователь или workflow создали execution | создаётся local execution, `clientRequestId`, requested route | `POST /session/{sessionID}/prompt_async` не отправлялся |
| `Queued` | `Starting` | получены checkout/route/concurrency locks | locks зафиксированы; запускается управляемый процесс или ACP session | Process Supervisor spawn; ACP `session/new` |
| `Starting` | `SessionConfirmed` | native session ID и binding подтверждены | native session ID записан; process state отделён от session state | `session.updated`; ACP `session/new` result |
| `SessionConfirmed` | `Running` | prompt принят transport/backend либо получено первое execution event | `promptHash` и requested route зафиксированы; turn timer стартует | `message.updated` (role user); первое `message.part.updated` |
| `Running` | `WaitingApproval` | backend запросил approval | approval показан пользователю; turn timer продолжает учитывать activity | permission request в SSE; ACP permission request |
| `WaitingApproval` | `Running` | approval разрешён либо пользователь отказал, а backend продолжает тот же turn без запрещённой операции | allow/deny отправлен и записан в audit; отказ пользователя не увеличивает breaker; завершение turn классифицируется отдельно по terminal evidence | `POST /permission/{requestID}/reply` (allow либо `reject`); ACP поддерживаемый permission reply |
| `Starting`, `SessionConfirmed`, `Running`, `WaitingApproval` | `Cancelling` | пользователь или timeout запросили cancel | graceful cancel запущен; новые approval requests закрываются | `POST /session/{sessionID}/abort`; ACP in-band cancel |
| любое нетерминальное | `Succeeded` | получено нормализованное terminal success event | observed route зафиксирован; success без route evidence не засчитывается | `message.updated` с `finish=stop`; `session.idle`; ACP terminal update |
| любое нетерминальное | `Failed` | получено однозначное terminal failure event до/после доставки | health class зафиксирован; retry policy применяется согласно evidence | error event; protocol failure; non-zero termination evidence |
| любое нетерминальное | `TimedOut` | turn hard timeout; доставка доказанно исключена либо native terminal/подтверждённое завершение owned process tree исключает дальнейшую работу | timeout class; session reconciliation запускается; одного timer недостаточно для освобождения writer | Execution Watchdog timer вместе с доказательством отсутствия dispatch либо native termination; возможная доставка без terminal evidence → `Ambiguous` |
| любое нетерминальное | `Ambiguous` | prompt мог быть доставлен, terminal evidence отсутствует | auto-retry запрещён; breaker не растёт; retry budget не расходуется | обрыв transport/SSE; restart сервера; выход ACP-процесса без terminal event |
| любое нетерминальное | `RouteMismatch` | observed route не совпал с requested route | success не засчитан; выполняется policy расхождения | plugin route evidence; сравнение requested/observed |
| `Cancelling` | `Cancelled` | backend подтвердил cancel/termination | lock освобождается после reconciliation; session → `Idle` только при подтверждённой liveness | terminal cancel event; ACP cancel confirmation; подтверждённое завершение process tree |

### 3.3. Terminal classification

Backend result DTOs are mapped to this domain enum by their journal; their labels are not additional
Execution states. The current Mirasim compatibility mapping is specified in
[turn protocol](../protocols/mirasim/turn-protocol.md): its ManualOnly `Succeeded` records a terminal
host turn with `nativeIdentityConfirmed=false`, not verified productive route success. This bounded
compatibility case cannot satisfy generic workflow route verification, health model recovery or
account/model-origin acceptance. A terminal state alone never authorizes writer release.

1. Terminal state не имеет исходящих переходов. Повторный запуск после `Succeeded`/`Failed`/`TimedOut`/`Cancelled`/`Ambiguous`/`RouteMismatch` создаёт новый Execution.
2. Ручной повтор создаёт новый Execution со ссылкой `retryOfExecutionId`, показывает риск двойного расхода/действия и требует подтверждения.
3. `Succeeded` невозможен без нормализованного terminal success event; `Cancelled` невозможен без terminal evidence, а не только HTTP-ack abort.
4. `RouteMismatch` никогда не считается success и не расходует workflow retry budget.

### 3.4. Инварианты Execution

1. В одной session одновременно допускается не более одного нетерминального Execution.
2. Process started, session confirmed, prompt delivery и terminal outcome — четыре разных факта; состояние отражает только подтверждённые факты.
3. Тишина модели — наблюдение, а не автоматический failure: `TimedOut` наступает только по turn hard timeout, а не по отсутствию событий.
4. Hard timeout turn не завершает живой server/ACP process; heartbeat не пишется в stdin протокола.
5. При невозможности записи событий и заполнении буфера фиксируется `BufferOverflow`; terminal events не отбрасываются молча. `Failed` допустим до возможной доставки либо при подтверждённом terminal backend failure. После возможной доставки без terminal evidence используется `Ambiguous` с сохранением ownership и запретом автоматического retry.

## 4. Health state machine (circuit breaker)

### 4.1. Состояния

| State | Terminal | Описание |
|---|---|---|
| `Healthy` | нет | Route полностью участвует в routing. |
| `Degraded` | нет | Зафиксирована одиночная transient error или ухудшение latency/error rate; route ещё участвует. |
| `CoolingDown` | нет | Достигнут threshold N/W; route исключён из automatic routing до истечения D. |
| `ProbeRequired` | нет | Cooldown истёк; возврат только через отдельный pinned probe. |
| `Recovering` | нет | Пользователь подтвердил probe; выполняется проверка auth/model/minimal turn. |
| `QuarantinedAuto` | нет | Probe failed или повторена однородная ошибка; route исключён автоматически. |
| `DisabledManual` | нет | Пользователь выключил route. |
| `ForcedEnabled` | нет | Пользователь принудительно разрешил route без успешного probe. |

### 4.2. Переходы

| From | To | Trigger/guard | Side effects | Native evidence |
|---|---|---|---|---|
| `Healthy` | `Degraded` | одиночная учитываемая transient error или ухудшение latency/error rate | audit health event; route остаётся в routing pool | normalized execution error |
| `Healthy`, `Degraded` | `CoolingDown` | достигнут threshold N одинаковых учитываемых ошибок в rolling window W (default N=3, W=15 минут) | route исключён из automatic routing; sticky session stop с объяснением | rolling health metrics |
| `CoolingDown` | `ProbeRequired` | истёк cooldown D (default 5 минут) либо пользователь запросил сброс cooldown (clear cooldown action) | cooldown прерван, требуется обязательный pinned probe; прямой переход в `Healthy` по-прежнему категорически запрещён | cooldown timer; user clear cooldown action |
| `ProbeRequired` | `Recovering` | пользователь подтвердил отдельный pinned probe | создаётся отдельный pinned Execution; показывается возможный расход | user confirmation; pinned probe |
| `Recovering` | `Healthy` | probe успешно подтвердил auth, model и минимальный turn/operation | route возвращается в routing pool; verified recovery записан в audit | probe success evidence |
| `ProbeRequired`, `Recovering` | `QuarantinedAuto` | probe failed либо повторена однородная ошибка | route исключён; replacement session не создаётся автоматически | probe failure; repeated homogeneous error |
| `Healthy`, `Degraded`, `CoolingDown`, `ProbeRequired`, `Recovering`, `QuarantinedAuto`, `ForcedEnabled` | `DisabledManual` | пользователь выключил route | route не участвует в routing; audit; повторное включение требует проверки | user disable action |
| `DisabledManual` | `ProbeRequired` | пользователь снова включил route | обязательная проверка перед возвратом; кнопка не даёт `Healthy` | user enable action |
| `CoolingDown`, `ProbeRequired`, `QuarantinedAuto` | `ForcedEnabled` | пользователь принудительно разрешил route без успешного probe | визуально отличается от `Healthy`; automatic routing только после отдельного opt-in | user action + explicit opt-in |
| `ForcedEnabled` | `ProbeRequired` | probe required | route снова требует проверки; audit | probe requirement |
| `ForcedEnabled` | `Healthy` | подтверждённый pinned probe установил auth, model и успешный минимальный turn/operation | verified recovery записан в audit | correlated probe admission и полное success evidence |
| `ForcedEnabled` | `CoolingDown` | threshold учитываемых ошибок или немедленный auth block | принудительное разрешение прекращено; route исключён | normalized failure |
| `ForcedEnabled` | `Recovering` | пользователь подтвердил отдельный pinned probe | выполняется проверка; без успешного terminal evidence route не становится healthy | probe start |

### 4.3. Нормализованные классы ошибок

| Error class | Описание | Учитывается breaker |
|---|---|---|
| `executableMissingOrVersion` | executable отсутствует или версия несовместима | да |
| `startupOrSessionCreation` | startup или создание native session не удалось | да |
| `authenticationOrRefresh` | auth failure или refresh failure; блокирует все routes account | да |
| `quotaOrRateLimit` | квота исчерпана или rate limit | да |
| `networkOrTimeout` | сетевой сбой или timeout транспорта | да |
| `provider4xx5xx` | provider ответил 4xx/5xx | да |
| `modelUnavailableOrMismatch` | model недоступна или не совпала; блокирует только конкретный route | да |
| `malformedProtocolEvent` | malformed protocol/event; контент не исполняется | да |
| `toolOrPermission` | backend/tool failure учитывается; явный пользовательский approval deny не является такой ошибкой | да для backend/tool failure; нет для user deny |
| `shellCompositionOrQuoting` | ошибка составления аргументов или shell quoting | да |
| `unexpectedInteractiveInputWait` | процесс ждёт интерактивный ввод при закрытом stdin; не является model failure | нет |
| `workspaceConflict` | конфликт workspace или checkout lock | да |
| `userCancellation` | user cancel | нет |
| `unknownOrAmbiguousCompletion` | unknown/ambiguous completion; auto-retry запрещён | нет |

### 4.4. Политика по умолчанию и инварианты

1. Нормативные defaults для тестов: N=3 одинаковые учитываемые ошибки в rolling window W=15 минут → `CoolingDown` на D=5 минут → `ProbeRequired`; параметры настраиваются per provider.
2. `CoolingDown` никогда не переходит напрямую в `Healthy`; обязателен `ProbeRequired` и успешный pinned probe.
3. Кнопка UI не возвращает route в `Healthy` без проверки. `ForcedEnabled` визуально отличается от `Healthy`.
4. Auth failure немедленно блокирует все routes account. Model mismatch блокирует только конкретный route.
5. User cancel, approval deny и `Ambiguous` не увеличивают breaker. Из account cooldown и route health применяется более строгое состояние.
6. Пока route в `CoolingDown`, `QuarantinedAuto` или `DisabledManual`, следующий turn существующей sticky session останавливается с объяснением; replacement не создаётся автоматически.
7. Probe — отдельный pinned Execution, не продолжает пользовательскую session, освобождён от quarantine eligibility gate, показывает возможный расход и требует подтверждения.
8. `Degraded` сохраняется после обычного успешного turn: этот успех не является подтверждением recovery. При достижении N/W состояние переходит в `CoolingDown`; явное выключение/включение переводит в `ProbeRequired` с последующим pinned probe. Автоматического `Degraded` → `Healthy` по одному успеху или истечению окна нет.
9. `QuarantinedAuto` восстанавливается через явное disable/enable → `ProbeRequired` → `Recovering` либо force-enable → подтверждённый model probe → `Recovering`; прямой probe из quarantine не допускается. Quarantine eligibility exception действует после admission probe, а не добавляет переход. Без correlated admission authority результат не меняет health.
10. Совместные health gates не используют числовой порядок enum: автоматический маршрут допускается только при разрешении provider/account/model/route. Account auth block/cooldown сохраняется при force-enable route; opt-in automatic routing не заменяет probe evidence и не обходит account gate.

## 5. Привязка к реальным событиям backend

| Native evidence | Источник | Нормализованный смысл | Переходы |
|---|---|---|---|
| `server.connected` | OpenCode SSE `/event`, первый event | Transport инстанса готов; session evidence не является | — |
| `session.updated` | OpenCode SSE `session.updated` | Подтверждение native session ID (`ses_...`) и binding | `Starting` → `Active` / `SessionConfirmed` (`Draft` → `Starting` — локальный запуск до ответа бэкенда) |
| `message.updated` | OpenCode SSE `message.updated` | Роль сообщения, старт/завершение assistant message (`finish=stop`) | `SessionConfirmed` → `Running`; terminal success evidence |
| `message.part.updated` | OpenCode SSE, parts `step-start`, `text`, `tool` (`running`/`completed`), `step-finish` | Turn activity, tool calls, контекст approval | `Running`, `WaitingApproval`, activity tracking |
| `session.idle` | OpenCode SSE `session.idle` | В session нет выполняющегося turn | `Active` ↔ `Idle`; terminal success вместе с `finish=stop` |
| `POST /session/{sessionID}/abort` | OpenCode HTTP | Cancel запрошен; HTTP-ack terminal evidence не является | → `Cancelling`; `Cancelled` только после terminal event |
| `POST /permission/{requestID}/reply` | OpenCode HTTP | Ответ на approval request; native literals версионно-зависимы | `Running` ↔ `WaitingApproval` |
| malformed event | OpenCode SSE / ACP stdio | Health class `malformedProtocolEvent`; не исполняется | перехода нет; учитываемая ошибка health |
| proxy stream delta/result | `star-cliproxy` SSE | Activity/terminal evidence только после проверки provider/model/session/account context | `SessionConfirmed` → `Running` → terminal либо `RouteMismatch` |
| `acp.initialize` | Cursor ACP `initialize` (`protocolVersion` = число 1) | ACP transport готов; числовое сравнение версии | health backend; готовность к `session/new` |
| `acp.session.new` | Cursor ACP `session/new` result | Native session ID и workspace binding подтверждены | `Starting` → `Active` / `SessionConfirmed` (`Draft` → `Starting` — локальный запуск) |
| `acp.streaming.updates` | Cursor ACP protocol events over stdio | Turn activity, partial output, terminal updates | `Running`, `WaitingApproval`, terminal states |
| `acp.approvals` | Cursor ACP permission request | Approval запрошен агентом | `Running` → `WaitingApproval` |
| `acp.cancel` | Cursor ACP in-band cancellation | Cancel запрошен; terminal evidence обязателен | → `Cancelling`; `Cancelled` либо `Ambiguous` |
| `acp.session.load` | Cursor ACP, `loadSession=true` | Кандидат на reattach persisted native session | reconciliation `Reattached` |
| Mirasim turn/watch/reconcile terminal DTO | Mirasim HTTP host | Compatibility mapping: Completed→Succeeded, Cancelled→Cancelled, Ambiguous→Ambiguous, остальные admitted результаты→Failed; подробные ограничения `nativeIdentityConfirmed=false` и причины приведены в [turn protocol](../protocols/mirasim/turn-protocol.md) | подтверждённый terminal journal + cleanup допускает освобождение owned writer; неизвестный исход сохраняет ownership; pre-admission lock refusal не создаёт execution; DTO label не даёт права на productive success или auto-retry |
| process exit без terminal event | Process Supervisor (OpenCode/star-cliproxy/ACP) | Liveness потеряна; terminal evidence отсутствует | → `Ambiguous`; recovery decision |

## 6. Связь с контрактными тестами

Инварианты этого документа проверяются классом `tests/LLMWorkGUI.Backends.ContractTests/StateAndApprovalContractTests.cs`:

- полнота и валидность состояний и переходов Session, Execution и Health;
- недопустимые переходы (`Draft` → `Active`, `Queued` → `Cancelling`, terminal → любой, `CoolingDown` → `Healthy`);
- привязка переходов к native evidence и reconciliation outcomes;
- политика threshold N/W/D и запрет возврата в `Healthy` без probe.

## 7. Evidence index

- `TECHNICAL_SPECIFICATION.md`
- `ROADMAP.md`
- `docs/adr/ADR-0002-opencode-serve-topology-and-api.md`
- `docs/adr/ADR-0003-cursor-acp-lifecycle.md`
- `docs/protocols/opencode/server-api-inventory.json`
- `docs/protocols/opencode/event-stream-sample.jsonl`
- `docs/protocols/opencode/session-schema.json`
- `docs/protocols/cursor/acp-capabilities.json`
- `docs/protocols/cursor/acp-handshake-response.json`
- `docs/protocols/capabilities/capability-matrix.md`
- `docs/protocols/capabilities/approval-mapping.md`
- `docs/protocols/capabilities/legacy-entrypoint-boundary.md`
- `docs/architecture/DATA_FLOW.md`
