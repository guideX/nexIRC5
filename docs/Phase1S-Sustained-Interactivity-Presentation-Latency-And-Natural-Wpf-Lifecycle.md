# nexIRC 5 Phase 1S — Sustained Interactivity, Presentation Latency, and Natural WPF Lifecycle

Date: 2026-09-06
Repository: `D:/dev/nexIRC/nexIRC5`
Branch: `main`

## Outcome

Phase 1S produced two accepted outcomes:

- Outcome A for sustained responsiveness: realistic deterministic sustained traffic did not produce reproducible user-visible head-of-line blocking. Conversation, network, navigator, draft, unread, query, and rapid-switch interactions remained correct while traffic was active.
- Outcome B for a narrow projection defect: WPF collection generators became inconsistent after several ordered transcript mutations in one projection slice. Batched collection publication now uses one safe `Reset` notification.
- Outcome D for a natural WPF lifecycle defect: the normal registered-session close path could cancel the outbound writer before a queued `QUIT` was written. Session disposal now waits for the writer to acknowledge the `QUIT` write before cancellation, with a bounded timeout, and this is covered by a Networking regression.

No Outcome C scheduling limitation was proven. The cooperative FIFO authority and 32-item slice remain in place.

## Repository state

The required starting commit was present:

```text
036f06ecbd4652a52b8d5c56a28eb36cae417b69 Implement nexIRC 5 Phase 1R long-session durability
```

Preflight recorded a clean worktree on `main`, with `origin` configured as `git@github.com:guideX/nexIRC5.git`, and `ahead=1`, `behind=0` relative to the then-current remote-tracking `origin/main`.

During the task, the local `origin/main` reference later became equal to the Phase 1R HEAD. Its reflog records `update by push` at 2026-09-06 15:19:36 -0700. This task did not invoke fetch, pull, merge, rebase, or push; the change in the remote-tracking reference is recorded as an external repository-state discrepancy.

## Phase 1R baseline and metric meanings

Phase 1R reported:

- 16,672 inbound IRC lines;
- 3 concurrent networks;
- 243 observed conversation projections;
- 1,176 stable member projections;
- 6 reconnects;
- 10 transports disposed exactly once;
- zero surviving transport subscriptions;
- maximum authority queue depth 3,303;
- maximum pending WPF callbacks 1;
- cooperative slices 3,192 and yields 3,166;
- dispatcher-boundary p50/p95/p99 of 1.534/3.731/3.950 ms;
- authority-queue residence p50/p95/p99 of 520.832/1,012.614/1,098.794 ms.

The earlier presentation samples explain the Phase 1S distribution requirement. Phase 1Q’s 2,000-event run measured WPF wait/state/next-render values of 35.220/38.221/105.103 ms, while its 5,000-event run measured 24.289/26.792/107.588 ms. Phase 1R’s corresponding individual interaction-to-state and interaction-to-presentation samples were 180.846/366.763 ms for 2,000 events and 73.912 ms for the 5,000-event presentation sample. Their inversion is why Phase 1S reports bounded distributions across repeated interaction observations rather than treating one burst sample as representative.

Phase 1S keeps these measurements separate:

- Dispatcher-boundary latency is the wait to enter or begin processing at the serialized authority.
- Authority residence is the wait of accepted work behind earlier accepted authoritative work.
- User-action state latency is the interval from a WPF interaction callback to the corresponding observable WPF state change.
- Presentation-opportunity latency is the interval from the interaction to the next `CompositionTarget.Rendering` opportunity after that state change.
- Physical display latency was not measured and is not claimed.

## Sustained workload

The deterministic WPF sustained-interactivity scenario uses two fake networks:

- Network A / Alpha: 3,600 ordinary `PRIVMSG` events to `#general`, sent in batches of 32 with 1 ms pacing.
- Network B / Beta: 360 ordinary `PRIVMSG` events to `#general`, sent in batches of 24 with 2 ms pacing, plus one quiet-network unread marker.
- Total measured traffic: 3,961 events.
- Observed elapsed traffic interval: 1,584.373 ms.
- Approximate accelerated rate: 2,500.043 events/s.
- User interactions: 11; all 11 overlapped active traffic.
- Maximum authority queue depth: 3,172.
- Queue depth at interaction: minimum 60, p50 1,032, p95 1,374, p99 1,374, maximum 1,374.
- Current oldest queued work age at interaction: minimum 3.405 ms, p50 186.861 ms, p95 323.214 ms, p99 323.214 ms, maximum 323.214 ms.

This is deliberately accelerated deterministic traffic for regression testing. It is not a claim about a production server’s wire rate.

The scenario also retains the existing 2,000-event and 5,000-event burst smoke cases. Those cases passed after the Phase 1S changes.

## Interactivity measurements

The final sustained run collected 11 observations. Percentiles use the repository’s ceiling-rank convention.

| Measurement | Minimum | p50 | p95 | p99 | Maximum |
|---|---:|---:|---:|---:|---:|
| WPF callback wait | 0.075 ms | 0.364 ms | 25.085 ms | 25.085 ms | 25.085 ms |
| Interaction to state | 0.330 ms | 1.981 ms | 43.992 ms | 43.992 ms | 43.992 ms |
| State to presentation opportunity | 0.864 ms | 18.519 ms | 34.913 ms | 34.913 ms | 34.913 ms |
| Interaction to presentation opportunity | 1.193 ms | 21.765 ms | 68.927 ms | 68.927 ms | 68.927 ms |

The presentation row means the next observed WPF rendering opportunity after the state change. It is not physical pixel delivery latency.

The run also reported recent completed authority samples from the bounded diagnostics window:

- authority residence: minimum 3,993.214 ms, p50 5,129.237 ms, p95 5,438.784 ms, p99 5,486.788 ms, maximum 5,496.658 ms;
- dispatcher-boundary wait: minimum 0.001 ms, p50 1.759 ms, p95 3.712 ms, p99 3.936 ms, maximum 3.998 ms;
- maximum pending WPF callbacks: 1;
- cooperative slices: 258;
- cooperative yields: 251;
- maximum slice size: 32 work items;
- maximum slice duration: 21.078 ms;
- WPF callbacks posted/executed: 252/258;
- navigation refresh requests/executions/coalesced: 4,018/18/4,017.

The large authority-residence values are not equivalent to user-action state latency. The interaction callback executes through the WPF boundary and changes the relevant view-model state while old IRC work remains queued. The evidence classifies the Phase 1R approximately one-second authority tail as mixed:

- A — expected historical IRC backlog under the accepted cooperative architecture; and
- D — the sample includes work that is not semantically relevant to the current interaction.

It is not evidence of user-visible head-of-line blocking. No reproducible interaction was found that had to wait for the complete historical authority backlog.

## User interactions and final-state assertions

The sustained scenario measured and asserted:

1. selecting another conversation;
2. selecting another network;
3. activating a conversation through the navigator;
4. editing a draft/input value;
5. switching away and restoring the draft;
6. selecting a conversation to clear unread/activity state;
7. opening an existing historical query destination;
8. rapidly switching among conversations;
9. restoring a quiet-network navigator destination;
10. editing input after navigation;
11. final conversation selection back to the noisy network.

The conversation-selection, network-selection, navigator, draft/input, draft restoration, unread/activity, historical query, rapid-switch, and final selection assertions all passed. The noisy Alpha tail was FIFO ordered; the quiet Beta tail was FIFO ordered; Alpha messages did not appear in Beta. Beta’s unread marker was observed before selection and cleared by the selection interaction.

## Cross-network fairness

Alpha was the noisy network and Beta was the quiet network. Interactions were performed across both networks while both producers were active. Beta state remained correct, Beta unread/activity classification was isolated, and no Alpha traffic crossed into Beta. The test also fixes a real activation issue: activating a view now deactivates the previously active view on other networks before unread classification. This preserves network isolation without introducing a second authority or a priority scheduler.

## Production changes

The following narrow changes were justified by reproducible user-facing or lifecycle evidence:

- `ThreadSafeObservableCollection<T>` now publishes one `Reset` notification after a deferred projection batch. WPF `ItemsControl` generators did not reliably consume a range Add/Remove event after several ordered mutations in one slice; the old behavior produced an actual inconsistent-items-source exception in sustained smoke.
- Navigator activation now selects the resolved `WorkspaceView` rather than passing a `Guid` back through the selection path.
- Cross-network activation deactivates the prior active view so unread/activity classification cannot treat a view on another network as still active.
- `WorkspaceDispatchDiagnostics` exposes current oldest queued-work age independently from completed sample residence.
- Rendering subscription ownership is explicit and idempotent. `MainWindow.OnClosed` unsubscribes exactly once, cancels a pending presentation probe, and completes the close fence.
- Registered-session disposal leaves the session cancellation token live long enough to enqueue and write `QUIT`; the writer acknowledges that write before the session is cancelled, bounded by two seconds. The transport regression asserts one `QUIT`, one disposal, zero callbacks, and zero active reads.

No arbitrary priority queue, event dropping, reordered IRC semantics, uncontrolled `Task.Run`, per-event WPF posting, timer-driven flushing, or parallel authoritative state mutation was introduced.

## Natural WPF close matrix

The Release executable was launched separately for each scenario. Each scenario used the application’s real `MainWindow` close handling; no forced termination was used as acceptance.

| Scenario | State exercised | Result | Close request to natural process exit |
|---|---|---|---:|
| `close-idle` | usable idle window | pass, exit 0 | 115.567 ms |
| `close-sustained` | 6,000 Alpha + 600 Beta inbound events still producing | pass, exit 0 | 127.487 ms |
| `close-backlog` | 10,000 Alpha + 2,000 Beta accepted inbound events | pass, exit 0 | 147.267 ms |
| `close-reconnect` | remote disconnect at reconnect boundary | pass, exit 0 | 125.702 ms |
| `close-partial` | transport connected before IRC registration | pass, exit 0 | 96.207 ms |
| `close-registered` | two registered fake sessions | pass, exit 0 | 119.003 ms |
| `close-persistence` | 1,000 accepted events with persistence active | pass, exit 0 | 124.299 ms |
| `close-interacted` | view switches, query, and draft restoration | pass, exit 0 | 226.833 ms |

The in-process close probe waits for the window close fence, verifies the rendering handler is detached, verifies tracked fake transports have zero callbacks and zero active reads, and counts outbound `QUIT`/disposal state. All eight probes passed those assertions. The outer process result was natural termination with code 0 for all eight scenarios.

The matrix therefore covers idle, sustained traffic, substantial accepted backlog, reconnect, partial connection, registered connection, persistence activity, and repeated interaction.

## Repeated Release launch/close

Eight successful Release launch-and-close cycles were completed as the eight natural-close matrix scenarios. All eight returned exit code 0. No child `nexIRC.Desktop` process remained after the matrix. The test harness did not use force termination.

## Live Libera WPF close proof

The final accepted run used the Release WPF executable and the normal `MainWindow.Close()` path:

```text
LIVE_WPF_TRACE dns=true tcp=true tls=true registration=001
LIVE_WPF_TRACE whois=318
LIVE_WPF_TRACE preclose_state=Registered preclose_registration=Registered
LIVE_WPF_TRACE close_origin=MainWindow.Close
LIVE_WPF_RESULT states=Connecting,TlsNegotiation,Connected,CapNegotiation,Registering,Registered,Disconnected quit=True disconnected=True outbound_reader_stopped=True natural_window_close=true close_request_to_window_closed_ms=36.791
EXIT code=0
```

No credentials were used. TLS used normal certificate validation. DNS, TCP, TLS, CAP/NICK/USER registration, `001`, self-WHOIS `318`, `QUIT`, `Disconnected`, and natural process exit were all observed.

Two post-repair retry attempts encountered a remote disconnect between WHOIS completion and the close request; both correctly reported `Disconnected` but no `QUIT` because the server-side connection had already ended. They are recorded as external timing observations, not accepted close proofs. The accepted run captured `Registered` immediately before `MainWindow.Close()` and verified `QUIT=True`.

The earlier pre-repair WPF run reproduced `Disconnected=True` with `QUIT=False` and a roughly two-second close wait. That was the concrete lifecycle defect repaired by preserving writer cancellation until the queued `QUIT` write was acknowledged.

## Resource ownership

Repeated presentation measurements and close probes leave no rendering handler attached. `MainWindow.OnClosed` is idempotent, cancels any pending probe completion, and removes the `CompositionTarget.Rendering` handler. The close matrix verifies this after each natural close. The live probe also waits for its outbound measurement reader to stop before reporting success.

Transport callback ownership remains covered by Phase 1R and the new Phase 1S Networking test. The final focused and full Networking suites passed with no callback/read leaks.

## Regression and validation results

- Focused Application Phase 1Q/1R/1S tests: 7 passed, 0 failed.
- Focused Networking Phase 1R/1S tests: 2 passed, 0 failed.
- Core tests: 50 passed, 0 failed.
- Full Networking tests: 27 passed, 0 failed.
- Full Application tests: 115 passed, 1 skipped, 0 failed. The skip is the existing opt-in Phase 1O benchmark when the opt-in environment variable is absent.
- Opt-in Phase 1O deterministic performance benchmark: 1 passed, 0 failed.
- Aggregate solution test run: 192 passed, 1 skipped, 0 failed.
- Phase 1R long-session harness: passed; its accepted ownership/convergence coverage remains green.
- Deterministic UI smoke: 11/11 passed (`participant`, `moderation`, `channel-properties`, `multi-network`, `lifecycle`, `read-state`, `reconnect`, `burst`, `burst-fairness`, `query-nick`, `sustained-interactivity`).
- Burst UI smoke: 2/2 passed (2,000 and 5,000 event runs).
- Sustained-interactivity matrix: 1/1 passed, 11/11 observations overlapped active traffic.
- Natural-close matrix: 8/8 passed with natural exit code 0.
- Release build: passed with 0 warnings and 0 errors.
- Format verification: `dotnet format nexIRC5.sln --verify-no-changes --no-restore` passed with exit code 0.

The full solution test suite retained the repository’s opt-in benchmark skip policy. Running the opt-in benchmark separately passed.

## Limitations and recommendation

The sustained traffic is deterministic fake-transport traffic and is intentionally accelerated. It proves interaction behavior and semantic convergence under a bounded queue pressure shape, not a universal real-world throughput guarantee. Physical pixels were not measured. Live Libera is inherently subject to external remote disconnect timing; the final registered pre-close run passed the complete WPF close proof.

Phase 1T should preserve the serialized authoritative architecture and focus on broader long-duration usability and production-scale observability: longer mixed traffic traces, more representative pacing distributions, memory/GC behavior over hours, and additional real-server lifecycle samples. It should not promote authority residence alone to a user-facing latency requirement unless a future test ties it to a reproducible WPF state or rendering-opportunity delay.
