# Mirasim turn wire protocol (Slice 2)

Sanitized contract used by `IMirasimSessionLifecycleService`. Mirasim host is a user-owned
loopback instance (`http://127.0.0.1:4970` by default); LLMWorkGUI only talks to it over
`HttpClient` and never starts, restarts or terminates it.

## Endpoints

| Operation | Method | Path | Request body | Response |
|---|---|---|---|---|
| Create session | POST | `api/sessions` | `instance`, `harness`, `model`, `workspace` | `sessionKey`, `harness`, `model`, `routeMode` |
| Continue session | POST | `api/sessions/{sessionKey}/continue` | `instance`, `harness`, `model`, `workspace` | `sessionKey`, `harness`, `model`, `routeMode` |
| Execute turn | POST | `api/sessions/{sessionKey}/turns` | `prompt`, `executionMode`, `requestedHarness`, `requestedModel`, `requestedAccount`, `requestedRouteLeg`, `executionId`, `processGeneration` | `turnId`, `phase`, `error`, `response`, `incomplete`, `model`, `harness`, `account`, `leg`, `routeMode` |
| Read turn state (reconcile) | GET | `api/sessions/{sessionKey}/turns/{turnId}` | none | same shape as execute |
| Cancel turn | POST | `api/sessions/{sessionKey}/turns/{turnId}/cancel` | `{}` | `turnId`, `phase`, `cancelled`, `terminal` |
| Watch turn | GET | `api/sessions/{sessionKey}/turns/{turnId}/events` | none | JSON Lines stream |

`phase` values observed so far: `running`, `done`, `error`, `cancelled`, `incomplete`.
`routeMode` values: `ManualOnly`, `Opaque`.

## Security invariants (ТЗ §9.3, ADR-0008 §6)

- The prompt travels only in the JSON body of `POST api/sessions/{sessionKey}/turns`.
  It is never placed in the request URI, query string, headers, process arguments or logs.
- The local access token travels only in the `Authorization: Bearer <token>` header of the
  call it belongs to. LLMWorkGUI never reads Mirasim token files and never stores the token
  in options; `MirasimTurnRequest.ToString()` prints `[REDACTED]` for prompt and token.
- Raw response bodies and prompts are never written to LLMWorkGUI logs or exception messages.

## Outcome normalization (ADR-0008 §10, acceptance plan)

| Host evidence | Status | Error class |
|---|---|---|
| HTTP 503 (`no upstream available`) | `Failed` | `UpstreamUnavailable503` |
| HTTP 422 (`platform busy`) | `Failed` | `PlatformBusy422` |
| Other non-success HTTP status without an established refusal classification | `Ambiguous` | `Unknown` |
| Initial execute response has no usable `turnId` or does not echo the requested `sessionKey` | `Ambiguous` | `Unknown` |
| `phase=done` with non-empty `error` | `Failed` | `DoneWithError` |
| `incomplete=true` or `phase=incomplete` | `Incomplete` | `IncompletePayload` |
| Explicit observed `model`/`harness`/`account`/`leg` contradicts requested | `Failed` | `RouteMismatch` |
| Connection drop or timeout after delivery | `Ambiguous` | `ConnectionDrop` / `Timeout` |
| Connection/DNS failure before delivery | `Failed` | `ConnectionDrop` |
| `phase=cancelled` (cancel confirmation) | `Cancelled` | `None` |

A timeout observed while sending the turn request (response headers not yet received; the POST may
already have been delivered for a long-running turn) is normalized to `Ambiguous` + `Timeout`, not
to `Failed`. Like a post-delivery connection drop it retains the writer lock until reconciliation,
because the turn may still be running on the host.

Only successful HTTP responses are parsed as turn evidence. Existing typed refusal statuses retain
their established classifications; other non-success responses cannot prove terminal completion even
when their JSON body says `phase=done`. Initial execute evidence must provide a nonblank turn identity
before any phase can be accepted. Ambiguous evidence retains the durable execution/session and writer
ownership; it never triggers an automatic prompt retry. Known-target control response identity checks
remain unchanged.

Wire/result status is not a domain enum or an independent unlock authority. The current journal
maps `Completed` to `ExecutionState.Succeeded`, `Cancelled` to `Cancelled`, `Ambiguous` to
`Ambiguous`, and `Failed`/`Incomplete`/local refusal to `Failed` with the separate failure reason.
`RouteMismatch` is represented by wire `Failed` plus its DTO error classification; the current
Mirasim journal stores `Failed`/`InternalError`, rather than the general domain `RouteMismatch`
state. The DTO classification must not be inferred from that generic journal reason alone.
These mappings do not add `Completed`, `Incomplete` or `RefusedByLock` to the domain enum.

A local refusal before admission owns no execution lock to release. An admitted owner is retired
only after the exact terminal journal transaction and its ownership release are confirmed.
Ambiguous delivery retains ownership until `ReconcileTurnAsync` obtains matching terminal evidence;
reconciliation never resends the prompt (GET only). Restart recovery additionally requires the
chronology/project/session/no-other-unconfirmed-execution guards in the threat model. Terminal-looking
HTTP fields or an abort acknowledgement alone cannot release a writer.

`ReconcileTurnAsync` re-evaluates the route that was requested when the turn was dispatched
(harness, model, account and leg are captured at `ExecuteTurnAsync` time). A reconciled `phase=done` payload
whose explicit observed binding contradicts that expectation is never surfaced as `Completed`;
it is normalized to `Failed` + `RouteMismatch`, exactly like a direct execute response.
Missing account/leg remain unreported, force `ManualOnly` and never prove productive account identity.
For this compatibility channel, `Completed`/journal `Succeeded` records an observed terminal host
turn, including ManualOnly turns. It is not verified account/model-origin success:
the journal records `nativeIdentityConfirmed=false`. Missing fields remain `Not reported`, are not
copied from the request as observed facts, and cannot satisfy the productive route acceptance gate.
`routeMode` describes channel opacity; it is not an account identity claim. The generic productive
workflow route remains unsupported until independent complete native binding evidence exists.

Peer response bodies are limited to 4 MiB and JSON Lines to 1 Mi UTF-16 characters per line.
Overflow after delivery preserves `Ambiguous` writer ownership; a stream overflow fails the local
subscription without claiming terminal native completion. Local shutdown waits for actual retained
operations even when a cancellation callback throws, then reports that shutdown failure.

## Fixtures

- `turn-stream-event-sample.jsonl` — sanitized watch stream.
- `turn-503-upstream-sample.json` — HTTP 503 body.
- `turn-422-busy-sample.json` — HTTP 422 body.
- `turn-done-with-error-sample.json` — `phase=done` with error.
- `turn-incomplete-sample.json` — `incomplete=true`.
- `turn-cancel-response-sample.json` — terminal cancellation confirmation.
