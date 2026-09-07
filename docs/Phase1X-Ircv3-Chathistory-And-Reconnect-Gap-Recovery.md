# nexIRC 5 Phase 1X — IRCv3 CHATHISTORY and Reconnect Gap Recovery

## Outcome

Phase 1X adds a capability-gated, bounded CHATHISTORY request path on top of
the Phase 1W durable playback substrate. Server history is optional. A server
that does not advertise the capability continues through the ordinary live
and local-history paths without warnings or protocol writes.

The IRCv3 CHATHISTORY command is still a draft/work-in-progress extension.
nexIRC therefore treats it as an explicitly negotiated and bounded feature,
not as a generally available IRC command.

## 1. Phase 1W substrate inherited

Phase 1X reuses the existing `ConversationEntryCandidate`,
`ConversationHistoryMerge`, canonical timestamp/sequence ordering,
network-scoped authoritative server-message identity, JSONL schema evolution,
and side-effect-free historical projection.

`Live`, `LocalHistory`, and `ServerPlayback` remain distinct. A server replay
does not become a live participant, notification, unread, highlight, or
automation event. Local JSONL projection never writes itself back.

## 2. Capability name and negotiation

The exact capability name is `draft/chathistory`. The client does not request
an unprefixed `chathistory` alias and does not request `draft/event-playback` in
the normal Phase 1X path.

The application default Phase 1X capability set includes:

* `draft/chathistory`;
* `batch`;
* `server-time`;
* `message-tags`.

The session adds the three substrate capabilities if a caller explicitly asks
for `draft/chathistory`. An ACK alone never sends a history command. A request
is usable only when all four capabilities are enabled. A NAK, missing
advertisement, reconnect reset, or generation change disables the path for
that session generation.

CAP state remains generation-scoped and retains advertised, requested,
enabled, and rejected values independently.

## 3. BATCH requirements and validation

The request path expects a bounded BATCH response with this shape:

```text
BATCH +<id> chathistory <target>
@batch=<id> ... PRIVMSG/NOTICE ...
BATCH -<id>
```

The existing maximum of 32 active batches, bounded identifiers, bounded
parameters, harmless unknown close handling, and generation reset are retained.
The BATCH model now exposes the stored type and parameters to the session
coordinator, allowing it to validate target ownership before accepting child
messages.

An unrelated `chathistory` batch is fenced and cannot complete the active
request. A malformed or never-closed owned batch terminates by bounded timeout
or disconnect cancellation.

## 4. ISUPPORT parsing

Arbitrary-valued `005` tokens remain retained in the typed ISUPPORT snapshot.
Phase 1X additionally interprets:

* `CHATHISTORY=<limit>`;
* `MSGREFTYPES=<comma-separated-types>`.

Missing values, malformed limits, zero limits, unknown reference names, and
duplicate names are safe. Known types are retained in server preference order.
The currently understood reference types are `msgid` and `timestamp`.

`CHATHISTORY=0` is retained as the protocol unlimited marker. It never creates
an unbounded client request. A malformed numeric limit is treated as unknown
and the client maximum still applies.

If `MSGREFTYPES` is absent or has no known value, nexIRC conservatively uses a
timestamp reference model. It does not infer `msgid` support from
`message-tags`.

ISUPPORT and capability state are rebuilt after reconnect; values from a prior
generation are not reused.

## 5. Typed support model

`ServerFeatureSet.Chathistory` exposes:

* `CapabilityEnabled`;
* `BatchEnabled`;
* `ServerTimeEnabled`;
* `MessageTagsEnabled`;
* `ServerMaximumRequestSize`;
* `ClientMaximumRequestSize`;
* `SupportedReferenceTypes`;
* `PreferredReferenceType`;
* `EffectiveMaximumRequestSize`;
* `IsUsable`.

The default client maximum is 100 messages per request. It can be lowered in
session options, is always positive, and is combined with a positive server
limit using the smaller value.

## 6. Request abstraction

`ChathistoryRequest` carries the owning network ID, connection generation,
logical conversation, wire target, operation, typed reference, requested limit,
and purpose. Purposes are `InitialContext`, `ReconnectGap`, and `LoadOlder`.

The request lifecycle retains a monotonic request ID, start/completion state,
generation, conversation, reference, and bounded accepted message list. There
is at most one active request per session, which also prevents unbounded
concurrent paging.

Supported operations are `LATEST`, `BEFORE`, and `AFTER`. `AROUND` and
`BETWEEN` are represented in the type model but rejected by the Phase 1X wire
builder for later work.

## 7. Reference selection

References are typed and cannot be assembled as arbitrary raw command text.
Supported values are authoritative server `msgid`, authoritative server
timestamp, and wildcard/latest.

For manual paging and reconnect recovery, nexIRC walks the server-advertised
reference preference order:

1. use a server message ID only if `msgid` is advertised and the boundary has
   an authoritative ID;
2. otherwise use a server timestamp only if `timestamp` is advertised and the
   boundary timestamp came from `server-time`;
3. otherwise do not send the request.

Local durable sequences, local receive timestamps, and fabricated IDs are
never serialized as server references.

## 8. Command construction

`ChathistoryCommandBuilder` is the only Phase 1X command construction path.
It validates target tokens, ownership fields, generation, operation,
reference compatibility, positive limits, server/client limits, and IRC line
length through the existing command builder.

Examples:

```text
CHATHISTORY LATEST #room * 50
CHATHISTORY BEFORE #room abc123 50
CHATHISTORY AFTER #room 2026-09-07T12:00:00.000Z 50
```

There is no arbitrary raw CHATHISTORY escape hatch.

## 9. Timeout, cancellation, and errors

Every request has a default five-second timeout. It can be shortened by
session options. Disconnect, disposal, generation invalidation, cancellation,
duplicate active request, malformed ownership, and timeout all terminate the
request. A closed conversation cancels its matching request.

The narrow standard-reply surface recognizes `FAIL`, `WARN`, and `NOTE`.
`FAIL CHATHISTORY ...` and relevant legacy numeric errors complete the request
as a failure. Unknown failure text remains bounded diagnostic text. `WARN` and
`NOTE` are retained as typed semantic replies without turning them into a
broad standard-replies UI.

Successful empty batches are valid results and mark the conversation's older
direction exhausted. A `draft/chathistory-end` (with the tolerated
`chathistory-end` spelling) BATCH marker also marks exhaustion. No automatic
infinite paging loop is used.

## 10. Accepted playback content

Only messages in an owned, target-matching, open `chathistory` batch are
accepted as server playback. Phase 1X maps:

* `PRIVMSG`;
* `NOTICE`;
* CTCP `ACTION`.

The existing typed message retains sender, target, server timestamp, account
tag, server message ID, batch ID, network/session generation, and all bounded
unknown tags. Historical account metadata remains message context; it is not a
presence mutation.

## 11. Historical state-event defense

Without `draft/event-playback`, historical `JOIN`, `PART`, `QUIT`, `NICK`,
`MODE`, `TOPIC`, `AWAY`, and `ACCOUNT` messages are not applied to current
session state. Children of an owned history batch that are not supported
message types are ignored safely. Children of an unrelated history batch are
discarded before they can mutate presence.

`draft/event-playback` is deliberately deferred until a separate state
reconstruction model exists.

## 12. Playback merge and persistence

Accepted playback enters the same Phase 1W candidate/merge path as local and
live records. There is no CHATHISTORY-specific deduplication key.

An exact `NetworkId + ServerMessageId` replay is one logical message. The same
ID on a different network remains distinct. Two no-ID observations remain two
messages even when sender, timestamp, action kind, and text all match.

New server playback may be appended to durable JSONL once. The existing JSONL
store performs exact server-ID no-op suppression, so a replay already present
on disk is not appended again. Local-history projection remains read-only.

## 13. Canonical ordering and live races

Protocol receive order remains the order used for session state processing.
Transcript display order remains the Phase 1W canonical timestamp,
durable-sequence, and deterministic fallback order.

Therefore, if reconnect sends a history request, live message `E` arrives,
and the response later contains `C` and `D`, the display merge is `C`, `D`,
`E` whenever the authoritative timestamps establish that order. An exact ID
overlap with `E` is removed. A no-ID overlap is retained conservatively.
Live traffic is not globally buffered waiting for playback.

Playback does not increment unread/activity, publish notifications, flash the
taskbar, alter highlights, or update participant state.

## 14. Manual older-history paging

The shared `WorkspaceActionRouter` exposes `Load older messages` for channels
and queries. Enablement is recomputed from the current network/session,
conversation identity, usable capability state, authoritative boundary,
request-active state, and exhaustion state.

The action finds the earliest canonical transcript entry, selects the
server-preferred authoritative reference, issues bounded `BEFORE`, and lets
the normal playback merge handle the result. It returns concise feedback such
as `Loaded 24 older messages`, `No older server history is available`, or a
bounded failure message.

Local history continues to work when server history is unavailable.

## 15. Reconnect gap recovery

Before an automatic reconnect, the application records the latest authoritative
boundary for currently joined channel views. It intentionally does not scan
every persisted conversation. The recovery set is capped at 16 conversations,
one request round per conversation, 50 requested messages per conversation,
200 total recovered messages, and the normal five-second request timeout.

After registration, each boundary is converted using the server's preferred
reference type and a bounded `LATEST` request is sent. Recovery runs
sequentially because the session allows one active history request. Failures
are quiet; a single status summary is shown only when messages were actually
recovered.

The exact reconnect example is:

```text
live A(id=A), live B(id=B)
disconnect
offline C(id=C), D(id=D)
reconnect
request LATEST #room B 50
live E(id=E)
history batch C, D, and possibly E
```

`C` and `D` become `ServerPlayback` candidates. `E` remains the live
observation unless its authoritative ID appears in the batch, in which case
the Phase 1W exact-ID merge keeps one record. The final canonical conversation
contains A, B, C, D, E in timestamp/sequence order, with no participant
rollback and no deletion of legitimate no-ID repeats.

## 16. Query behavior

Manual query paging uses the stable Phase 1W query conversation key and the
currently displayed peer target. Automatic reconnect recovery is deliberately
limited to joined channels in Phase 1X. A private nickname can change or be
reused across sessions without IRC proving that it is the same conversation.
This avoids fabricating cross-session nickname continuity; manual query
history remains available whenever a safe authoritative boundary exists.

## 17. Exhaustion and generation state

Exhaustion, active request, and last-failure state are attached to the
session's logical conversation key. Empty success and the draft end marker
set the older-direction exhausted flag. A new connection generation clears
capability, ISUPPORT, request, and exhaustion state. New live data does not
automatically trigger endless paging.

## 18. TARGETS and deferred features

`CHATHISTORY TARGETS` is not required. It would discover direct-message
conversations outside the current workspace and would expand identity/UI
scope. It is recommended for Phase 1Y only after conversation discovery and
query identity rules are explicit.

`AROUND`, `BETWEEN`, event playback, redactions, edits, reactions, read
markers, replies, multiline, broad labeled-response work, and broad
standard-replies UI are deferred.

## 19. Deterministic evidence

The fake transport exercises the real parser, CAP/ISUPPORT state, session
generation, BATCH ownership, typed request coordinator, and semantic event
path. Focused Phase 1X tests cover capability/ISUPPORT parsing, bounded
commands, valid and empty batches, unrelated batches, timeout cleanup,
historical content, and current-state fencing. Existing Phase 1W tests remain
unchanged and continue to cover exact IDs, no-ID repeats, schema compatibility,
ordering, malformed JSONL, search/export, volume, and bounded page reads.

## 20. WPF and performance boundary

The UI receives one shared contextual action; it does not construct IRC lines
or observe raw protocol state. Historical insertion uses the existing
cooperative projection batch and canonical transcript path. No permanent
provenance badge or transcript redesign is added.

The existing bounded dispatcher, transcript capacity, JSONL queue, and exact
identity indexes remain the performance controls. Playback is merged through
the central path rather than sorting the complete history on every child
message. A future dedicated 500-message playback burst smoke should retain
the Phase 1P/1R queue and WPF interactivity thresholds.

## 21. Live-server finding

The credential-free Libera smoke remains the supplemental live validation for
DNS, transport, TLS, CAP, registration, JOIN/NAMES, metadata, WHOIS, QUIT,
disconnect, and process exit. Libera did not advertise CHATHISTORY during the
Phase 1X validation path, so no live CHATHISTORY result is claimed. The
deterministic fake-server proof is normative for the new optional feature.
