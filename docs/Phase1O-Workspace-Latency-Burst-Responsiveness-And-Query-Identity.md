# Phase 1O — Workspace latency, burst responsiveness, and query identity

Date: 2026-09-06
Repository: `D:\dev\nexIRC\nexIRC5`
Baseline commit: `44a6c156f9b68094b6edaf2e26f9fde9a21a471e` (`Implement nexIRC 5 Phase 1N serialized state and lifecycle correctness`)

## Baseline and scope

The baseline checkout was clean on `main`, tracking `origin/main`, with zero ahead/behind commits. No fetch, pull, merge, rebase, or push was performed. The initial solution test run reported 50 Core passes, 23 Networking passes, and 96 Application passes with one pre-existing `WorkspaceTests.ChannelProjectionUsesAdaptivePrefixAndReconnectStateDoesNotKeepOldMembers` failure (`expected Adaptive topic`, `actual null`). A rerun after the initial implementation completed 97/97 before the new Phase 1O tests were added; the final normal suite is recorded below.

Phase 1O keeps the single serialized workspace mutation stream. It adds measurement and targeted projection/identity fixes without introducing a second state authority or an unbounded event buffer.

## Dispatcher and WPF latency instrumentation

`SerializedWorkspaceDispatcher` now records a bounded rolling window of 2,048 samples. Each sample captures enqueue, dispatch-start, WPF-dispatch completion, mutation completion, total duration, queue depth, and an action category. Diagnostics expose queue depth, maximum queue and mutation durations, WPF scheduling wait, total duration, and p50/p95/p99 queue-wait and mutation-duration percentiles.

The categories are:

- `IncomingMessage`
- `Membership`
- `ModeOrTopic`
- `Lifecycle`
- `ReadState`
- `UserSelection`
- `OperationFeedback`
- `Notification`
- `HistoryProjection`
- `UiDemoCommand`
- `Other`

`NetworkSessionManager` assigns categories at the semantic-event boundary and uses the same diagnostics for manager-level reporting. WPF scheduling wait is measured inside the existing `WpfWorkspaceDispatcher` callback boundary; mutation timing remains the action body on the WPF dispatcher thread. This distinguishes queue wait from UI-dispatch wait and from actual state mutation time, but it is not a pixel-render timing measurement.

The app-owned manager now drains the bounded raw, parsed, parse-error, semantic, and outbound diagnostic streams. These streams remain bounded and available to direct `ServerSession` consumers, while the desktop/application composition no longer stalls after the 4,096-item channel capacity during a burst.

## Projection audit and optimization

The measured code audit identified two Phase 1N costs:

1. Every channel snapshot rebuilt the complete `Members` projection and raised collection churn even when member order and identity were unchanged.
2. Every incoming event copied the bounded transcript for count-only decisions.

Channel member projection now updates existing rows in place when count/order are unchanged and rebuilds only when membership/order changes. `WorkspaceView.EntryCount` provides count-only access without allocating `EntriesSnapshot`. The transcript remains bounded at `WorkspaceView.MaximumEntries = 500`.

The WPF presentation boundary also coalesces conversation-navigator and command-state refreshes onto one `ContextIdle` callback. This removes a full navigator `Clear`/re-add cycle from every incoming message while keeping authoritative state and transcript mutations FIFO. Protocol mutations are scheduled at WPF `Background` priority; carefully classified user-intent work uses `Input` priority, while the serialized predecessor chain still prevents protocol actions from overtaking one another.

## Deterministic burst matrix

The opt-in test is `Phase1OBenchmarkTests.DeterministicBurstMatrixReportsResponsivenessAndOrdering`. It is skipped in the normal fast suite. Run it separately with:

```powershell
$env:NEXIRC_RUN_PHASE1O_PERFORMANCE = '1'
dotnet test tests/nexIRC.Application.Tests/nexIRC.Application.Tests.csproj --no-restore --filter FullyQualifiedName~Phase1OBenchmarkTests --logger "console;verbosity=detailed"
```

Each row uses an immediate application dispatcher for deterministic workspace timing, verifies FIFO tail ordering and the 500-entry bound, and separately queues a `UserSelection` action behind incoming-message work on a yielding dispatcher. Values below are the observed run on 2026-09-06; wall time and scheduler values are environment-dependent.

| Events | Networks | Conversations | Wall ms | Max queue | Max wait ms | p50 wait ms | p95 wait ms | p99 wait ms | Max mutation ms | User selection queue wait ms | Ordered |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|
| 100 | 1 | 1 | 26.864 | 1 | 3.657 | 0.000 | 0.002 | 0.015 | 9.605 | 39.154 | true |
| 100 | 1 | 32 | 18.816 | 1 | 0.001 | 0.000 | 0.001 | 0.001 | 0.061 | 39.188 | true |
| 100 | 2 | 1 | 16.601 | 1 | 0.074 | 0.000 | 0.028 | 0.068 | 0.057 | 39.079 | true |
| 100 | 2 | 32 | 17.452 | 1 | 0.074 | 0.029 | 0.048 | 0.072 | 0.063 | 39.251 | true |
| 1,000 | 1 | 1 | 47.543 | 1 | 0.002 | 0.000 | 0.001 | 0.001 | 3.797 | 386.672 | true |
| 1,000 | 1 | 32 | 78.993 | 1 | 0.002 | 0.000 | 0.001 | 0.001 | 1.044 | 377.970 | true |
| 1,000 | 2 | 1 | 31.388 | 1 | 0.127 | 0.000 | 0.025 | 0.042 | 1.793 | 382.865 | true |
| 1,000 | 2 | 32 | 65.868 | 1 | 3.785 | 0.036 | 0.059 | 0.112 | 3.780 | 382.092 | true |
| 5,000 | 1 | 1 | 65.296 | 1 | 0.002 | 0.000 | 0.000 | 0.000 | 0.869 | 1,926.846 | true |
| 5,000 | 1 | 32 | 102.970 | 1 | 0.006 | 0.000 | 0.000 | 0.000 | 2.695 | 1,898.416 | true |
| 5,000 | 2 | 1 | 31.801 | 1 | 0.062 | 0.000 | 0.003 | 0.004 | 1.643 | 2,006.010 | true |
| 5,000 | 2 | 32 | 138.105 | 1 | 9.152 | 0.005 | 0.024 | 0.052 | 8.177 | 1,957.541 | true |

The high queued user-selection wait is intentional evidence of FIFO behavior under synthetic pressure, not a claim that the WPF UI rendered for that duration. It provides the baseline needed before considering further scheduler work. The manager-side benchmark uses an immediate dispatcher, so its WPF scheduling wait is effectively zero; the WPF smoke reports the real WPF scheduling sample separately.

The 2,000-event WPF smoke provided the before/after decision point. Before the WPF priority and navigator-refresh changes, the observed selection action was 0.769 ms with p95 application queue wait 8,286.263 ms. In the latest final run after the Phase 1O changes, selection was 0.314 ms, p95 application queue wait 7,513.896 ms, p95 WPF scheduling wait 8.917 ms, and p95 mutation duration 1.595 ms. The result justifies bounded WPF priority/coalescing for interaction responsiveness; it does not claim that all protocol backlog latency disappeared.

## Phase 1O outcomes

Dispatcher outcome B: keep the serialized FIFO mutation stream, classify work, schedule protocol mutations at WPF `Background` priority, schedule user-intent work at `Input` priority, and coalesce navigator presentation refreshes. The measurements justify this cooperative presentation-boundary change while preserving protocol ordering; they do not justify priority reordering inside the authoritative state stream.

Query identity outcome C: use a stable internal `QueryView.Id` and immutable `HistoryConversationKey` as the logical identity, with a mutable display nickname. A direct NICK may update that display identity only within the active connection generation and only when collision-free. Reconnects, network scope, and collision checks remain hard identity boundaries.

## Query identity and NICK semantics

The workspace treats a query object and its stable history key as the logical conversation identity. A direct NICK event follows an existing query only when all of the following hold:

- the old nickname matches under the active IRC case mapping;
- the query is bound to the current connection generation;
- the new folded nickname does not collide with another existing query.

When it follows, the same `QueryView.Id`, draft key, unread/important counters, activity state, navigation identity, and log path are retained. The visible title/nickname changes, and a query transcript NICK row is appended without creating an extra user notification or changing activity. Configuration favorites and recents are renamed in place when safe; collisions leave both destinations intact.

After reconnect, prior direct-NICK evidence is deliberately not reused for a new inbound message. An inbound message from a reused nickname therefore opens a new query object, preserving the old transcript and draft boundary. The same rules are scoped by network, so identical nicknames on different networks never merge. Account-tag data remains participant metadata and is not used as a general cross-network identity key.

History records now carry a stable conversation key. Current-query search, paging, export, JSONL paths, and the in-memory store accept that key so records written before and after a nickname transition remain one conversation. A historical search result for a private conversation reuses the matching open query by key when available.

## Focused tests and UI smoke

`Phase1OTests` covers:

- direct NICK follow with stable activity and history identity;
- RFC1459 case mapping (`Alex{`/`Alex[`);
- collision-safe non-merge and preserved important state;
- nickname reuse after the transition;
- network-local behavior and close/reopen identity;
- reconnect identity boundary;
- bounded dispatcher samples, categories, and percentiles;
- mixed protocol activity ordering and coarse action categories.

The desktop smoke harness adds:

```powershell
dotnet run --project src/nexIRC.Desktop -c Release --no-build -- --demo --ui-smoke burst
dotnet run --project src/nexIRC.Desktop -c Release --no-build -- --demo --ui-smoke query-nick
```

`burst` injects 2,000 messages, verifies FIFO tail retention, unread/read transitions, and reports `BURST_UI_METRICS` including selection latency, application queue wait, WPF scheduling wait, and mutation percentiles. `query-nick` verifies same-view identity, stable key, transcript presentation, participant reopen, and draft restoration, and reports `QUERY_NICK_UI_METRICS`.

The conservative live smoke used the headless harness against `irc.libera.chat:6697` with TLS, registration, temporary nickname `nexirc5p1o`, self-WHOIS through numeric 318, and clean disconnect. No channel was joined and no flood was sent.

## Verification record

- `dotnet build nexIRC5.sln --no-restore --verbosity minimal`: passed, 0 warnings, 0 errors.
- Normal solution suite: Core 50 passed; Networking 23 passed; Application 105 passed and 1 opt-in performance test skipped; 179 total (178 passed, 1 skipped).
- Opt-in deterministic matrix: 1 passed; all 12 matrix rows reported `ordered=True`.
- Release build: passed, 0 warnings, 0 errors.
- `dotnet format nexIRC5.sln --verify-no-changes --no-restore`: passed.
- Deterministic WPF smoke: all existing scenarios passed, plus `burst` and `query-nick`; the latest burst reported `selection_ms=0.314`, `max_queue_depth=1993`, `p95_queue_wait_ms=7513.896`, `p95_wpf_schedule_wait_ms=8.917`, `p95_mutation_ms=1.595`, `tail_ordered=true`.
- Ordinary WPF smoke: started `nexIRC 5`, close was requested, exit code 0, no forced termination.
- Live Libera smoke: TLS registration reached 001, self-WHOIS reached 318, and clean disconnect completed with exit code 0.

## Limitations

The timings are host- and scheduler-dependent samples, not service-level guarantees. The deterministic application matrix uses an immediate dispatcher for repeatable state measurements; its WPF scheduling wait is therefore not representative of desktop rendering. The desktop smoke measures dispatcher/projection completion, not pixels or end-to-end input hardware latency. Nickname continuity is proven only by an in-session direct NICK event; account tags remain metadata, and reconnect or nickname reuse deliberately starts a new identity boundary.

## Phase 1P recommendation

Keep the single FIFO authoritative mutation stream and the bounded telemetry. If real-world WPF p95 queue wait remains materially high after the coalesced presentation refresh, evaluate bounded cooperative batching/yielding at explicit protocol batch boundaries, with the same ordering and generation-fencing assertions. Do not add a general priority scheduler or history migration unless a new measured workload demonstrates that this presentation-boundary change is insufficient.
