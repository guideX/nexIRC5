# nexIRC 5 — Phase 21: History Coverage, Infinite Scroll, and Deterministic Pagination

## Scope and starting state

Phase 21 started from `main` at `03a171b` (`Implement nexIRC 5 Phase 20 exact history gap repair`). The starting worktree was clean and `origin/main...HEAD` was `0/0`. No fetch, pull, merge, rebase, push, reset, or unrelated cleanup was performed.

Phase 20 already supplied canonical JSONL history, the rebuildable `.hidx` index, bounded CHATHISTORY recovery, exact gap repair, typed `draft/chathistory-end` recognition, durable query identity, and one active server-history request per session. Phase 21 builds on those contracts.

## Coverage architecture

`HistoryCoverageLedger` is a bounded, runtime-only ledger in the application/session layer. A coverage key is:

`NetworkId + durable conversation/history key`

The durable conversation key is the Phase 1Z key (`Channel:#room`, or the stable private-conversation key), not the current nickname spelling and never a WPF view id. The ledger is capped by conversation and window counts. It stores:

* observed local windows, rather than a falsely continuous oldest/newest interval;
* local-beginning evidence;
* one remote backward frontier;
* the current bounded request identity and generation;
* explicit remote exhaustion;
* conservative no-progress, failure, cancellation, and stale results;
* bounded counters for local pages, remote pages, accepted/deduplicated rows, coalescing, and zero-progress termination.

Coverage is evidence, not inference. A projected row, a short batch, an empty response without an end marker, a timestamp gap, or the start of a JSONL segment does not establish complete remote history.

Canonical JSONL remains authoritative. `.hidx` remains disposable acceleration metadata and can rebuild after restart or corruption. The coverage ledger is intentionally not persisted: request state and server frontiers are generation-scoped runtime evidence and are discarded conservatively on a replacement connection. Durable rows do not need to be downloaded again after restart.

## Local, indexed, and projected history

The durable store, its `.hidx` lookup, and the WPF `Entries` collection are separate layers:

1. JSONL is the canonical persisted record set.
2. `.hidx` provides bounded reverse lookup and random record reads where usable; scan fallback remains safe.
3. `WorkspaceView.Entries` is only the bounded visual projection (`500` rows today).

Opening a historical conversation projects a newest local page. `LoadOlderMessagesAsync` then reads the next bounded page from the local store first (`50` rows), using the durable conversation key and optional network id. It prepends/merges those records canonically through the serialized workspace dispatcher. It does not issue IRC traffic merely because a row is not currently visible.

Local windows are recorded independently, so an old window, an unloaded region, and a recent window remain distinguishable. Equal timestamps use durable sequence/offset tie-breakers where available; msgids are never sorted as numbers or lexical chronology.

## Remote frontier and selector policy

When local history is exhausted and remote retrieval is safe and supported, the application sends one bounded `CHATHISTORY BEFORE` request. The page limit is the smaller of the negotiated server limit, the session client limit, and the Phase 21 remote page budget (`50`). The local projection budget is also `50`; one user action creates at most one remote request.

The request uses the strongest trustworthy negotiated reference:

* `msgid=<opaque id>` when the canonical edge has a server msgid and `MSGREFTYPES` advertises msgid;
* otherwise a server-time timestamp when timestamp references are advertised and the edge has server-time evidence;
* otherwise remote pagination fails closed while local pagination remains available.

The returned page is ordered canonically, exact server-id duplicates are removed, and no-id messages remain distinct. The oldest accepted canonical row becomes the next opaque backward frontier. A page that only duplicates existing history does not advance the frontier. A page with no progress and no explicit end terminates conservatively so it cannot loop.

`draft/chathistory-end` is accepted only from the correlated response batch owned by the same request, network, durable conversation, target, and connection generation. It sets `RemoteExhausted` and suppresses later requests for that runtime generation. A short page alone does not set exhaustion. Empty/no-progress, unsupported, failed, and explicitly exhausted are distinct coverage outcomes.

## Request lifecycle and coalescing

The existing one-active-session CHATHISTORY tracker remains authoritative for reconnect recovery, context retrieval, exact gap repair, TARGETS, and pagination. Phase 21 adds a per-durable-conversation manager task only to coalesce repeated WPF/user requests before they reach that tracker. Ten top-scroll events while a page is active share one task. A completion is re-evaluated against the current coverage rather than blindly requeued.

Generation changes discard the runtime remote frontier and pending request evidence. Stale completions cannot mutate the current view. Closing a conversation still cancels the session request; application shutdown and disconnect continue to use the existing serialized lifecycle.

## Exact gaps and context retrieval

Phase 20 exact gaps remain preferred for known bounded A–B / F–G holes. Pagination does not blindly replace a discovered exact gap with `BEFORE F`; the existing gap ledger and BETWEEN repair remain the precise path. Once the gap converges, ordinary backward pagination uses the newly canonical oldest edge.

Contextual AROUND/BETWEEN retrieval continues through `NetworkSessionManager` and the same session-level one-active request policy. Local context is served from the durable store first. Pagination and context actions do not create competing protocol state machines.

## Query identity and target selection

Local query paging always uses the stable Phase 1Z durable query key. If Alice becomes Alicia under strong account continuity, the same history remains addressable. A conflicting account creates/retains an isolated query and cannot read the old durable conversation.

Remote query paging uses the current safe server target only after the existing identity/recovery rules establish it. A nickname spelling alone is not enough. If no safe current target exists, local history works and remote pagination is deferred/fails closed.

## WPF integration and viewport behavior

The existing conversation `ListBox` remains the transcript control. It now has:

* a bounded `Load older messages` action for deterministic accessibility and keyboard/smoke use;
* top-scroll detection through the existing `ScrollViewer.ScrollChanged` route, armed by upward wheel/key input;
* one active loading operation per durable conversation;
* extent-delta restoration after prepending rows, keeping the prior viewport approximately anchored instead of jumping to the new oldest row.

Historical insertion is a projection-only operation. It does not mark unread/activity, generate notifications/taskbar flashes, mutate current membership/topic/modes/account state, or steal input focus. Live traffic preserves the existing newest-row behavior; the bounded visual window is retained at `500` rows and is not a new virtualized chat control. When historical rows must displace rows at the bound, the older frontier is preserved for the current read operation; canonical rows remain in JSONL and can be reprojected.

## Historical firewall and canonical merge

The existing event-playback firewall remains in force for JOIN, PART, QUIT, NICK, MODE, TOPIC, KICK, AWAY, ACCOUNT, and TAGMSG. Playback rows are transcript rows only. They cannot restore users, rename current users, change current channel state, increment live unread counts, or publish live notifications.

Remote overlap uses `NetworkId + ServerMessageId` exact identity. Repeated no-msgid rows are not deleted by text/timestamp heuristics. Response order is irrelevant; canonical timestamp, durable sequence, and deterministic tie-break ordering controls the transcript.

## Retention, restart, and schema impact

No retention policy was invented or weakened. Existing cleanup remains authoritative; deleted rows are not treated as locally covered and may be fetched again if remote history permits. No JSONL or configuration schema change was required. The runtime ledger is disposable, version-free metadata; it is recreated from canonical/indexed history after restart.

## Budgets and threading

The Phase 21 bounds are:

* local projection page: `50`;
* remote CHATHISTORY page: `50` or the negotiated/session lower limit;
* WPF transcript window: existing `500` rows;
* one remote request per user action;
* one coalesced request per durable conversation;
* bounded ledger conversations/windows.

Disk/index work occurs asynchronously. WPF collection changes use the existing serialized dispatcher and `WorkspaceProjectionBatch`. Remote playback continues through the existing event-drain path. No full JSONL re-scan is introduced for indexed page reads; the store already falls back safely when an index is unavailable.

## Focused tests and smokes

Added focused application coverage tests for:

* separate local windows and local-beginning versus remote exhaustion;
* request coalescing, opaque frontier advancement, deduplication counters, and explicit end;
* network-scoped local pages and bounded reverse-page lookup.

Added desktop semantic smoke scenario `history-pagination`. It creates 300 durable local rows, initially projects 50, loads five older pages with zero CHATHISTORY traffic, reaches the local beginning, sends exactly one bounded BEFORE request, accepts a deliberately older page with `draft/chathistory-end`, and verifies that a repeated request does not send another BEFORE.

## Validation record

The Phase 21 implementation was validated with repository-native commands:

* Core: 83/83 passed.
* Networking: 42/42 passed.
* Application: 150/150 passed plus the one intentional existing benchmark skip, using bounded per-test-class runs. The aggregate Application host was also attempted, but was cancelled after approximately eight minutes without incremental output; no new assertion failure was reported by that invocation.
* Retained Phase 1R–20 coverage: Phase 1R 1/1, 1S 2/2, 1T 2/2, 1U 4/4, 1V 3/3, 1W 8/8, 1Y 2/2, all three 1Z groups 9/9, Phase 20 4/4, and Phase 21 3/3.
* Existing deterministic WPF smokes: 18/18 passed, including chathistory, history-discovery, history-identity, history-gap-repair, fairness/cooperative scheduling, and sustained interactivity.
* New history-pagination trace: local_pages=6 local_rows=300 remote_pages=1 coalesced_requests=10 end_reached=true repeated_before_suppressed=true.
* Release desktop build: clean with zero warnings and errors.
* dotnet format nexIRC5.sln --verify-no-changes --no-restore: passed.
* Credential-free Libera smoke: TLS, CAP, registration, JOIN/NAMES, server-time, WHOIS, natural close, QUIT, and disconnect all passed. Libera did not advertise draft/chathistory; remote pagination was therefore validated through the deterministic fake-server smoke.

The final local validation also confirmed no push or other remote repository operation.

## Outcome and limitations

Outcome A: explicit coverage, local-first backward pagination, integrated armed top-scroll/button loading, bounded CHATHISTORY BEFORE, canonical merge, viewport restoration, and explicit server exhaustion suppression are implemented and green in the deterministic desktop path.

Remaining limitations are deliberate: runtime remote exhaustion is generation-scoped and is not persisted; automatic scroll loading is conservative/armed to avoid accidental requests; full bidirectional infinite scroll and a new virtualized transcript control are out of scope; live Libera validation may not advertise `draft/chathistory`, so remote pagination is validated deterministically through the fake-server harness when that capability is absent.

## Recommended next phase

Use the coverage/frontier substrate for safe forward-page and date/message navigation only after measuring real-world server selector behavior. Keep it bounded and preserve the distinction between canonical history, indexed history, and projected UI rows.
