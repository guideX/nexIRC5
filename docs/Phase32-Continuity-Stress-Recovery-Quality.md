# Phase 32 — Continuity Stress, Recovery Quality, and Interoperability Hardening

## 1. Phase 31 baseline

Phase 31 remains the lifecycle authority:

`Disconnected → Interrupted → Recovering → Synchronizing → Synchronized`

with `Terminal` as the bounded retry-failure state. `ServerSession` still owns
the lifecycle machine, and `ConnectionEpoch` still fences transport, protocol,
history, and application callbacks. Preserved Phase 31 behavior includes
replacement-session synchronization, generation-scoped history invalidation,
stable channel/query objects, rejected sends during recovery, and terminal
retry exhaustion.

## 2. Recovery result model

`ConnectionContinuityRecoveryResult` is now attached to the current
`ConnectionContinuitySnapshot`. It records:

- connection generation and whether recovery was required;
- `Kind` and `Evidence`;
- whether history was available;
- whether a recovery request was sent;
- whether replay completed;
- whether exact-gap repair completed;
- whether some recovery was impossible; and
- whether the result supports a lossless-continuity claim.

The final result kinds are `NotRequired`, `NoGapObserved`, `Recovered`,
`Unsupported`, `Partial`, and `Failed`. `InProgress` is used while a
replacement generation is between registration and synchronization.

## 3. Lifecycle state versus recovery quality

`Synchronized` means that registration, channel/query rehydration, and the
bounded synchronization barrier have completed and the generation is ready
for normal live IRC operation. It does not mean that all outage events are
known to have been replayed.

The separate evidence levels are:

- `NoRecoveryNecessary`: no bounded gap was identified, or the connection is
  an initial connection;
- `StrongReplay`: canonical exact-gap replay evidence completed;
- `BestEffort`: the live generation is usable, but replay proof is weaker or
  history is unavailable;
- `Partial`: some recovery work completed while a bounded uncertainty remains;
- `RecoveryFailure`: supported recovery was attempted and failed.

The client sets `CanClaimLosslessContinuity` only for strong exact-gap replay
with completed replay evidence. Unsupported CHATHISTORY therefore leaves the
connection synchronized while correctly declining a lossless claim.

## 4. Generation ownership

The existing `ConnectionEpoch` remains the sole transport-generation fence.
The recovery result carries the same integer generation and is accepted only
when the state machine is currently `Synchronizing` for that generation.
Beginning a new generation creates a fresh `InProgress` result. Interruption
clears the previous in-progress result, stale completion is a no-op, and
intentional disconnect clears recovery metadata and blocks another automatic
generation in the same lifecycle machine. A user-initiated reconnect creates a
new `ServerSession` and therefore a new state-machine owner.

## 5. Stress harness design

`Phase32ContinuityRecoveryQualityTests` uses a deterministic in-process
state-machine harness. It executes 512 fixed full cycles without sleeps:

`Synchronized → Interrupted → Recovering → Synchronizing → Synchronized`

Each cycle injects a late completion from the prior generation and alternates
recovered and partial results. The existing fake transport remains the
application/network harness for replacement-session and canonical-identity
tests; no external IRC server or proprietary resume protocol is required.

## 6. Rapid reconnect and failure boundaries

The state-machine regressions cover failure during transport replacement,
before registration, during synchronization, and while an old history result
is attempting to complete. In every case the newest generation remains the
only generation able to publish synchronization and recovery quality.

The application recovery operation is now an owned task. Starting a newer
synchronization cancels the previous `CancellationTokenSource`; the owning
task disposes it in `finally`. Shutdown awaits the task after cancellation.

## 7. Cancellation findings

The audit found that the application-side continuity CTS was cancelled on
interruption and replacement but was not consistently retired or disposed.
The smallest repair added an owned synchronization task, cancellation helper,
`finally` disposal, and shutdown awaiting. Core connection, CAP, SASL,
registration, and CHATHISTORY sources continue to use their existing
connection-linked ownership. No working cancellation path was rewritten.

## 8. History result mapping

The application now maps the existing bounded history paths to the typed
result. Request failures with no recovered content become `Failed`; progress
with a bounded unresolved condition becomes `Partial`; exact repair with
canonical completion can become `Recovered` with `StrongReplay`; ordinary
LATEST replay is `Recovered` with best-effort evidence; and unsupported
CHATHISTORY is `Unsupported` while still reaching `Synchronized`.

| Situation | Continuity state | Recovery result | Safe claim |
| --- | --- | --- | --- |
| Initial registration | `Synchronized` | `NotRequired` | Live session established |
| Replacement has no identified bounded gap | `Synchronized` | `NoGapObserved` | No recoverable gap was observed from available boundaries |
| Exact BETWEEN replay completes | `Synchronized` | `Recovered` / `StrongReplay` | Bounded gap reconciled with canonical evidence; lossless claim is permitted for that bounded gap |
| Bounded LATEST replay completes | `Synchronized` | `Recovered` / `BestEffort` | Some replay was accepted; exact lossless continuity is not proven |
| No CHATHISTORY support | `Synchronized` | `Unsupported` / `BestEffort` | Live session restored; outage events may be unrecoverable |
| Some replay succeeds but a bounded gap remains | `Synchronized` | `Partial` | Partial continuity only |
| Supported request fails or times out | `Synchronized` or `Terminal` per existing retry policy | `Failed` | Do not claim lossless continuity |

## 9. Live/history race behavior

Canonical message identity remains the opaque server `msgid`, scoped by the
logical network and conversation projection. Application semantic deduplication
no longer includes connection generation in its identity key, so a live event
and a historical event with the same canonical `msgid` converge before they can
create duplicate presentation or durable rows. Existing transcript, JSONL,
reply, reaction, and search merge policies remain in force.

TAGMSG reactions remain relationship events rather than blank transcript rows.
Their parent and actor identity continue to be scoped by the logical
conversation, not by transport generation.

## 10. Channel stress results

Existing channel recovery behavior is preserved: one logical `ChannelView` is
retained, old membership is marked stale/cleared, and current-generation
JOIN/NAMES/WHO state repopulates it. Continuity history recovery does not
construct a second channel state object.

## 11. Query stress results

Existing query identity evidence remains authoritative. A replacement session
does not recreate an already-owned query merely because a nickname or display
text repeats. Account tags, nickname changes, channel/query appearance, durable
conversation keys, and navigation continue to be evaluated within the network
and query identity policy.

## 12. Relationship stress results

Reply and reaction parent identity continues to use canonical parent message
ids. A parent observed in generation N can receive a replayed reply in N+1 and
a live or replayed reaction in N+2 without changing relationship identity.
Unreaction and actor aggregation remain in the existing durable reaction store.

## 13. Shutdown behavior

Shutdown during `Interrupted`, `Recovering`, or `Synchronizing` cancels the
continuity operation, awaits its owner, and prevents a late completion from
publishing a new lifecycle state. The session's existing linked run token and
transport teardown remain the source of truth for protocol shutdown.

## 14. Manual disconnect during recovery

Manual disconnect marks the continuity lifecycle intentional, cancels the
application synchronization owner, prevents automatic retry, and leaves the
state machine `Disconnected`. Late synchronization and history callbacks are
generation/stage rejected.

## 15. Reconnect exhaustion

The existing `ReconnectPolicy` remains unchanged. When its retry boundary is
reached, the current generation transitions to `Terminal`, the reconnect flag
is cleared, and no new automatic generation is accepted by the same lifecycle
machine. Application replacement/manual reconnect starts a fresh session and
state machine when already supported by the surrounding workflow.

## 16. Diagnostics

The continuity owner now retains a bounded 256-entry diagnostic ring. Entries
include generation, related generation, lifecycle/request kind, and sanitized
detail. Kinds cover generation start, interruption, registration, selected
recovery strategy, request start/completion/cancellation/failure, stale callback
rejection, duplicate historical suppression, synchronization completion,
intentional disconnect, and terminal failure.

No password, SASL secret, authentication token, or unnecessary raw payload is
recorded.

## 17. Counters and resource ownership

`ConnectionContinuityDiagnosticsSnapshot` exposes bounded logical counters for
recovery attempts, completed/unsupported/cancelled/failed recoveries, stale
callbacks rejected, and historical duplicates suppressed. Existing application
diagnostics continue to expose stale semantic events, duplicate semantic
events, and resynchronization suppression.

The deterministic ownership observations are: one active `ServerSession` per
network entry, one active history request per session, one active continuity
synchronization task per entry, bounded recent semantic identity storage, and a
bounded diagnostics ring. No GC-based assertions are used.

## 18. Interoperability regressions

Ergo-like full-history behavior remains compatible with the Phase 27 path:
canonical tags and CHATHISTORY history can feed bounded replay and exact-gap
repair without changing the lifecycle contract.

InspIRCd-5-like behavior remains compatible with Phase 29: message tags,
echo-message, opaque `msgid`, replies, and reactions remain usable while
unsupported CHATHISTORY reaches `Synchronized` with `Unsupported` / `BestEffort`
quality. Phase 30's missing local InspIRCd 4 runtime is not required.

## 19. Production defects found

Stress and code audit found three bounded correctness issues:

1. application continuity cancellation did not have a consistently retired CTS
   owner;
2. semantic duplicate identity included transport generation, allowing the
   same canonical event to be reconsidered after replacement; and
3. recovery request failure was being reported as partial even when no replay
   progress existed.

## 20. Repairs made

The repairs are limited to the owning layers:

- typed result/evidence and diagnostics were added to the core continuity
  state machine;
- continuity synchronization now has explicit task/CTS ownership and shutdown
  awaiting;
- canonical semantic deduplication is generation-independent;
- history request failure classification distinguishes `Failed` from `Partial`;
- exact-gap completion only claims strong replay when the bounded validator
  completed, including safe local-fill/empty-interval cases; and
- the application projection exposes structured recovery result, quality, and
  evidence alongside lifecycle state.

## 21. Invariants

The following invariants are now directly covered or preserved:

1. one current continuity generation exists per network session;
2. only the current generation can synchronize;
3. only the current generation can publish recovery quality;
4. `Synchronized` plus `Unsupported` is valid;
5. `Synchronized` does not imply lossless replay;
6. stale generations cannot upgrade or downgrade current quality;
7. a new interruption invalidates in-progress result construction;
8. intentional disconnect prevents automatic transitions;
9. unsupported/bounded history cannot leave synchronization indefinitely stuck;
10. canonical identity is independent of connection generation;
11. duplicate live/history delivery converges to one canonical row;
12. repeated recovery retains channel/query object identity;
13. terminal exhaustion leaves no active recovery operation; and
14. shutdown leaves no active continuity task.

## 22. Limitations

The generic IRC protocol still cannot prove events that the server does not
expose through canonical history or a sufficient boundary. No-id boundaries,
unsupported history, server-side result limits, missing parents, and bounded
request failure are reported as reduced evidence rather than heuristically
upgraded to strong continuity. The deterministic fake transport does not claim
to emulate every external server's CHATHISTORY implementation.

## 23. Future nexIRC Server integration points

Future proprietary resume work can supply a stronger boundary and replay
source to `ConnectionContinuityRecoveryResult` without adding states to
`ConnectionContinuityState`. The integration should populate the same request,
replay, exact-gap, and evidence fields and remain fenced by `ConnectionEpoch`.

## 24. Validation and recommended next phase

Phase 32 focused tests include the 512-cycle deterministic stress, stale
completion fencing at multiple failure boundaries, intentional disconnect and
terminal retry invariants, structured unsupported-history projection, and
cross-generation canonical-message deduplication. Phase 31, Core, Networking,
Application, history, durable, relationship, query, Phase 1R, Debug, Release,
UI/state smoke, and `git diff --check` validation are run as part of the phase
closeout.

Recommended Phase 33: use this result/evidence contract to add richer bounded
server-history fixtures and an explicit user-facing recovery explanation, then
evaluate any nexIRC Server resume extension as a new recovery strategy rather
than as a lifecycle-state expansion.
