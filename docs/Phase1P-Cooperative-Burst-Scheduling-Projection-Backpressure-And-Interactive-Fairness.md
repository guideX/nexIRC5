# Phase 1P — Cooperative burst scheduling, projection backpressure, and interactive fairness

Date: 2026-09-06
Repository: `D:\dev\nexIRC\nexIRC5`
Branch: `main`

## 1. Starting repository state

The starting worktree was clean at `1a27a26fa04b0c4441804e905f2b58c47551c7fa`, subject `Implement nexIRC 5 Phase 1O workspace latency and query identity`. No fetch, pull, merge, rebase, or push was performed. The live comparison was `0 ahead / 0 behind` against `origin/main`; this differs from the requested expected `1 ahead / 0 behind` and was preserved rather than synchronized.

## 2. Phase 1O evidence and audit

Phase 1O reported a large application/UI queue during the 2,000-message WPF smoke: approximately 1,993 queued state actions, multi-second application queue p95, and low actual selection execution time. Its final representative measurements were selection execution `0.314 ms`, application queue p95 `7,513.896 ms`, WPF scheduling p95 `8.917 ms`, and mutation p95 `1.595 ms`.

The audited path is:

```text
ServerSession reader
  -> SessionSemanticEvent / StateChanged callback
  -> NetworkSessionManager.Dispatch (one FIFO action per accepted event)
  -> SerializedWorkspaceDispatcher
  -> WpfWorkspaceDispatcher (Background for protocol-derived work)
  -> ApplySnapshot + RouteSemanticEvent + AppendRendered
  -> ObservableCollection/property notifications
  -> MainWindowViewModel.OnNavigationChanged
  -> one ContextIdle navigator/command refresh
```

The audit found:

* `NetworkSessionManager` raises model/property/collection changes from `NetworkWorkspace.ApplySnapshot`, `ChannelView.ApplySnapshot`, `WorkspaceView.Append`, activity counters, lifecycle updates, member updates, and query identity updates.
* One semantic event previously produced one WPF dispatcher callback. That callback combined authoritative workspace mutation and presentation notification work.
* `AppendRendered` requests a navigation refresh for every rendered event. The desktop view-model already coalesced those requests with `_navigationRefreshPending`, so the navigator was not rebuilt once per message in Phase 1O; Phase 1P adds request/execution/coalescing counts to prove this.
* Transcript appends and bounded-tail removals are legitimate per-event projection changes. Unchanged member order already uses in-place member updates from Phase 1O; no new member rebuild was introduced.
* Protocol-derived callbacks use WPF `Background`; user-intent categories (`UserSelection`, `ReadState`, operation feedback, and demo commands) retain `Input`. Priority is not used to reorder the serialized authority stream.

The root cause is therefore a combination of A, C, and E: legitimate event-by-event transcript/state work, excessive one-callback-per-action overhead, and collection notification churn. B was already addressed for navigator refreshes in Phase 1O. F was already classified correctly and was not the principal remaining cause. There was no evidence supporting a second authority or out-of-order priority scheduler.

## 3. Selected outcome and architecture

Phase 1P is Outcome D (mixed, measured solution): bounded cooperative draining plus safe projection-notification coalescing, while retaining the existing priority policy and Phase 1O navigator coalescing.

Before Phase 1P, the serialized dispatcher chained one task and one UI dispatch operation per action. After Phase 1P it remains one serialized FIFO queue, but drains up to 32 accepted actions in one presentation callback and yields after either 32 actions or an approximately 4 ms slice budget. A new UI callback is then posted, giving WPF `Input` work a scheduling opportunity between protocol-derived `Background` slices.

This is batching of execution and binding invalidations, not batching of IRC meaning. Every accepted action remains an individual queued item, is executed in submission order, receives its own completion and telemetry sample, and remains visible to the existing generation and duplicate-event checks.

`ThreadSafeObservableCollection<T>` now supports a `WorkspaceProjectionBatch` scope. Values are changed immediately. During a slice, equivalent binding notifications are deferred:

* add-only changes emit one range `Add` notification;
* remove-only changes emit one range `Remove` notification;
* structural changes and mixed bounded transcript trim (add plus remove) emit one `Reset`, because WPF `ItemsControl` generators did not reliably accept a synthetic mixed range sequence;
* semantic transcript entries are never removed from the authoritative sequence. The collection still contains the exact bounded 500-entry tail.

`MainWindowViewModel` continues to coalesce equivalent “navigator needs refresh” requests into one `ContextIdle` callback. The latest authoritative model is read when that callback runs. Requests, executions, and coalesced requests are now bounded counters used by the smoke diagnostics.

No lifecycle, generation, query identity, transcript, member, or notification semantic event is coalesced.

## 4. Backpressure and priority semantics

Never-coalesced authoritative semantics include ordered transcript messages, JOIN/PART/QUIT/KICK/NICK, mode/topic changes, lifecycle transitions, reconnect/generation boundaries, and query identity transitions. The dispatcher does not drop or merge these actions.

Coalesced work is limited to derived presentation invalidations: collection binding notifications within one already-ordered slice and repeated navigator/command refresh requests. The WPF boundary reports `WorkItemsSuperseded = 0`; coalescing is observable separately as navigator refresh requests that did not create another pending callback.

Protocol-derived state work remains `DispatcherPriority.Background`. Input/read-state/feedback work remains `DispatcherPriority.Input`. The dispatcher does not promote a later protocol event over an earlier protocol event. Selection is injected at the WPF boundary in the burst smoke and runs between bounded callbacks when a slice is still pending; it does not bypass or mutate the IRC event ordering.

## 5. Bounded telemetry

The existing 2,048-sample rolling window remains bounded. Each action sample still distinguishes enqueue-to-dispatch queue wait, dispatch-to-WPF execution wait, mutation duration, total action duration, queue depth, and category.

Phase 1P adds:

* cooperative slice count and yield count;
* maximum actions per slice and maximum measured slice duration;
* actual WPF callbacks posted/executed, current pending WPF callbacks, and maximum pending WPF callbacks;
* navigator refresh requests, executions, and coalesced requests;
* interaction WPF scheduling wait and execution duration in the deterministic burst smoke.

These values measure dispatcher scheduling and projection completion. They do not measure physical pixel presentation, input hardware latency, or compositor frame delivery.

## 6. Measurements

The following are representative Release `--demo --ui-smoke burst` values after Phase 1P on this host; scheduler and WPF timings vary by run.

| Metric | Phase 1O baseline | Phase 1P representative |
|---|---:|---:|
| Burst events | 2,000 | 2,000 |
| Application/state queue maximum | ~1,993 | 1,943 |
| Actual WPF callbacks / posts | not separately measured | 106 / 100 |
| Maximum pending WPF callbacks | not separately measured; Phase 1O depth was state actions | 1 |
| Selection timing | synchronous execution 0.314 ms | WPF wait 30.200 ms; execution 2.913 ms |
| WPF scheduling p50 / p95 / p99 | p95 8.917 ms | 1.796 / 3.788 / 3.963 ms |
| Application queue p50 / p95 / p99 | p95 7,513.896 ms | 1,875.631 / 3,319.117 / 3,384.159 ms |
| Mutation p95 | 1.595 ms | 4.443 ms |
| Cooperative slices / yields | not measured | 106 / 97 |
| Maximum slice actions / duration | not measured | 31 / 18.759 ms |
| Navigator requests / executions / coalesced | coalesced, not counted | 2,048 / 20 / 2,037 |

The Phase 1O “1,993” value was the serialized application-action depth, not a direct count of simultaneously pending WPF dispatcher operations. Phase 1P is the first run that reports actual WPF callback pending depth, so a literal before/after WPF-pending comparison is unavailable. The comparable result is that a 2,000-action burst produced roughly 100 WPF callbacks rather than one callback per action, with p95 WPF scheduling remaining below 4 ms in the representative run. The one indivisible action and notification flush can exceed the nominal 4 ms cooperative budget; this is why maximum slice duration is reported rather than treated as a hard deadline.

## 7. Performance matrix and stronger burst workload

The opt-in Phase 1O matrix was retained at 100, 1,000, and 5,000 events; 1 and 2 networks; and 1 and 32 conversations. All 12 rows completed with `ordered=True` after the dispatcher change. The latest run reported:

| Events | Networks | Conversations | Wall ms | Max queue | p95 queue wait ms | p99 queue wait ms | Max mutation ms | Ordered |
|---:|---:|---:|---:|---:|---:|---:|---:|:---:|
| 100 | 1 | 1 | 23.908 | 1 | 0.007 | 0.046 | 15.285 | true |
| 100 | 1 | 32 | 14.930 | 1 | 0.003 | 0.009 | 0.148 | true |
| 100 | 2 | 1 | 15.220 | 54 | 0.760 | 0.812 | 4.445 | true |
| 100 | 2 | 32 | 16.456 | 90 | 4.268 | 4.314 | 0.241 | true |
| 1,000 | 1 | 1 | 62.264 | 1 | 0.002 | 0.003 | 2.486 | true |
| 1,000 | 1 | 32 | 124.579 | 1 | 0.002 | 0.003 | 3.522 | true |
| 1,000 | 2 | 1 | 30.905 | 516 | 10.140 | 10.642 | 5.859 | true |
| 1,000 | 2 | 32 | 77.499 | 826 | 54.127 | 54.704 | 8.310 | true |
| 5,000 | 1 | 1 | 142.465 | 1 | 0.001 | 0.002 | 2.188 | true |
| 5,000 | 1 | 32 | 311.277 | 1 | 0.002 | 0.002 | 2.645 | true |
| 5,000 | 2 | 1 | 237.501 | 3,463 | 126.663 | 128.013 | 40.849 | true |
| 5,000 | 2 | 32 | 371.773 | 4,740 | 241.393 | 245.027 | 44.980 | true |

The two-network rows expose producer concurrency and remain ordered per the single authority. Their queue depth is diagnostic pressure, not evidence of a second mutation stream. The application benchmark’s synthetic `UserSelection` action is intentionally queued behind protocol actions to prove FIFO and is not the UI-fairness measurement.

The existing 2,000-event `burst` smoke now injects a real WPF `Input`-priority selection while the burst is draining. A stronger `burst-fairness` smoke uses 5,000 deterministic PRIVMSG events, injects the same selection during the burst, and verifies the bounded tail, order, unread/read transitions, and final state.

## 8. Correctness and regression results

Focused Phase 1P tests cover FIFO order across multiple cooperative slices, bounded slice size/yields, sequence-barrier `FlushAsync` behavior, and collection-notification coalescing. Mixed trim is explicitly tested as a safe `Reset` while retaining every final value.

The existing Phase 1N/1O suites remain in the aggregate run. They continue to prove generation fencing, lifecycle/disconnect/reconnect behavior, duplicate handling, transcript order and 500-entry tail retention, member ordering/projection, activity/read state, and all Phase 1O query-NICK cases:

* stable query ID and history key across direct NICK;
* draft, unread, highlight, navigation, and history continuity;
* no NICK notification/activity change;
* collision-safe non-merge and new-query nickname reuse;
* network isolation and reconnect identity boundary.

The generation check remains in `NetworkSessionManager.IsCurrentGeneration` before any semantic route. A queued old-generation action is discarded and counted; a later WPF slice cannot resurrect it. Closing/removing a workspace remains protected by disposal and `TryGet` checks.

## 9. UI smoke and live IRC

All deterministic fake-transport UI smoke scenarios passed: participant, moderation, channel-properties, multi-network, lifecycle, read-state, reconnect, burst, burst-fairness, and query-nick. The burst scenarios verified startup, mid-burst selection, ordered bounded transcript tail, member/activity/navigator convergence, no stale work, and clean smoke shutdown. The query-NICK smoke reported `same_view=true history_key_stable=true draft_restored=true query_count=1`.

The ordinary Release WPF executable launched with title `nexIRC 5` and was responsive at startup. The host’s native computer-use surface exposed browser controls only, so no native WPF close action was available through that tool. A normal process `CloseMainWindow()` request did not exit within the allotted graceful-close window and the process had to be force-stopped. This is classified as an ordinary-startup/shutdown validation limitation, not a successful clean-shutdown proof; the deterministic smoke close path remains clean. No Phase 1P-specific WPF exception was observed in the smoke suite.

The established conservative Libera path was attempted separately with TLS, temporary nickname, registration, self-WHOIS through numeric 318, and clean disconnect; no channel was joined and no flood was sent. The Phase 1P attempt reached `TlsNegotiation` and then failed before registration, so `001`, `318`, and clean disconnect were not observed in this run. The prior Phase 1O run reached 001/318 and clean disconnect; the Phase 1P live result is classified separately as a TLS/network failure and is supplemental to deterministic correctness.

## 10. Validation record

The final validation record is reported with exact counts in the Phase 1P completion report. The required gates are: Core, Networking, Application, focused Phase 1P tests, retained opt-in matrix, all deterministic UI smoke scenarios, Release build, `dotnet format nexIRC5.sln --verify-no-changes --no-restore`, and startup/shutdown checks. The ordinary aggregate suite continues to skip only the opt-in Phase 1O performance fact when its environment variable is not set.

## 11. Limitations and Phase 1Q recommendation

Timings are host- and scheduler-dependent. The 4 ms slice budget is cooperative and cannot interrupt one indivisible WPF/model action. Actual pixel presentation and frame latency are not measured. WPF range notification behavior requires conservative reset handling for mixed transcript trim. The current design still performs authoritative model updates on the serialized UI-owned boundary; Phase 1P reduces callback and binding overhead without creating a second state authority.

Phase 1Q should first add a first-class presentation queue/authority split only if a future workload shows that single-event model actions, rather than callback/notification overhead, still dominate. Any such split should preserve the current event fence, transcript tail, query identity, and one FIFO authoritative stream. Otherwise, retain this bounded cooperative boundary and focus on measuring real frame/input latency before adding further scheduler complexity.
