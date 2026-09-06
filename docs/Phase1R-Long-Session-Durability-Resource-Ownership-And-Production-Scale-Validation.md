# Phase 1R — Long-Session Durability, Resource Ownership, and Production-Scale Validation

Date: 2026-09-06
Repository: `D:\dev\nexIRC\nexIRC5`
Branch: `main`

## 1. Starting repository state

The Phase 1R preflight found the requested repository, branch, and starting
commit:

| Item | Value |
|---|---|
| Starting HEAD | `ba4f7a72bd563726711616877a9d72d0bbb01088` |
| Subject | `Implement nexIRC 5 Phase 1Q presentation and shutdown reliability` |
| Worktree | clean |
| Upstream | `origin/main` |
| Ahead / behind | `0 / 0` |

No fetch, pull, merge, rebase, or push was performed. Existing Phase 1P and
Phase 1Q conclusions were treated as accepted architecture, especially the
single cooperative FIFO authority and the measured maximum WPF callback depth
of one.

## 2. Architecture and ownership audit

The inspected path remains:

```text
ServerSession reader/writer and protocol state
  -> NetworkSessionManager session event handlers
  -> one SerializedWorkspaceDispatcher FIFO
  -> WPF or test dispatcher boundary
  -> NetworkWorkspace / conversation projections
  -> bounded transcript, member, activity, navigation, and notification state
```

The lifetime model is explicit:

* `NetworkSessionManager` owns one session entry per configured network,
  attaches session events, starts the five event drainers, and removes its
  handlers before stopping a session.
* `ServerSession` owns the supervisor cancellation source, one connection
  epoch at a time, the per-connection linked cancellation source, outbound
  channel, reader, writer, and transport. A transport is disconnected and
  disposed in the supervisor finally path.
* `NetworkWorkspace` owns logical channel and query views. Closing a view is a
  presentation operation so it can be reopened; explicit removal of a
  historical conversation removes the logical view. This is intentional
  retained state, not an inferred leak.
* `ConversationNavigationHistory` is bounded by
  `ConfigurationLimits.MaximumConversationNavigationHistory` and stores
  identities rather than owning views.
* Operation timeout tasks use per-operation cancellation sources and retire
  them on completion, cancellation, or disconnect.
* `JsonlConversationLogStore` owns one bounded 2,048-entry writer channel. It
  uses short-lived file streams for writes and reads, and caches only validated
  history/search index snapshots keyed by source path. The store drains and
  disposes its writer task during shutdown.
* Projection notifications remain coalesced only at the already-proven
  derived-presentation boundaries. IRC semantic events remain individual FIFO
  actions.

One concrete ownership defect was found: `ServerSession` subscribed an
anonymous callback handler to every callback-capable transport connection but
did not remove it. The handler used a weak session reference, so this was not
shown to retain the session, but it left a stale subscription on the transport
until transport disposal. The narrow repair stores the handler and removes it
in the connection finally block. No scheduler redesign was made.

## 3. Observability added

The deterministic fake transport now exposes test-only lifecycle counters:

* callback subscription count;
* active and maximum concurrent read count;
* disposal count and disposed state;
* factory-created transport count and remaining scripted transports.

The existing application diagnostics were reused for:

* serialized queue depth and maximum depth;
* bounded recent dispatch samples and p50/p95/p99 queue and dispatcher-boundary
  timing;
* cooperative slice/yield counts;
* maximum slice work items and duration;
* stale-generation and duplicate semantic-event discards;
* dispatcher-boundary posted/executed/pending work.

The harness does not add production heap walks, static object registries, or
retention roots. Managed working-set movement was not used as leak evidence.

## 4. Production-shaped long-session harness

`tests/nexIRC.Application.Tests/Phase1RTests.cs` runs an accelerated,
deterministic workload through the real application/session path and a
counting dispatcher boundary:

| Dimension | Configuration |
|---|---:|
| Concurrent initial networks | 3 |
| Channels per initial network | 8 |
| NAMES identities per channel | 48 plus one WHO-observed identity |
| Stable final members | 49 per channel; 1,176 across 3 networks |
| Initial traffic messages per network | 3,600 |
| Traffic per reconnect generation/network | 800 |
| Manual reconnect cycles | 6 total, 2 per network |
| Remove/re-add exercise | 1 logical network, then removed again |
| Logical network configurations exercised | 4 |
| History segment size | 64 KiB test setting |
| Total inbound IRC lines | 16,672 in the representative run |

The workload includes CAP/ISUPPORT/registration, channel JOIN, NAMES, WHO,
WHOIS/318, topic, mode, member churn, NICK, PRIVMSG, NOTICE, direct messages,
unread/activity transitions, favorites, recents, view close/reopen,
historical-conversation removal, navigation selection, history pages, search,
intentional reconnect, session replacement, network removal/re-addition, and
idempotent application disposal.

The transcript assertions compare each channel's exact ordered suffix after
normal lifecycle entries are accounted for and verify the 500-entry projection
bound. Member projections converge to the resynchronized 49-member set after
each replacement; old generation members do not survive reconnect.

## 5. Resource results

Representative stable counts from the soak were:

| Resource | Before traffic | After six reconnects | After intentional remove/re-add | After shutdown |
|---|---:|---:|---:|---:|
| Active transport sessions | 3 | 3 | 2 | 0 |
| Active fake transport reads | 3 | 3 | 2 | 0 |
| Transport callback subscriptions | 3 | 3 | 2 | 0 |
| Transport instances created | — | 9 | 10 | 10 disposed exactly once |
| Maximum concurrent read loops per transport | — | 1 | 1 | 1 |
| Maximum pending dispatcher callbacks | — | 1 | 1 | 0 current |
| History files after writer flush | — | — | 171 | 171 stable before indexing |
| History files after first search/index build | — | — | 171 | 171 stable across repeat searches |

The lower post-remove/re-add active count is intentional: the re-added network
was removed again before the final shutdown check. Every old transport reached
zero active reads, zero callback subscriptions, and disposed exactly once.
Repeated history searches did not create additional files after the first
validated index build.

No monotonic unintended growth was discovered. The 243 observed view
projections in the representative diagnostic include intentionally retained
logical channel/query state across the three workspace plans; closed views and
historical removal behavior were exercised separately. Favorites, recents,
navigation history, transcript tails, operation feedback, and index files are
bounded or intentionally persistent according to their existing ownership
contracts.

## 6. Scheduler and responsiveness results

The long-session run retained one FIFO authority and reported:

| Metric | Result |
|---|---:|
| Maximum serialized queue depth | 3,303 |
| Cooperative slices | 3,192 |
| Cooperative yields | 3,166 |
| Maximum pending dispatcher-boundary callbacks | 1 |
| Authority queue p50 / p95 / p99 | 520.832 / 1,012.614 / 1,098.794 ms |
| Dispatcher-boundary wait p50 / p95 / p99 | 1.534 / 3.731 / 3.950 ms |

The authority queue values reflect three producers and a deliberately large
long-session workload; they are not WPF frame latency. The dispatcher-boundary
values are comparable to the Phase 1Q WPF scheduling reference of
`1.918 / 3.795 / 3.942 ms` and do not demonstrate a new user-facing
scheduling limitation. The long-session test boundary is not WPF and does not
claim physical pixel latency.

Phase 1Q's actual WPF burst measurements remain the presentation reference:

* 2,000 events: WPF wait `35.220 ms`, state change `38.221 ms`, next
  presentation opportunity `105.103 ms`, `102/93` slices/yields.
* 5,000 events: WPF wait `24.289 ms`, state change `26.792 ms`, next
  presentation opportunity `107.588 ms`, `217/208` slices/yields.

No new `CompositionTarget.Rendering` measurement was added to the non-WPF
long-session harness. The existing Phase 1Q terminology remains in force:
interaction-to-WPF-state and next WPF presentation opportunity, not physical
pixel presentation.

## 7. Correctness and lifecycle results

The soak passed the following local convergence checks:

* ordered channel tails remained exact and bounded;
* member state resynchronized exactly after replacement;
* unread/activity state was raised for inactive conversations and cleared on
  selection;
* navigator history remained within its configured bound;
* favorites and recents remained scoped and bounded;
* history page reads and content searches returned persisted workload data;
* six reconnects replaced transports without duplicate active read loops;
* stale generation work was fenced. The representative run discarded `10`
  stale-generation events and recorded `0` duplicate semantic events;
* old callback-capable transports could no longer deliver callbacks after their
  connection finally path;
* existing Phase 1Q query-NICK coverage retained stable query identity,
  history key, draft, unread, highlight, and navigator behavior;
* existing NICK, history, collision, reconnect, and network-isolation
  regressions remained covered by the aggregate suites.

The lifecycle-focused Networking test passed with one reconnect replacement,
zero callback subscriptions after each connection finally path, one disposal
per transport, and zero active reads after shutdown.

## 8. Shutdown and live validation

The soak called manager disposal twice and completed both calls. The accepted
FIFO work drained before the dispatcher completion fence, the post-completion
queue remained closed, and no transport read or callback subscription remained.
The existing Phase 1Q shutdown tests continue to prove one QUIT per relevant
live session, post-shutdown reconnect fencing, and no callback after final
completion. No force termination was used by the deterministic Phase 1R
harness.

The prior Phase 1Q Release WPF close proof remains the natural-process-exit
authority: `CloseMainWindow()` exited the `nexIRC 5` process naturally with
code 0 and no force-stop. The deterministic UI smoke close path also remains
natural.

Live IRC validation succeeded separately with normal TLS validation, a
temporary nickname, no credentials, self-WHOIS, QUIT, and bounded cleanup:
DNS, TCP, TLS, CAP/NICK/USER registration, numeric `001`, numeric `318`,
intentional QUIT, and final `Disconnected` were all observed. No channel was
joined and no credentials were used.

## 9. Validation record

Final Release validation recorded:

* focused Phase 1R tests: **2 passed** (one Networking lifecycle test and one
  Application long-session test);
* Core: **50 passed**;
* Networking: **26 passed**;
* Application: **113 passed, 1 skipped** — the existing opt-in Phase 1O
  performance fact;
* aggregate: **189 passed, 1 skipped, 0 failed**;
* opt-in Phase 1O performance matrix: **12/12 rows passed**, all
  `ordered=True`;
* deterministic UI smoke: **10/10 passed**;
* burst smoke: **2/2 passed** (`burst` and `burst-fairness`), each with
  `max_wpf_pending=1` and `tail_ordered=true`;
* long-session harness: **1/1 passed**;
* Release solution build: **0 warnings, 0 errors**;
* `dotnet format nexIRC5.sln --verify-no-changes --no-restore`: passed.

The first concurrent baseline command also exposed one timing-sensitive
Networking test enumeration failure; the isolated test and the final complete
Release suite passed without changing or weakening that test.

## 10. Outcome and Phase 1S recommendation

Phase 1R is expected to classify as **Outcome B — Defect found and repaired**:
the transport callback subscription ownership defect had a deterministic
reconnect reproducer, a narrow explicit-unsubscribe repair, focused regression
coverage, and a passing production-shaped soak.

The live validation result is **Outcome A for external validation**: the
network lifecycle reached registration, WHOIS completion, QUIT, and
`Disconnected` cleanly. The overall Phase 1R classification remains Outcome
B because the transport callback ownership defect was repaired.

Phase 1S should preserve the cooperative FIFO architecture and focus on any
remaining evidence gaps: repeat natural WPF close validation on a second host,
longer real-world history retention observation, and optional presentation
opportunity sampling during a production-shaped desktop session. A deeper
authority/presentation split is not justified by Phase 1R measurements.
