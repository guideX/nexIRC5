# Phase 33 — Recovery Strategies and Deterministic Replay Fixtures

Phase 33 separates continuity recovery selection from the continuity
lifecycle and adds a deterministic in-process replay fixture. The existing
Phase 31 state machine remains authoritative: a strategy performs bounded
work and returns evidence; only the state machine can complete synchronization.

```text
Continuity State Machine
        |
        v
Recovery Strategy Selector (current generation capabilities)
        |
        +--> None / initial connection
        +--> BestEffortNoHistory
        +--> Ircv3ChatHistory
        +--> future replay strategies
        |
        v
ConnectionContinuityRecoveryResult + evidence
        |
        v
Synchronization Barrier
```

## 1. Previous recovery coupling

Before Phase 33, `NetworkSessionManager.RunContinuitySynchronizationCoreAsync`
directly tested `ServerSession.ChathistorySupport.IsUsable`. It either emitted
the unsupported result or called `RecoverReconnectHistoryAsync`. That method
contained the full protocol-specific sequence: bounded channel LATEST requests,
exact BETWEEN repair, known-query repair, TARGETS discovery, recovered-query
projection, BATCH completion, and partial/failure classification. This crossed
the lifecycle boundary because selection, execution, and result construction
were all in one application method.

The protocol implementation itself was not rewritten. The existing request,
canonical identity, server-time, BATCH, cancellation, durable-history, query,
relationship, and generation-fencing behavior remains the executor used by
the IRCv3 strategy.

## 2. Strategy abstraction and ownership

`ConnectionRecoveryStrategyId`, `ConnectionRecoveryBoundary`, and
`ConnectionRecoveryStrategyPolicy` live in `nexIRC.Core.Session`.
`IConnectionRecoveryStrategy`, `ConnectionRecoveryStrategyContext`, and the
concrete strategies live in `nexIRC.Application`:

* `NoConnectionRecoveryStrategy` represents the initial connection.
* `BestEffortNoHistoryRecoveryStrategy` represents safe degradation.
* `Ircv3ChatHistoryRecoveryStrategy` invokes the existing bounded history
  executor.

`ConnectionRecoveryStrategySelector` is stateless and chooses a strategy for
the current generation. It does not own reconnect policy, transport lifetime,
UI state, or lifecycle transitions. `NetworkSessionManager` owns the selector
and supplies the bounded executor. `ConnectionContinuityStateMachine` owns
`Recovering`, `Synchronizing`, `Synchronized`, cancellation fencing, and final
state transition authority.

The strategy context contains the current `ServerSession`, a protocol-neutral
`ConnectionRecoveryBoundary`, and one generic bounded-recovery callback. It does
not expose `SessionEntry`, WPF views, or transport internals.

## 3. Selection rules

Selection uses only the current generation's negotiated `ChathistorySupport`:

1. Initial registration has `recoveryRequired == false` and selects `None`.
2. Replacement registration with usable current-generation CHATHISTORY selects
   `Ircv3ChatHistory`.
3. Replacement registration without usable current-generation history selects
   `BestEffortNoHistory`.

No production selection logic uses a hostname, server brand, software string,
msgid format, or capabilities remembered from another generation. A new
`ServerSession` receives a new capability snapshot, so Full → No History,
No History → Full, and History → Broken History are naturally generation-owned.

## 4. Initial connection and no-history behavior

Initial registration never enters the reconnect synchronization barrier. It
produces `NotRequired`, evidence `NoRecoveryNecessary`, strategy `None`, and
sends no CHATHISTORY command or replay request.

On a replacement generation without usable history, the no-history strategy
returns `Unsupported` with `BestEffort` evidence. It sends no replay command,
does not fabricate recovered events, preserves durable conversations and
canonical messages already known, and explicitly records that outage events
may be unrecoverable. Live operation may still become `Synchronized`; that
state means live readiness, not lossless replay.

## 5. IRCv3 CHATHISTORY strategy

The IRCv3 strategy delegates to the existing bounded implementation. The
implementation still supports the production request forms currently emitted:

* channel LATEST fallback;
* exact-gap BETWEEN repair when both canonical boundaries are usable;
* BEFORE/AFTER/AROUND through existing history services;
* TARGETS discovery for bounded private-history recovery;
* canonical opaque msgids, server-time, BATCH, durable integration, query
  identity, replies, reactions, and unreactions;
* generation ownership, task cancellation, and generation-independent semantic
  deduplication.

An advertised capability does not guarantee a successful request. A request
failure remains `Failed` when no adequate replay evidence exists, or `Partial`
when some canonical recovery completed. It is never silently relabeled
`Unsupported` after an actual supported request was attempted.

## 6. Result and evidence contract

Every completed strategy returns the existing
`ConnectionContinuityRecoveryResult` for its generation. Phase 33 adds typed
metadata without changing the Phase 32 result kinds:

* selected `Strategy` and `StrategyReason`;
* `RecoveredEventCount`;
* `UnresolvedGap`;
* `CommandsIssued`.

The result kinds remain `NotRequired`, `NoGapObserved`, `Recovered`,
`Unsupported`, `Partial`, and `Failed`. `StrongReplay` is only available when
the result is `Recovered`, replay completed, exact canonical-gap evidence was
established, and lossless continuity is supported. `Unsupported` is best
effort; `Partial` records bounded evidence with a remaining gap; `Failed`
records attempted recovery failure.

The strategy never calls `CompleteSynchronization`. The continuity owner
rejects non-final results, stale generations, and completions before
registration. Synchronization cannot complete while a required strategy is
still pending, but `Unsupported` is a valid terminating result.

## 7. Recovery boundary contract

`ConnectionRecoveryBoundary` contains the current generation, the previous
synchronized generation, capture time, durable conversation boundaries, and a
bounded list of known gap keys. Each conversation boundary contains its
durable conversation key, current server target, opaque last canonical msgid,
server timestamp, channel/query classification, and durable sequence.

It deliberately does not contain UI objects, socket objects, application
state-machine authority, or raw authentication data. The existing application
boundary remains the source for the detailed history executor; the strategy
sees only the protocol-neutral summary.

## 8. Cancellation and generation ownership

The owning synchronization CTS is cancelled on interruption, replacement,
manual disconnect, shutdown, or entry stop. Strategies check cancellation
before starting and after bounded executor work. A cancelled strategy returns
no result to the lifecycle owner. A stale callback is fenced by the current
generation and cannot complete synchronization or publish a replacement
result. A new generation constructs a new context and selects from its own
negotiated capabilities; it never inherits the previous strategy.

The bounded diagnostic ring remains capped at 256 entries. Strategy selection
records identity, reason, generation, and a non-secret boundary summary.
Synchronization completion records the result category, evidence, strategy,
and command count.

## 9. Deterministic fixture

`nexIRC.Networking.Testing.DeterministicServerHistoryFixture` is an in-process
protocol behavior simulator backed by `FakeIrcTransport`. It does not require
the internet, Docker, an IRCd installation, privileged ports, or real sleeps.
The fixture supports the narrow behavior needed by recovery tests:

* CAP LS, ACK, and NAK;
* message-tags, echo-message, server-time, account-tag, batch, and
  labeled-response advertisement;
* optional draft/chathistory and CHATHISTORY=50/MSGREFTYPES ISUPPORT;
* LATEST, BEFORE, AFTER, BETWEEN, AROUND, and TARGETS request shapes;
* canonical opaque msgids, server-time, account tags, relationship metadata,
  BATCH start/end, live messages, TAGMSG-compatible events, and controlled
  failure/disconnect/malformed responses.

Fixture events retain the exact opaque msgid string. Selection never assumes
numeric ordering or a server-specific id format. History is keyed by the
fixture's conversation target and can be scripted as complete, empty, bounded
partial, failed, malformed, or disconnected during response.

## 10. Fixture profiles

The implemented profile identities are:

* `FullIrcv3Replay`: full CHATHISTORY capability and replay batches.
* `NoHistory`: ordinary metadata capabilities but no usable CHATHISTORY.
* `AdvertisedHistoryFails`: capability is advertised but requests fail.
* `BoundedPartialReplay`: a bounded subset is returned.
* `ExactGap`: full history is available for exact boundary requests.
* `MissingParentRecoverable`: parent and relationship events can be scripted.
* `MissingParentUnrecoverable`: parent can be omitted to verify safe unresolved
  behavior.

The fixture profile is a human-facing test label. Production never branches
on it. Actual strategy selection continues to use negotiated evidence.

## 11. Live/replay races and relationships

The fixture can enqueue live events before or after a history request and can
repeat the same canonical msgid in either path. Existing generation-independent
semantic deduplication remains responsible for suppressing the duplicate; the
fixture does not fake deduplication.

The event model carries target, sender, account, msgid, server-time, command,
body, tags, and relationship metadata. This permits deterministic parent,
reply, reaction, unreaction, multi-actor, and multi-value reaction sequences.
Relationship resolution remains canonical and conversation-scoped: a missing
parent is recovered only by canonical replay evidence; an unrecoverable parent
does not trigger text guessing, cross-conversation attachment, or a blank
TAGMSG transcript row.

## 12. Queries and capability changes

The existing TARGETS/query path remains in the IRCv3 executor. Durable query
identity is reused only with the established account/target/generation evidence
policy. Channel and query conversation keys remain isolated.

Capability changes are represented by replacement `ServerSession` instances:

* Full → No History selects `BestEffortNoHistory` for the new generation.
* No History → Full selects `Ircv3ChatHistory` for the new generation.
* History → Broken History selects `Ircv3ChatHistory`, then returns `Failed` or
  `Partial` if the attempted request does not complete adequately.

## 13. Defects and repairs

The abstraction work exposed one defect in the new fixture only: an unknown
opaque reference was initially treated as index zero, causing LATEST to return
an empty batch rather than the scripted sequence. The fixture now returns
`-1` for an unknown reference and preserves the full deterministic replay.
No production recovery defect was exposed or repaired in this phase.

## 14. Regression results

Phase 33 focused tests cover capability-driven selection, initial/no-history
semantics, typed strategy results, lifecycle non-ownership, cancellation,
opaque canonical fixture replay, no-history fixture behavior, advertised
failure, and live ordering. Phase 31/32 continuity tests remain green, as do
the existing CHATHISTORY and history suites.

The complete validation matrix is recorded in the Phase 33 closeout. A live
IRC server is not required for the deterministic Phase 33 tests. The
inaccessible Phase 30 InspIRCd 4 runtime remains non-blocking.

## 15. Future bouncer and nexIRC Server integration

The context is intentionally not shaped around one command sequence. A future
Soju-style or standardized bouncer strategy can consume the same current
generation, boundary, durable-state executor, cancellation token, and bounded
diagnostics without changing continuity states.

A future `NexIrcResumeRecoveryStrategy` could use a persistent session
identity, negotiated resume capability, an account-bound resume/session token,
server-side sequence or replay boundary, authoritative replay, explicit replay
completion, token expiry/invalidation, and account binding. If the token is
invalid or unavailable, it can return structured failure or delegate to the
ordinary IRC strategy. It must still return
`ConnectionContinuityRecoveryResult`; it must not transition the lifecycle or
bypass the synchronization barrier.

Likely future precedence is authoritative nexIRC resume, standardized
bouncer/server replay, IRCv3 CHATHISTORY, and best-effort no-history. Phase 33
does not encode nonexistent strategies or protocol messages. Adding one later
requires a new strategy and selection policy evidence, not a second reconnect
architecture or new continuity lifecycle states.

## 16. Known limitations

The fixture intentionally is not a complete IRC server and does not model all
IRC numerics, SASL, permissions, server-specific limits, or real network
timing. The current production recovery executor remains application-owned and
is exposed to strategies through a bounded callback; a later phase may move
more durable-history orchestration behind a dedicated service if multiple
strategies need it. No proprietary nexIRC resume protocol is implemented.
