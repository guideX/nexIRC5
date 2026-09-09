# Phase 26 — IRCv3 reactions and durable reaction state

Status: implemented. This phase is classified as `DETERMINISTIC_REACTIONS_COMPLETE` (Outcome B): the implementation is covered by fake-transport, storage, and desktop build evidence; no live IRC compatibility environment was configured for this run.

## Protocol

nexIRC recognizes only the exact, case-sensitive client tags `+draft/react=<value>` and `+draft/unreact=<value>`. Both forms also require the existing canonical `+reply=<parent-msgid>` tag and are valid only when carried by `TAGMSG <target>`. The builder emits exactly:

```text
@+reply=<parent>;+draft/react=<value> TAGMSG <target>\r\n
@+reply=<parent>;+draft/unreact=<value> TAGMSG <target>\r\n
```

The existing message-tag parser, escaping, duplicate-key (last value wins), UTF-8 framing, and Phase 25 parent-msgid validation are reused. Missing/invalid parent metadata, wrong-case tags, empty values, and simultaneous react/unreact tags fail closed while leaving the containing IRC message available as an unrecognized `TAGMSG`. Received values are not restricted to an emoji allow-list; the outbound UI path applies a deterministic 128-byte UTF-8 product limit and rejects control characters.

## State and actor identity

The protocol produces a typed `IrcReactionEvent`, and the application converts it to a typed `ReactionEvent` scoped by network and durable conversation. Each `WorkspaceView` owns a bounded `ReactionStateStore`; it is not a global graph. State is `parent msgid -> reaction value -> actor set`, with idempotent React/Unreact transitions, multiple values per actor, first-known deterministic ordering, and bounded actor attribution.

Actor keys prefer an authenticated `account` tag, then exact prefix evidence, then conservative nickname evidence. Keys include the network ID. Friendly attribution retains bounded account/nickname/user/host evidence without exposing internal keys. A current-session nickname alias reconciles a no-echo local operation with its later own server echo without promoting that evidence to durable account identity.

## Durable history and playback

Reaction events use an additive JSONL record kind (`messageKind: "Reaction"`) with parent/value/operation/actor evidence fields. Existing Phase 25 and pre-v3 records remain readable because the new fields are optional. Server event msgids use the existing network-scoped deduplication; no-id events remain distinct on disk while state transitions remain idempotent. A bounded reaction-event read path rebuilds summaries in canonical order without rewriting the transcript.

Reaction records are excluded from ordinary history pages, exports, search results, transcript projection, history counts, and notification/activity presentation. They may still hydrate state when a view is reopened, paged, navigated, or receives historical CHATHISTORY playback. Historical state reconstruction never raises unread activity or live notifications. A reaction received before its parent is retained in the bounded per-view relationship store and attaches when the parent enters the projection; no fake parent, timestamp matching, or broad recovery request is created.

## Sending and capabilities

WPF invokes `WorkspaceActionRouter`, which revalidates the network/view, registration, current generation, writable channel/query ownership, canonical parent, `message-tags`, target, value, and relevant client-tag policy before calling the single `ServerSession.SendReactionAsync` path. `CLIENTTAGDENY` supports exact entries, `*`, and `-` exceptions with case-sensitive tag names; `reply`, `+reply`, `draft/react`, and `draft/unreact` are checked. The same `+reply` denial now disables Phase 25 reply eligibility. Receiving and displaying stored reactions remains available after a capability downgrade.

With `echo-message`, the canonical server event is authoritative. Without it, the accepted outbound operation updates the local known state and is persisted without inventing a server msgid. Current actor aliases prevent the equivalent later echo/history event from becoming a second visible reaction.

Channels and durable direct-message queries use the existing network/query identity routing. Query reactions target the durable query view, while incoming peer evidence continues through the account-aware query identity machinery. Cross-network and cross-conversation state is isolated.

## WPF presentation and accessibility

Ordinary message rows show compact subordinate reaction pills only when a summary exists. Each pill is keyboard reachable, has deterministic `MessageReaction.Summary`/`MessageReaction.Toggle` automation metadata, and exposes value, count, attribution, and whether the current user can remove their reaction through its accessible name and tooltip. The message context menu provides an arbitrary-text `React…` action; there is no permanently expanded picker.

## Bounds and verification

The implementation bounds outbound reaction values to 128 UTF-8 bytes, pending parents to 1,024, reaction groups per parent to 128, actors per group to 4,096, attribution names to 12, and durable reaction hydration to the existing 10,000-record history export limit. Transcript projection remains capped at 500 entries.

Focused coverage includes exact protocol parsing/building, malformed metadata, duplicate tags, wire framing, target and value validation, `CLIENTTAGDENY`, session routing, no-echo and echo reconciliation, actor aggregation, idempotent unreaction, network/conversation isolation, JSONL reopen, search/history filtering, and a 50,000-message/5,000-event structural scale fixture. The desktop release build is included in the Phase 26 closeout. No live IRC server or external UI automation result is claimed.

Known limitation: the draft reaction tags are not advertised as a separate negotiated capability in this phase; sending is gated by negotiated `message-tags` plus current session/tag-deny policy, and server relay/interoperability still needs live validation.

Recommended Phase 27: validate the draft tags against a configured compatible IRC service, then add any server-specific capability/relay observations without weakening the conservative playback and identity boundaries.
