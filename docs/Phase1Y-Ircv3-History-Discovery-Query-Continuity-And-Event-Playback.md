# nexIRC 5 Phase 1Y — IRCv3 History Discovery, Query Continuity, and Event Playback

Phase 1Y extends the Phase 1X bounded CHATHISTORY path with TARGETS discovery,
conservative private-query continuity, AROUND/BETWEEN context retrieval, and a
historical-only projector for the draft event-playback capability.

The governing rule is unchanged:

> Historical information may enrich a conversation timeline, but it must never
> masquerade as current session state.

## 1. Audit of the inherited Phase 1X architecture

Phase 1X already had the smallest useful protocol boundary:

* `ChathistoryRequest` carries network and connection-generation ownership.
* `ChathistoryCommandBuilder` is the only CHATHISTORY wire serializer.
* `ServerSession` owns one bounded active request, timeout/cancellation, batch
  fencing, disconnect completion, and stale-generation rejection.
* `SessionStateStore` interprets IRC messages and owns current channel/query
  state.
* validated `chathistory` children were already converted to
  `ServerPlayback` message events without touching member state.
* `NetworkSessionManager` routes typed semantic events through the serialized
  workspace dispatcher and the shared history merge/logging path.
* `WorkspaceView.AppendConversationCandidate` and
  `ConversationHistoryMerge` provide canonical timestamp/sequence ordering,
  network-scoped server-id deduplication, and no-id preservation.

The audit found two deliberate Phase 1X extension points:

1. AROUND and BETWEEN were already named in the request enum, but the builder
   rejected them.
2. State-changing children inside a history batch were intentionally dropped.
   That was the correct safety default, but it left no typed historical
   projector for `draft/event-playback`.

TARGETS does not fit a conversation request because it has no target buffer.
The request model therefore keeps one lifecycle tracker but represents TARGETS
with a null `Target`/`Conversation`, two timestamp selectors, and operation-
specific validation. Content requests retain a normal conversation target.

## 2. Draft/specification boundary

This implementation follows the current IRCv3 working specification:

* capability names are exactly `draft/chathistory` and
  `draft/event-playback`;
* the content batch type is the ratified `chathistory` type;
* TARGETS uses `draft/chathistory-targets`;
* selectors are serialized as `timestamp=YYYY-MM-DDThh:mm:ss.sssZ` or
  `msgid=<value>`;
* TARGETS accepts two concrete timestamp selectors only;
* AROUND accepts one timestamp or msgid selector;
* BETWEEN accepts two concrete selectors and permits mixed reference forms
  because the current command grammar permits either selector type in each
  position, subject to advertised `MSGREFTYPES` support;
* `draft/chathistory-end` is read as a no-value tag on the batch start;
* historical JOIN, PART, QUIT, MODE, TOPIC, NICK, KICK, AWAY, ACCOUNT, and
  TAGMSG lines are accepted only when `draft/event-playback` is enabled.

Reference serialization is centralized; callers cannot supply arbitrary raw
CHATHISTORY parameters. `ChathistoryReference.Serialize()` remains an
unprefixed diagnostic value, while `SerializeWire()` is the only current wire
form.

Reference: [IRCv3 chathistory extension](https://ircv3.net/specs/extensions/chathistory).

## 3. TARGETS request and response model

`ChathistoryOperation.Targets` is a discovery operation. It requires:

* current registered generation and network ownership;
* no ordinary target or conversation identity;
* two valid UTC timestamp references in ascending order;
* a positive limit clamped by both the server `CHATHISTORY` ISUPPORT value and
  the nexIRC client maximum;
* the same request timeout, cancellation, disconnect, and batch ownership as
  content history.

The response is exposed as `ChathistoryResult.Targets`, whose rows contain:

* network id and connection generation;
* target spelling;
* latest known server-history timestamp;
* `Channel`, `Query`, or `Unknown` kind.

Rows are parsed only from an owned `draft/chathistory-targets` batch and are
deduplicated per request/generation using the active IRC casemapping. Malformed
commands, invalid timestamps, unsafe target tokens, wrong batch types, stale
generations, and over-limit input are ignored or fail the bounded request; they
cannot create a workspace tab.

Channel classification uses the negotiated `CHANTYPES` set. A non-channel row
is treated as a query candidate only when it is a bounded safe IRC token. A
TARGETS row is never itself conversation activity.

## 4. Discovery horizon and budgets

Automatic reconnect discovery is intentionally a startup aid, not a full
account-history crawl.

| Budget | Bound |
| --- | ---: |
| TARGETS rows per operation | 16 |
| channel recovery conversations | 16 |
| total automatic history operations | 32 |
| newly recovered query conversations | 8 |
| messages/events recovered across reconnect | 200 |
| content request size | min(server limit, configured client limit) |
| query discovery age | at most 24 hours |
| clock-skew allowance | 5 seconds |
| request lifetime | `ChathistoryRequestTimeout` |

If there are no open query tabs, TARGETS still runs from the last disconnect
time. If there is a known query boundary, the oldest known server timestamp is
used. Both are clamped to the 24-hour maximum age. Manual paging and context
actions remain separately bounded and can be used later.

## 5. Query identity and continuity

The logical query identity remains network-scoped and view-stable. A visible
nickname is mutable display data, not permanent person identity.

`QueryContinuityPolicy` has four explicit outcomes:

* `ReuseExistingQuery`: the same network, a query that was open before the
  disconnect, and a current-session target match are present. Account
  consistency or a live message can provide additional evidence.
* `CreateRecoveredQuery`: TARGETS identifies a valid query target for which no
  existing query can be safely reused. The recovered query is created without
  focusing or opening a new WPF tab.
* `LeaveAsCandidate`: a same-spelling query exists without reconnect evidence,
  or an old query and a post-reconnect nickname cannot be correlated safely.
* `Reject`: the row is malformed or not a usable query target.

Same nickname alone is never enough. An identical nickname on another network
is always separate. A historical NICK line is alias evidence for the timeline,
not permission to rename the current query. Current account information is
accepted as strengthening evidence when available, but the implementation does
not invent account identity where the server did not provide it.

An open query marked as a reconnect candidate may be reused by a matching live
message in the new generation. The candidate is cleared as soon as current
session identity is established. If a live message creates a query before
discovery completes, the discovery pass sees that existing live query and
leaves an ambiguous target unmerged rather than creating `Alice (Recovered)`.

## 6. Recovered-query and unread behavior

Recovered content enters the same canonical transcript/history path as channel
playback. It is not routed through the live unread counter or notification
publisher. The workspace has a bounded `RecoveredUnread` indication and count;
opening/marking the view read clears it. It is deliberately separate from
`UnreadCount`, so local history reads and manual older paging do not create new
live activity. No per-message desktop notifications or taskbar flashes are
generated.

The manager emits one bounded status indication after reconnect recovery rather
than a notification storm.

## 7. Reconnect sequencing

For a registered replacement generation the sequence is:

1. restore desired channels;
2. perform bounded known-channel recovery using the proven LATEST boundary;
3. issue one bounded TARGETS discovery request;
4. evaluate target rows against the conservative query policy;
5. perform bounded LATEST recovery for safe/recovered query candidates;
6. clear reconnect boundaries and publish one summary status row.

Every stage checks generation ownership and the shared request tracker. A
disconnect, timeout, capability downgrade, or stale generation terminates the
remaining recovery sequence. No BETWEEN request is inserted into the proven
Phase 1X automatic gap algorithm: Phase 1X has only one reliable pre-disconnect
anchor in the normal case, while BETWEEN materially helps only when both
authoritative endpoints are known. The public context API supports BETWEEN for
that future and deterministic use case.

## 8. AROUND and BETWEEN

AROUND and BETWEEN use the existing `ChathistoryResult.Messages` path. They do
not have a special transcript implementation. Responses are routed through the
same `ServerPlayback` projection, JSONL persistence, exact server-id dedupe,
no-id preservation, and canonical timestamp/durable-sequence ordering.

`NetworkSessionManager.LoadContextAroundAsync` is available to the shared
`WorkspaceActionRouter` as `LoadContextAround`. It is enabled only when the
network is registered, CHATHISTORY is usable, the view is a valid channel/query,
and the selected entry has a supported server timestamp or msgid. The action is
revalidated immediately before sending. `LoadContextBetweenAsync` provides the
same bounded contract for two anchors.

Ordinary local search and local around-date history remain fully available
without a server connection.

## 9. Historical event representation

`IrcSemanticEvent` now carries source, network scope, and the logical history
conversation when it is server playback. Historical forms are typed as the
existing event records with `IsHistorical=true` and
`Source=ServerPlayback`:

* JOIN → `IrcJoinEvent`
* PART → `IrcPartEvent`
* QUIT → `IrcQuitEvent`
* NICK → `IrcNicknameChangedEvent`
* MODE → `IrcModeEvent`
* TOPIC → `IrcTopicEvent`
* KICK → `IrcKickEvent`
* AWAY → `IrcAwayEvent`
* ACCOUNT → `IrcAccountEvent`
* TAGMSG → bounded `IrcTagmsgEvent`

Message server timestamp, msgid, BATCH id, and received-at data remain on the
typed IRC message. The application projection renders these events using the
same visual status/message kinds as live events, but with `ServerPlayback`
provenance.

TARGETS rows use `IrcHistoryTargetEvent` only as an internal discovery metadata
event and are not rendered as transcript activity. TAGMSG has no reactions,
read-marker, typing, or other modern-message semantics in Phase 1Y.

## 10. Live-state firewall

The state store now forks at the validated history-batch boundary:

```text
typed IRC event
        +--> live reducer       --> current session state
        +--> historical projector --> timeline/history only
```

Historical projection never calls the live reducer. In particular:

* historical JOIN does not add members, mark joined state, or request NAMES;
* historical PART, QUIT, and KICK do not remove current members;
* historical NICK does not rename the current participant or query;
* historical MODE does not alter current prefixes, channel modes, or action
  authority;
* historical TOPIC does not replace the current topic;
* historical AWAY/ACCOUNT does not mutate current presence metadata;
* historical events do not alter desired-channel or JOIN lifecycle state;
* stale/wrong-batch/malformed events are ignored before projection.

The application also skips live reconciliation helpers for historical
TOPIC/MODE/KICK/NICK events. This makes the firewall testable at both the Core
state snapshot boundary and the WPF/workspace boundary.

## 11. Persistence, identity, ordering, and dedupe

Historical status entries are persisted through the existing version-2 JSONL
schema. The existing `TranscriptEntryKind` and `LogMessageKind` discriminators
are sufficient; no runtime participant snapshot is serialized. A playback row
is logged only after canonical insertion succeeds.

Exact identity remains:

```text
NetworkId + ServerMessageId
```

The same server id on two networks is not a duplicate. Without a msgid,
identical timestamp/sender/text/event rows remain distinct. Historical
messages and historical state events use one transcript ordering contract:
server timestamp first, then durable sequence, then deterministic identity/text
fallbacks. Playback arriving after newer live traffic is inserted into its
canonical position and is never replayed retroactively through live state.

Old JSONL records remain readable. Local reads never write themselves back.

## 12. Capability downgrade and malformed input

`draft/event-playback` is requested only through the Phase 1Y capability set,
and the handler is active before the request set is advertised. If the server
omits or NAKs it, `ChathistorySupport.EventPlaybackEnabled` is false and the
ordinary PRIVMSG/NOTICE history path continues unchanged. Reconnect resets the
capability snapshot and requires fresh negotiation.

Malformed historical state input is ignored without disconnecting a healthy
session. Wrong BATCH types cannot complete a pending operation. A target batch
is never treated as content, and a content batch is never treated as TARGETS.

## 13. WPF and action integration

The existing serialized `WorkspaceActionRouter` remains the application
boundary. It now exposes a bounded `LoadContextAround` action for channel and
query views when a usable anchor exists. The existing Load Older action remains
manual and channel/query scoped. No new sidebar paradigm or automatic tab focus
was added.

Projection coalescing and dispatcher backpressure are unchanged. Historical
state events are ordinary bounded transcript insertions; they do not trigger
member collection churn because the current participant reducer is never
called.

## 14. Validation matrix

Focused Phase 1Y coverage includes:

* TARGETS serialization, interval validation, limit clamp, empty/duplicate/
  malformed rows, channel/query classification, stale generation, timeout,
  disconnect, wrong-batch fencing;
* query policy reuse/create/ambiguous/cross-network branches;
* AROUND msgid, BETWEEN timestamp and msgid forms, mixed selector validation,
  bounded responses, and canonical merge path;
* event-playback advertised/ACK/NAK and ordinary history downgrade;
* historical JOIN/PART/QUIT/NICK/MODE/TOPIC playback with current-state
  snapshots unchanged;
* server playback never triggering live side effects.

The existing Phase 1R, 1T, 1U, 1V, 1W, and 1X suites remain meaningful and are
run without lowering their thresholds. Release/build/format totals and the
credential-free live IRC result are recorded in the Phase 1Y delivery report.

## 15. Limitations and next phase

The draft protocol may change incompatibly. Libera may not advertise either
history capability, so deterministic fake-server coverage is normative for this
phase. Account identity correlation is only used when the server supplies it;
the client does not claim to prove permanent nickname ownership. There is no
historical WHO/NAMES snapshot replay, reaction/read-marker/redaction/edit/
reply/multiline feature, or netsplit reconstruction.

Phase 1Z should focus on stronger server/account identity evidence, richer
history navigation and anchor indexing, and live-server interoperability once a
configured bouncer provides the draft capabilities—while preserving the
current-state firewall.
