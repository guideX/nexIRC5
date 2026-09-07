# nexIRC 5 Phase 1T — Production-Paced Endurance, Memory Stability, and Real-Server Lifecycle

Date: 2026-09-06
Repository: `D:\dev\nexIRC\nexIRC5`
Branch: `main`

## Outcome

Phase 1T is accepted as Outcome A: the retained Phase 1S architecture remains stable under accelerated reconnect/history/search churn and under production-paced traffic, with quiet-period convergence and release-path cleanup verified.

The only narrow repair identified by the work was lifecycle hygiene in `JsonlConversationLogStore`: disposal now clears its history and search indexes after the writer drains. This is recorded as a secondary Outcome B repair, not as evidence of an application-wide leak. No scheduler redesign or broad ownership change was required.

## Mandatory preflight

The preflight was run from the actual Git root, `D:\dev\nexIRC\nexIRC5`:

| Field | Value |
|---|---|
| Branch | `main` |
| Starting HEAD | `674d686b9b7500f48f33eb535470f0235298269b` |
| Subject | `Implement nexIRC 5 Phase 1S sustained interactivity and WPF lifecycle` |
| Worktree | clean at start |
| Upstream | `origin/main` |
| Local ahead/behind | `0 behind, 1 ahead` |
| Local `origin/main` | `036f06ecbd4652a52b8d5c56a28eb36cae417b69` |
| Supplied expected HEAD | `674d686b9b7500f48f33eb535470f0235298269b` |

The supplied expectation referred to the Phase 1S HEAD, while the local tracking ref still pointed at Phase 1R during preflight. During the work, the existing local remote-tracking ref advanced from `036f06e` to `674d686` in a reflog entry marked `update by push`, matching the supplied expectation. This was observed state, not a fetch or push performed by this work. No fetch, pull, merge, rebase, or push was performed by this work.

At completion, HEAD was `40b4f801e9cdc1c8be04d68be958d357518cadbb`, `origin/main` was `674d686b9b7500f48f33eb535470f0235298269b`, and `main` was 1 commit ahead with a clean worktree.

## Retained Phase 1S baseline

Phase 1T preserves the accepted Phase 1S behavior: a cooperative FIFO workspace dispatcher with bounded slices; WPF-boundary diagnostics; manager-owned session/event draining; per-connection epoch fencing; bounded inbound and outbound transport queues; one writer per live session; natural QUIT flush; stale-generation and duplicate-semantic suppression; bounded JSONL persistence; history paging/search; live IRC/TLS lifecycle; and genuine WPF close/rendering unsubscription.

The Phase 1S deterministic UI baseline remains 11/11 scenarios passed, including sustained interactivity, plus the 8/8 close matrix. Phase 1T adds endurance coverage without replacing those scenarios.

## Implementation changes

The implementation adds non-retaining diagnostics for live `ServerSession` instances, manager pending dispatches, transport inbound items, persistence writer/index state, and pending presentation interaction. These counters are read-only observability; they do not retain completed work or production object graphs.

`tests/nexIRC.Application.Tests/Phase1TTests.cs` adds two deterministic tests:

1. `AcceleratedEnduranceKeepsReconnectChurnAndRetainedStateBounded`
2. `ProductionPacedEnduranceSettlesDuringQuietPeriods`

The tests cover registration, NAMES membership, JOIN/PART/NICK/TOPIC/MODE/NOTICE/PRIVMSG traffic, channel and query views, unread navigation, multi-network activity, manual reconnect generations, stale callbacks, duplicate semantic IDs, network removal/re-add, history paging, repeated search, quiet periods, forced GC checkpoints, process memory, and final disposal.

## Accelerated endurance workload

The accelerated run used 3 networks with 4 channels each, 720 initial traffic lines per network, 20 reconnect generations alternating across two networks, 600 traffic lines per reconnect generation, 6 history/search cycles, and 2,400 additional 480-character history records. Each channel received 12 NAMES users plus the local user, for 13 members.

Representative Release result:

| Metric | Result |
|---|---:|
| Inbound test events | 15,241 |
| Direct history records | 2,400 |
| Reconnect generations | 20 |
| Observed conversation projections | 176 peak |
| Observed member projections | 156 peak |
| Transport queue maximum | 667 |
| Pending state dispatch peak/final | 0 / 0 |
| Pending writes peak/final | 2,032 / 0 |
| History/search indexes peak/final | 1/1 / 0/0 |
| History files after flush | 36 |
| History files after first search | 38 |
| Stale callbacks discarded | 1 |
| Duplicate semantic events discarded | 24 |
| Managed session entries peak/final | 3 / 0 |
| Active transports peak/final | 3 / 0 |
| Active readers peak/final | 3 / 0 |
| Transport subscriptions peak/final | 3 / 0 |
| Managed memory after startup/population/heavy/churn/history/quiet | 1.72 / 4.04 / 4.01 / 5.77 / 5.19 / 5.07 MB |
| Managed memory at pre-shutdown quiet boundary | 5.07 MB |
| Total allocated bytes | 2,315,870,552 |
| Gen0/Gen1/Gen2 collections | 400 / 73 / 24 |
| Process working set baseline/peak/final | 73.1 / 101.0 / 97.0 MB |
| Process private memory baseline/peak/final | 29.3 / 46.3 / 39.9 MB |

The allocation total is workload cost, not retained memory. The relevant signal is that forced-GC retained managed memory reached a plateau, queues drained, indexes cleared, readers/subscriptions reached zero, and old transports were disposed exactly once.

## Production-paced workload

The paced run used 2 networks with 3 channels each, 180 traffic lines per network, 60 lines after one reconnect, 8 ms ordinary inter-line delay, 4-line bursts within each 20-line group, 150 ms pauses every 60 lines, 750 ms quiet periods, and 4 history/search cycles.

Representative Release result:

| Metric | Result |
|---|---:|
| Inbound test events | 506 |
| Reconnect generations | 1 |
| Observed conversation projections | 18 peak |
| Observed member projections | 78 peak |
| Transport queue maximum | 75 |
| Pending state dispatch peak/final | 1 / 0 |
| Pending writes peak/final | 26 / 0 |
| History/search indexes peak/final | 0/0 / 0/0 |
| History files after flush/first search | 10 / 10 |
| Managed session entries peak/final | 2 / 0 |
| Active transports peak/final | 2 / 0 |
| Active readers peak/final | 2 / 0 |
| Transport subscriptions peak/final | 2 / 0 |
| Managed memory after startup/population/paced-heavy/reconnect/history/quiet | 2.62 / 1.05 / 3.03 / 3.14 / 3.07 / 3.09 MB |
| Managed memory at pre-shutdown quiet boundary | 3.09 MB |
| Total allocated bytes | 44,114,864 |
| Gen0/Gen1/Gen2 collections | 22 / 18 / 18 |
| Process working set baseline/peak/final | 91.6 / 96.7 / 90.9 MB |
| Process private memory baseline/peak/final | 33.8 / 38.2 / 32.1 MB |

The quiet fence observed zero queue depth and zero pending writes, and no additional processed state actions during the quiet observation window. The test also asserts that the writer remains live before manager disposal and completes after final disposal.

## Correctness and lifetime evidence

- Per-channel membership converged to 13 users.
- Message order remained ordinal after reconnect churn.
- Stale-generation text never appeared in current conversation entries.
- Duplicate semantic traffic was discarded while legitimate traffic remained visible.
- Repeated search did not grow the file set after initial sidecars were created.
- Removed and re-added networks disconnected cleanly.
- Every registered fake transport reached one QUIT and one disposal.
- Final manager-owned session-entry count, reader count, callback subscription count, transport count, manager queue depth, pending dispatch count, and pending writes were zero.
- Disposed history/search indexes were zero after log-store disposal.

## WPF and real-server lifecycle

Phase 1T retains the Phase 1S WPF ownership boundary. `PresentationTimingProbe` now exposes a non-retaining pending-interaction diagnostic, and `MainWindow` exposes it internally for lifecycle assertions. The existing close harness continues to verify the genuine WPF close path, rendering unsubscription, callback unsubscription, reader shutdown, persistence flush, and natural process exit.

The release desktop validation included the deterministic sustained-interactivity scenario; it passed with `max_wpf_pending=1`. The accepted Phase 1S baseline remains 8/8 for the external close matrix. A fresh Phase 1T rerun of those external-click probes was not completed because the available desktop computer-use bridge exposed no targetable native app window (`apps=[]` and native app methods were unavailable); the shell-launched probes correctly remained waiting for a genuine close action and were stopped without force-closing the app.

The real-server `--live-smoke` path was rerun successfully: DNS/TCP/TLS, registration, self-WHOIS `318`, genuine `MainWindow.Close`, QUIT, disconnected state, stopped outbound reader, and natural process exit. It reported `close_request_to_window_closed_ms=77.896`. Any live-server result is reported separately from deterministic fake-transport evidence because it depends on DNS, network reachability, and the server’s current availability.

## Validation commands

```text
dotnet build .\nexIRC5.sln --configuration Release --no-restore --verbosity minimal /m:1
dotnet test .\tests\nexIRC.Application.Tests\nexIRC.Application.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Phase1TTests
dotnet test .\nexIRC5.sln --configuration Release --no-restore --verbosity minimal
dotnet run --project .\src\nexIRC.Desktop --configuration Release --no-build -- --demo --ui-smoke sustained-interactivity
dotnet run --project .\src\nexIRC.Desktop --configuration Release --no-build -- --live-smoke
```

The final report records the exact pass/skip/fail counts and any environment-dependent live-server result.

## Phase 1U recommendation

Proceed to Phase 1U product work. The foundation is sufficiently stable for product-facing improvements: richer channel/query navigation, clearer reconnect and persistence status, searchable history UX, notification/read-state polish, and broader account/server configuration. Keep the Phase 1T counters and the accelerated/paced suites as regression gates while product work proceeds.
