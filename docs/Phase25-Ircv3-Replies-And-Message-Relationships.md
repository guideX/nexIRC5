# Phase 25 — IRCv3 Replies and Durable Message Relationships

## Scope and starting state

This phase was implemented in `D:\dev\nexIRC\nexIRC5` on `main`, starting at `18dacb7` (`Harden nexIRC 5 history integrity and gap repair`). The authoritative pre-change worktree was clean and local divergence was `0 ahead / 0 behind`; no fetch, pull, merge, rebase, reset, or push was performed.

The Phase 24 baseline supplied by the request was Core `86/86`, Networking `42/42`, Application `168 passed / 1 skipped`, with external UI Automation unavailable. The current focused baseline remained green while the Application endurance/full-host run continued to reproduce a host-side stall before its summary.

## Protocol and capability policy

nexIRC implements the ratified client-only `+reply=<msgid>` tag. It does not implement the obsolete/draft `+draft/reply` name. Parent ids are opaque, case-sensitive, network-scoped values; they are never case-folded, parsed numerically, or used for chronology.

`IrcReplyReference` validates a non-empty id with the existing 256-character bound and rejects whitespace, controls, and NUL. Invalid or empty values remain valid IRC messages and expose `ReplyValidationError`; the ordinary message body is not dropped. Duplicate tags retain the generic message-tag policy: raw tags remain available and the last exact key wins in `TagValues`.

Sending requires negotiated `message-tags`. `echo-message` was added to the preferred capability sets so canonical server echoes can supply the child msgid and server-side metadata. No separate reply capability is requested. `TAGMSG` does not create a visible reply.

The typed wire builder emits one bounded ordinary message:

```text
@+reply=<msgid> PRIVMSG <target> :<text>\r\n
```

The existing UTF-8 framed-line accounting is reused, including tag bytes and CRLF. Oversized replies fail before writing.

## Relationship and persistence architecture

`ReplyRelationship` carries the network id, durable conversation key, optional child canonical id, opaque parent id, provenance, parent preview metadata, and resolution state. `TranscriptEntry.ReplyParentMessageId` is the lightweight relationship field; it is not a WPF row reference.

Parent lookup is exact and conversation-local. It uses the existing network/conversation-scoped JSONL and `.hidx` msgid paths. No graph database or reverse relationship index was added. The parent may be absent.

Conversation JSONL is now schema version 3 with one additive optional `replyParentMessageId` field. Older records remain readable because the missing field deserializes as null; malformed relationship metadata remains skippable. Search results and history projections retain the field.

Resolution is refreshed when entries are appended or projected:

* `ResolvedLocally` shows the parent sender and a bounded preview.
* `RecoverableRemotely` exposes `Show original` when local history or safe CHATHISTORY is available.
* `Unavailable` is stable when no safe source exists.
* `AmbiguousOrInvalid` fails closed.

Children can arrive before parents. Later canonical pagination, replay, reconnect repair, or AROUND projection refreshes the relationship without polling.

## Composition, send lifecycle, and display

The WPF message context menu offers `Reply` only when the message has a canonical msgid, the selected channel/query is writable, the network is registered, the current identity is safe, and `message-tags` is enabled. `ReplyComposerState` stores the parent id, durable conversation key, parent sender/preview, target, network, view, and connection-generation boundary.

The editor contains only message text. A bounded, accessible banner identifies the parent and provides `Cancel reply`; Escape cancels reply mode. Switching to another view clears the scoped composer. Reconnect does not erase the composer or draft, but sending revalidates current registration, target, query identity, capability, and generation.

Slash commands are dispatched through the ordinary command path and do not inherit reply metadata. A reply send uses one `PRIVMSG` and clears reply mode only after the typed send path accepts the dispatch. With `echo-message`, local optimistic insertion is suppressed and the canonical echo is the sole visible row. Without it, the existing local-echo path inserts the outgoing reply with its durable parent field; a later exact server duplicate is still deduplicated by canonical msgid.

Replies stay in normal chronological transcript order. The compact indicator does not hoist or duplicate the parent. Clicking it enters the existing Phase 22 navigation path. Missing parents use at most one coalesced bounded `CHATHISTORY AROUND ... msgid=<parent>` request, then persist/project the result and retry locally. Unsupported CHATHISTORY leaves the reply visible and unresolved without network traffic.

Historical replies use the existing playback firewall: they are persisted and projected with provenance, do not raise notifications or unread counts, and do not mutate current membership or other live state. Query replies use the Phase 24 durable query key and current safely rebound nickname; account conflicts cannot redirect a reply to another query. Network ids are included in every local lookup and recovery key.

## Verification

New focused coverage:

* Core `Phase25ReplyProtocolTests`: `5/5`.
* Networking `Phase25ReplySessionTests`: `2/2`.
* Application `Phase25ReplyRelationshipTests` and `Phase25ApplicationReplyTests`: `4/4`.
* WPF deterministic `message-reply` smoke: passed, with one tagged send, one canonical echoed row, local zero-traffic parent navigation, and search/reopen preservation.
* WPF deterministic `missing-parent` smoke: passed, with one bounded AROUND request, recovery, re-resolution, and zero additional traffic on repeat navigation.

Full current suites: Core `91/91`; Networking `44/44`. Release solution build passed with zero warnings/errors. Application Phase 1U, 1V, 1W, 1Y, 1Z identity/context, 20, 21, 22, 23, and 24 classes passed individually (`49` tests total), and 1S passed `2/2`. The full Application suite and 1R endurance class stalled in this host before reporting a result; they were stopped without altering source state. `git diff --check` was clean. `dotnet format --verify-no-changes` was attempted but did not complete in the same host within the bounded run; Release analyzers/build passed.

No live IRC reply validation was run, so there is no server capability or public-network reply evidence. External UI Automation remains unavailable; the in-process WPF smoke is the deterministic UI evidence. These limitations classify this phase as Outcome B rather than Outcome A.

## Defects found and repairs

The audit found that own direct-message echoes were not emitted into the query semantic path, which would have prevented canonical direct-message replies from appearing correctly. The session state store now routes self direct echoes to the target query, and presentation marks them as outgoing. The application also suppresses duplicate optimistic local rows when `echo-message` is negotiated.

## Remaining limitations and Phase 26

An `echo-message` server that accepts the capability but fails to echo leaves the message without a local canonical row by design; this avoids duplicate reconciliation guesses. Servers without `message-tags` cannot send compliant replies, although ordinary messages remain available. No reactions, edits, redactions, thread panels, or reverse child index were added.

Recommended Phase 26: use the same typed relationship substrate for a narrowly bounded reaction/reaction-summary feature or redaction-aware unavailable-parent state, after defining the corresponding IRCv3 capabilities and canonical lifecycle.
