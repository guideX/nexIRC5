# nexIRC 5 Phase 1N — Serialized State, Lifecycle, and Read/Activity Correctness

## Starting state

Phase 1N started from `47cec7a1112b63e9502d344c2d6f77e6fd7a5c7b` on `main`, the
Phase 1M channel-authority and desktop-smoke commit. The worktree was clean and
matched its upstream at the start. No fetch, pull, merge, rebase, or push was
used.

The Phase 1M audit found a useful existing boundary: production WPF callbacks
already used `WpfWorkspaceDispatcher`, and protocol state was owned by each
`ServerSession`/`SessionStateStore`. The application projection callbacks,
however, were immediately dispatched and the test dispatcher could execute
callbacks on arbitrary producer threads. There was no explicit FIFO state
queue, no projection-level diagnostic view of stale generations, and activity
was represented only by one severity enum. Those were the defects hardened in
this phase. History storage, command routing, and the Phase 1M channel authority
model were otherwise retained.

## Serialization boundary and threading model

The application now wraps the supplied `IWorkspaceDispatcher` in one
`SerializedWorkspaceDispatcher`. It accepts state actions in submission order,
chains them FIFO, records queue high-water data, and stops accepting work during
shutdown. In the desktop process the wrapped dispatcher is
`WpfWorkspaceDispatcher`, so the ordered projection action and its WPF property
and collection notifications run on the WPF dispatcher thread. In application
tests the inner dispatcher remains immediate, but actions are still serialized
and projection collections provide snapshot enumeration for concurrent
diagnostic/test readers.

The intended path is:

```text
transport receipt -> ServerSession typed semantic event
                  -> NetworkSessionManager FIFO dispatch
                  -> workspace/channel/query projection
                  -> WPF property/collection notification
```

Socket work and history I/O do not run on the state dispatcher. User actions
already enter from the WPF UI thread; therefore they share the same WPF thread
as queued protocol projections. The manager's synchronous user-facing APIs were
not converted into an actor API, avoiding a larger architectural rewrite.

`ThreadSafeObservableCollection<T>` does not replace the WPF boundary. Its
purpose is to make snapshots and mutators safe for the application test and
diagnostic readers while production collection notifications are still emitted
from the WPF-owned dispatcher.

## Ordering and generation fencing

For accepted callbacks, the serialized manager observes the order in which the
callbacks are submitted. A projection appends a monotonically increasing
`TranscriptEntry.Sequence` token and navigation recent-activity ordering uses
`(LastActivity, LastActivitySequence, network, name)`. Equal wall-clock times
therefore cannot reorder causally ordered activity.

Every state or semantic callback is checked against:

* the current `SessionEntry` session reference;
* the workspace's current session reference;
* the session snapshot's exact connection generation; and
* the workspace snapshot not being newer than the callback generation.

`ServerSession` also fences transport callbacks at its connection epoch before
publishing them. A late old-session callback, stale generation-1 MODE/JOIN/
message, or queued event after replacement is discarded and cannot mutate the
new projection. Application-level stale-generation discards are counted. No
global persistent deduplication store was introduced.

Semantic events carrying IRCv3 `msgid` are deduplicated per session generation
and semantic event type with a bounded 512-entry window. Events without a
`msgid` remain distinct even when their text is identical. This preserves valid
repeated IRC messages while defending against accidental duplicate delivery.

## Reconnect and resynchronization

The existing session supervisor remains responsible for reconnecting. The
application projection sequence is now explicitly:

1. old generation disconnects and pending operations are invalidated;
2. the session enters reconnect/connecting states;
3. a new session generation registers;
4. desired channels are replayed;
5. channel synchronization reconciles JOIN/NAMES/WHO/TOPIC/MODE state;
6. live events are then allowed to contribute normal activity.

JOIN replay, NAMES, WHO, topic numerics, and channel-mode numerics are treated
as structural synchronization. They update authoritative membership, topic,
and mode state, but do not update live activity, unread/highlight counters,
recents, or desktop notifications. The suppression is bounded to the semantic
event being projected; it is not a blanket suppression of ordinary post-sync
chat.

## Conversation identity and lifecycle

Channel and query views remain scoped to their containing `NetworkWorkspace`.
The same `#general` or nickname on AlphaNet and BetaNet cannot share a view,
draft, favorite, transcript, operation, or activity state.

Channel lifecycle is represented by the existing joined/stale/synchronization
properties plus the explicit `ConversationLifecycleState` values:

* `HistoricalOnly` — retained history without a live join;
* `Joining` — desired join/synchronization is in progress;
* `Joined`/`Active` — server state says the channel is joined;
* `Parted` — the user has parted but the logical view/history remains;
* `Kicked` — server self-kick state;
* `Disconnected` — the view is retained while its network/session is down.

Closing a channel or query is a presentation operation. It does not send PART,
remove the logical conversation, or discard its transcript. `PartAndCloseAsync`
is the explicit operation that sends PART, removes desired-channel intent, marks
the channel parted, and closes the view. Historical reopening never sends JOIN.
`RejoinChannelAsync` is the explicit network action that restores desired intent
and sends JOIN when the registered session can send it. Favorites and retained
history use the network-qualified conversation identity throughout these
transitions.

Queries retain their existing behavior: the logical query nickname is immutable
for the view. The current protocol projection does not rename a query view when
an IRC NICK event is received; a later message addressed to the new nickname can
resolve as a separate query. This matches the pre-existing query identity and
protocol-state tests and avoids silently changing history or drafts. A query
opened from WHOIS or a participant action is still scoped to the originating
network.

## Read, unread, highlight, and self-message semantics

Selecting/activating a view is the read boundary. Activation marks the view read
and resets its unread, important, and highlight counters. The app does not model
an additional background-window focus state; historical inspection and auxiliary
views such as WHOIS, Ban List, and Channel Properties do not mark an unrelated
conversation read.

Background live activity increments bounded counters (maximum 10,000):

* `UnreadCount` counts all unread live activity;
* `ImportantCount` counts unread important activity;
* `HighlightCount` counts unread channel mentions;
* `Activity` exposes `None`, `Unread`, or `Important` as the projection summary.

Normal channel messages are unread, channel mentions are important/highlighted,
private messages and notices are important, and server/unknown errors remain
important. Selecting the conversation clears all three counters together,
which matches the existing single-activity shell behavior. Counters never go
negative and saturate at the configured bound.

Messages from the current nickname, including server echoes and CTCP/query
representations, are presented but do not create unread or highlight activity.
Local outgoing entries remain local transcript entries and do not create their
own unread state. Full IRCv3 `echo-message` negotiation was not added in this
phase.

Highlight matching uses the network-advertised CASEMAPPING through the existing
`IrcCaseMappingComparer`, including RFC1459 special-equivalence behavior where
advertised. It is never applied globally across network workspaces.

## History, recents, favorites, and drafts

History loading, JSONL paging, search context, export, retention, and archival
inspection remain observational. They append/display historical data without
changing live unread/highlight state, notifications, or last live activity.
Resynchronization likewise does not move recents. Recents are updated for
genuine live channel join activity, the first live message that opens a query
identity, explicit PART/rejoin actions, and the existing explicit user close
path. WHOIS, Ban List, Channel Properties, history/search, projection refresh,
and reconnect bookkeeping do not qualify as live activity.

Favorites are configuration entries scoped by profile/network and are not
reordered or deleted by reconnect, close/reopen, PART/KICK, history inspection,
or member/nickname churn. Drafts remain in-memory and keyed by
`(networkId, viewId)`, retain the existing 4,096-character cap, and are not
written to JSONL, search indexes, or exports. Protocol callbacks do not overwrite
composer text.

## Ignore and operation feedback

Ignore matching now suppresses presentation, activity, and notifications for
ignored PRIVMSG/query/CTCP and user-originated JOIN, PART, QUIT, NICK, KICK,
TOPIC, and MODE events. The underlying `SessionStateStore` still reconciles
membership, nick, topic, mode, and kick structure before the presentation layer
filters the event.

Operation failures remain visible through operation feedback and status/error
transcript paths. They do not turn an ordinary channel into an unread channel
merely because the user attempted an invalid channel action. Existing operation
generation and network scoping remain authoritative, including self-KICK and
PART confirmation behavior.

## Notifications and shutdown

Manager-produced protocol notifications carry a semantic identity. The bounded
notification coalescer keys by network, view, notification type, and semantic
identity, so two distinct same-text messages are not collapsed merely because
they are close together. Direct legacy notifications without an identity retain
the prior short coalescing behavior. Resynchronization projections do not publish
notifications, preventing reconnect storms.

Shutdown marks the manager disposed, removes session handlers, stops accepting
new serialized work, waits for pending manager dispatch tasks, drains the FIFO,
and then disposes owned notification/log services. Queued callbacks check the
disposed flag and cannot repopulate removed workspaces. Existing async operation
cancellation and session-disposal paths remain in force. Locks protect only
independent dictionaries, counters, or collection snapshots; no lock is held
across an await or WPF dispatcher call.

## Deterministic tests and diagnostics

`Phase1NTests` covers:

* FIFO dispatch order and shutdown rejection;
* structural-only resynchronization followed by bounded live read/highlight
  accounting;
* close-versus-PART and explicit rejoin lifecycle;
* self-KICK retention as a distinct lifecycle state until explicit rejoin;
* `msgid` duplicate suppression while distinct same-text messages survive;
* ignored structural events updating membership without presentation/activity;
* a deterministic 2,000-message live burst with ordered retained transcript
  tail and no lost unread accounting.

The transcript retains its existing bounded 500-entry projection window; the
burst test checks all 2,000 unread increments and the ordered final 500 entries.
Diagnostic counters are bounded in memory: the semantic dedup window is 512
keys, transcript projections retain 500 entries, history context retains its
existing configured limit, and unread counters saturate at 10,000. Exposed
`NetworkSessionDiagnostics` reports queued/processed actions, current and
maximum queue depth, stale generations discarded, duplicate semantic events,
and resynchronization events suppressed.

## Desktop smoke coverage

All smoke runs use `DemoScenario` fake transports and real view-model/service
paths; they do not open real sockets. They use bounded awaited conditions and
close the WPF window before returning. Existing Phase 1M scenarios remain:

```text
--demo --ui-smoke participant
--demo --ui-smoke moderation
--demo --ui-smoke channel-properties
--demo --ui-smoke multi-network
```

Phase 1N adds:

```text
--demo --ui-smoke lifecycle
--demo --ui-smoke read-state
--demo --ui-smoke reconnect
```

The lifecycle path checks close without PART, live delivery to the retained
object, same-object reopen, explicit PART, historical reopen without JOIN, and
explicit rejoin/synchronization. The read-state path checks ordinary unread,
highlight/important accumulation, selection clearing, history observational
behavior, and ignored structural/content handling. The reconnect path uses a
replacement fake transport, generation-1 disconnect, valid generation-2
resynchronization, a stale old callback attempt, and a valid new-generation
MODE. Failure paths emit `FAIL_UI_SMOKE` and set a nonzero process exit code;
success emits the stable `PASS_UI_SMOKE <scenario>` marker.

## Validation and limitations

The completed validation was: Core 50/50, Networking 23/23, Application
97/97, aggregate 170/170; Release build with zero warnings/errors; and
`dotnet format nexIRC5.sln --verify-no-changes --no-restore` clean. All seven
offline UI smoke scenarios passed (the four Phase 1M scenarios plus lifecycle,
read-state, and reconnect). A bounded ordinary Release `--demo` launch stayed
alive for the observation window. Conservative live Libera.Chat validation
observed TLS connection, registration/001, self-WHOIS/318, and clean
disconnect; it joined no public channel and sent no message flood.

This phase does not add a full actor framework, full IRCv3 echo-message support,
database storage, history redesign, scripting/plugins, or theme/command
expansion. Application tests using `ImmediateWorkspaceDispatcher` verify FIFO
ordering and snapshot safety, while only the desktop WPF smoke proves the
production dispatcher crossing. The app still intentionally models selection as
the read boundary rather than separately tracking OS-window activation. Query
nickname-following remains the existing immutable-view behavior described above.

## Phase 1O recommendation

Keep Phase 1O focused on measured gaps: instrument any real-world WPF queue
latency observed in use, decide whether query identity should follow NICK based on
product requirements, and add only the next protocol or history capability that
has a concrete acceptance test. Preserve the current generation fence,
resynchronization suppression, and deterministic smoke suite as non-regression
contracts.
