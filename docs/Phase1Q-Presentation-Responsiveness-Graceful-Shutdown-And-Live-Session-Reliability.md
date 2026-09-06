# Phase 1Q — Presentation Responsiveness, Graceful Shutdown, and Live Session Reliability

Date: 2026-09-06
Repository: `D:\dev\nexIRC\nexIRC5`
Branch: `main`

## 1. Starting repository state

The Phase 1Q audit started from a clean worktree at `cc3514bf5fe4956bda54cde7f773d25e69b90685`, subject `Implement nexIRC 5 Phase 1P cooperative burst scheduling`. The branch was `main`, and the live comparison was `0 ahead / 0 behind` against `origin/main`. The handoff estimate expected approximately `1 ahead / 0 behind`; that difference was preserved. No fetch, pull, merge, rebase, or push was performed.

## 2. Phase 1P handoff

Phase 1P retained one FIFO serialized authority stream, cooperative slices of up to 32 actions or approximately 4 ms, `Background` protocol presentation, `Input` interactive work, `ContextIdle` navigator refresh, and safe derived-notification coalescing. Its representative 2,000-event WPF measurements were p50 `1.796 ms`, p95 `3.788 ms`, and p99 `3.963 ms`; it reported 106 slices, 97 yields, maximum 31 actions per slice, and approximately 100 WPF callbacks. The 5,000-event run reported 225 slices and 216 yields.

The two open evidence gaps were ordinary `CloseMainWindow()` exit and a conservative Libera run that stopped after reaching TLS negotiation. Phase 1Q therefore audited those paths before changing architecture.

## 3. Shutdown reproduction and classification

The Phase 1P ordinary-close result was reproduced against the actual Release executable using a native `CloseMainWindow()` request. In the Phase 1Q run the process reached a top-level window titled `nexIRC 5`, accepted the close request, exited naturally with code `0`, and required no forced termination. A second post-change run had the same result.

The Phase 1P failure is classified as **A / validation-harness or environment-dependent behavior**, not a demonstrated production shutdown defect. The original report did not contain a concrete stuck task, foreground thread, socket, or dispatcher exception. The current executable did not reproduce the hang, so no forced-exit workaround was added.

The close path is:

```text
Window close request
  -> MainWindow.Closing
  -> asynchronous MainWindowViewModel shutdown
  -> NetworkSessionManager session stop and log-store drain
  -> SerializedWorkspaceDispatcher.CompleteAsync fence
  -> configuration/credential/notification cleanup
  -> MainWindow.Close()
  -> App shutdown and process exit
```

## 4. Ownership findings

The audit found no tray component, hidden owned window, reconnect timer, or synchronous UI wait in the ordinary startup path. Network reader/writer loops are cancellation-owned by `ServerSession`; each connection has a generation epoch; the manager owns event-drainer tasks and the serialized workspace dispatcher; `JsonlConversationLogStore` owns a single bounded writer queue; and configuration persistence is awaited by the view model shutdown path.

The concrete hardening was idempotence rather than a new scheduler. `NetworkSessionManager`, `MainWindowViewModel`, and `ServerSession` now share the in-flight shutdown task across repeated disposal requests. `ServerSession` also emits at most one shutdown `QUIT` for concurrent disconnect callers. This removes early-return races and duplicate-disposal behavior without changing session semantics.

## 5. Shutdown architecture before and after

Before Phase 1Q, shutdown used a boolean guard in the desktop view model and a boolean manager guard. A second caller could return before the first shutdown completed. `ServerSession` similarly relied on a boolean disposed flag and could receive concurrent disconnect requests.

After Phase 1Q, the first shutdown caller creates one task; all later callers await that same task. The window still cancels the first WPF close event while asynchronous shutdown runs, then closes in a `finally` block. If a recoverable shutdown exception is observed, it is reported to stderr and the close is still completed; normal operation does not use `Environment.Exit`, `Process.Kill`, or equivalent termination.

## 6. Cooperative queue shutdown semantics

The policy is **finish every already accepted serialized authority action, then fence the queue**. `SerializedWorkspaceDispatcher.CompleteAsync()` stops acceptance and waits for all accepted actions. It is idempotent. After completion, a new submission throws `ObjectDisposedException`, and no queued callback can execute through that dispatcher.

No IRC meaning is coalesced or abandoned. Presentation notification coalescing remains limited to derived binding invalidations and navigator refresh requests. During application shutdown, sessions are intentionally terminated first, event subscriptions are detached, and queued actions already carrying the manager’s shutdown fence become no-ops when they reach the manager. The window is not closed until the accepted dispatcher work has drained, so a callback cannot resurrect a closed view.

## 7. Burst shutdown behavior

`Phase1QTests` exercises shutdown during accepted 2,000- and 5,000-action bursts. Both runs drain all accepted actions in FIFO order, report multiple cooperative slices, leave zero queue depth, reject post-completion work, and complete repeated shutdown calls successfully. A queued interactive action is also verified to complete during shutdown rather than being silently dropped.

The desktop burst smokes close after convergence; the deterministic dispatcher tests are the authoritative close-during-burst regression because they control the queue boundary without relying on a fragile timing race in WPF.

## 8. Presentation and input instrumentation

`MainWindow` now subscribes to `CompositionTarget.Rendering`. The burst smoke marks the interaction request immediately before posting the `Input`-priority selection, marks the WPF-selected state inside the selection callback, and records the next rendering callback. Metrics are emitted as a **presentation opportunity**, not as proof that physical pixels reached a monitor.

The selection action directly changes the workspace selection on the WPF boundary; it does not enqueue a second serialized authority action. Consequently `selection_authority_wait_ms` is reported as `n/a`, while WPF scheduling wait, WPF state-change timing, and the next composition opportunity are measured explicitly.

## 9. 2,000-event presentation result

The final ten-scenario UI smoke run reported:

| Metric | Result |
|---|---:|
| Selection WPF scheduling wait | `35.220 ms` |
| Selection callback execution | `3.404 ms` |
| Interaction to WPF state | `38.221 ms` |
| WPF state to next presentation opportunity | `66.882 ms` |
| Interaction to next presentation opportunity | `105.103 ms` |
| Selection before burst completion | `true` |
| Maximum pending WPF callbacks | `1` |
| Cooperative slices / yields | `102 / 93` |
| WPF schedule p50 / p95 / p99 | `1.918 / 3.795 / 3.942 ms` |

The transcript retained the exact bounded 500-entry tail in order, and activity/read-state, member, and navigator state converged.

## 10. 5,000-event presentation result

The final ten-scenario UI smoke run reported:

| Metric | Result |
|---|---:|
| Selection WPF scheduling wait | `24.289 ms` |
| Selection callback execution | `2.902 ms` |
| Interaction to WPF state | `26.792 ms` |
| WPF state to next presentation opportunity | `80.796 ms` |
| Interaction to next presentation opportunity | `107.588 ms` |
| Selection before burst completion | `true` |
| Maximum pending WPF callbacks | `1` |
| Cooperative slices / yields | `217 / 208` |
| WPF schedule p50 / p95 / p99 | `1.689 / 3.623 / 3.905 ms` |

The 5,000-event run also retained the ordered 500-entry tail, continued projection after the selection callback, and converged member/activity/navigator state without starvation or livelock.

## 11. Phase 1P performance comparison

The 2,000-event WPF scheduling percentiles remain within run-to-run host variation and do not materially regress Phase 1P:

| Percentile | Phase 1P | Phase 1Q final smoke |
|---|---:|---:|
| p50 | `1.796 ms` | `1.918 ms` |
| p95 | `3.788 ms` | `3.795 ms` |
| p99 | `3.963 ms` | `3.942 ms` |

The authoritative application queue is intentionally not treated as a physical input-latency measure. It remains a diagnostic pressure signal for the single FIFO authority stream.

## 12. Libera Phase 1P failure reproduction

The Phase 1P report recorded a run that reached `TlsNegotiation` and failed before registration. It did not preserve a lower-level exception proving a malformed registration sequence, capability defect, or shutdown defect. The Phase 1Q classification is **A / transient or external network/TLS boundary**, with no production protocol fix justified by that single failure.

## 13. Conservative live IRC proof

The Phase 1Q live run used `irc.libera.chat:6697` with platform TLS validation, a temporary nickname, no password, SASL disabled, no channel join, no flood traffic, self-WHOIS, and intentional disconnect. The safe phase trace was:

```text
Connecting
TlsNegotiation
Connected
CapNegotiation
OUTBOUND CAP (three capability commands: LS/REQ/END)
OUTBOUND NICK
OUTBOUND USER
Registered after server 001
OUTBOUND WHOIS
server 318
OUTBOUND QUIT
Disconnected
```

DNS resolution and TCP connection succeeded as part of the successful transport connect; TLS succeeded on the `TlsNegotiation -> Connected` transition. Certificate validation was not weakened. `001`, self-WHOIS `318`, and clean `QUIT`/disconnect were all observed. The headless diagnostic now prints outbound command names and generations only; it does not print credentials, SASL payloads, or unnecessary private command contents.

## 14. Network/shutdown crossover

Deterministic coverage now includes:

* cancellation while the transport factory is still creating a transport;
* disposal during partial registration;
* concurrent disposal requests;
* reconnect-enabled shutdown with no second connection;
* clean registered disconnect and single-`QUIT` behavior;
* completion fencing after accepted presentation work.

The cancellation boundary completed within the test timeout, no reconnect started after disposal, and the final session state was `Disconnected`. Live timing at every individual DNS/TCP/TLS phase was not used as an automated gate; the fake transport tests are the repeatable regression authority.

## 15. Query identity and workspace regression

The retained Phase 1O/1P coverage remains green for stable direct-NICK query identity and history keys, draft continuity, unread/highlight continuity, no extra NICK notification, collision isolation, nickname reuse, reconnect boundaries, network isolation, transcript order, the 500-entry tail, and generation fencing. The `query-nick` desktop smoke reported `same_view=true history_key_stable=true draft_restored=true query_count=1`.

## 16. Twelve-row performance matrix

The opt-in Phase 1P matrix passed all 12 rows with `ordered=True`:

| Events | Networks | Conversations | Wall ms | Max queue | p95 queue ms | p99 queue ms |
|---:|---:|---:|---:|---:|---:|---:|
| 100 | 1 | 1 | 23.049 | 1 | 0.004 | 0.025 |
| 100 | 1 | 32 | 15.391 | 1 | 0.001 | 0.003 |
| 100 | 2 | 1 | 16.002 | 38 | 0.800 | 4.319 |
| 100 | 2 | 32 | 15.420 | 82 | 3.678 | 3.776 |
| 1,000 | 1 | 1 | 76.421 | 1 | 0.001 | 0.003 |
| 1,000 | 1 | 32 | 107.727 | 1 | 0.002 | 0.003 |
| 1,000 | 2 | 1 | 29.665 | 401 | 9.739 | 9.999 |
| 1,000 | 2 | 32 | 30.572 | 698 | 15.291 | 15.426 |
| 5,000 | 1 | 1 | 94.734 | 1 | 0.000 | 0.001 |
| 5,000 | 1 | 32 | 123.365 | 1 | 0.001 | 0.002 |
| 5,000 | 2 | 1 | 94.252 | 2,415 | 38.709 | 48.575 |
| 5,000 | 2 | 32 | 154.835 | 3,868 | 80.670 | 80.902 |

The 12-row test passed in `12.240 s`.

## 17. Complete validation

* Core tests: **50 passed, 0 failed**.
* Networking tests: **25 passed, 0 failed**.
* Application tests: **112 passed, 0 failed, 1 skipped**. The only skip is the existing opt-in Phase 1O performance fact when the environment variable is not set.
* Focused Phase 1Q tests: **4 passed**; they are included in the Application total.
* Phase 1P/1O cooperative and query regression coverage: passed through the Application suite and the 12-row opt-in matrix.
* Deterministic UI smoke: **10/10 passed** — participant, moderation, channel-properties, multi-network, lifecycle, read-state, reconnect, burst, burst-fairness, and query-nick.
* Ordinary WPF startup: passed; the window title was `nexIRC 5`.
* Ordinary WPF graceful shutdown: passed with natural process exit code `0`; no force termination was required.
* Release build: passed with 0 warnings and 0 errors.
* `dotnet format nexIRC5.sln --verify-no-changes --no-restore`: passed.
* Conservative Libera smoke: passed with TLS, registration `001`, self-WHOIS `318`, QUIT, and disconnect.

One aggregate Application run exposed a pre-existing timing-sensitive Phase 1G assertion that observed `Joined` immediately after the outbound PART line. The exact test passed on immediate isolated rerun and the subsequent complete Application run passed; no Phase 1Q production change was made for that unrelated test timing issue.

## 18. Files changed

Production changes are limited to idempotent shutdown ownership, the dispatcher shutdown-fence contract, WPF composition-opportunity instrumentation, safe outbound command-name diagnostics, and the corresponding focused tests. No second authority queue, protocol feature, or broad UI redesign was added.

## 19. Limitations

`CompositionTarget.Rendering` is the strongest application-owned signal available here, but it is only a WPF presentation opportunity. It does not prove physical monitor scan-out, compositor delivery, input hardware latency, or end-to-end human-perceived latency. WPF burst timing is host- and scheduler-dependent; one indivisible action can exceed the cooperative slice budget, as shown by maximum slice duration, while p95/p99 WPF scheduling remains bounded.

The live IRC proof depends on external DNS, network routing, Libera availability, and server policy. It is supplemental to deterministic protocol tests and does not join public channels. The headless trace reports command names rather than raw outbound contents by design.

## 20. Phase 1R recommendation

Retain the Phase 1P cooperative FIFO architecture. Phase 1Q evidence shows that it preserves interactive fairness, ordered convergence, and bounded WPF callback backlog while supporting clean shutdown and a real conservative IRC lifecycle. Phase 1R should focus on any remaining product-visible frame/input evidence, startup/profile restore observability, and broader deterministic lifecycle coverage. A deeper authority/presentation split is not justified unless a future measurement demonstrates a user-facing problem that this cooperative boundary cannot address.
