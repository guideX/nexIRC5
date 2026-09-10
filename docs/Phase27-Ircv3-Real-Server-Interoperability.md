# Phase 27 — IRCv3 real-server interoperability

Phase 27 validates nexIRC 5 message relationships against two independently connected real nexIRC clients and a real IRC server. The primary result is:

`REAL_SERVER_INTEROPERABILITY_VERIFIED` (Outcome B)

## Selected server and method

The selected endpoint was the official Ergo test service `testnet.ergo.chat:6697` over TLS. The local machine did not have Docker, Podman, Ergo, or InspIRCd available, so no local daemon or container was launched. The existing production TLS validation path was used; no certificate bypass, password, SASL credential, account registration, or unrelated channel interaction was used.

The observed server identified itself as Ergo, running version `ergo-v2.19.1`, on the `ErgoTestnet` network. Each run creates two randomized nicknames and a randomized temporary channel, then always attempts PART and disposal/QUIT cleanup. The test service reported that it runs in debug mode and logs user I/O; the harness therefore writes only sanitized, relevant protocol lines and never sends credentials.

Repeat the experiment from a terminal:

```powershell
.\scripts\run-phase27-ircv3-interoperability.ps1
```

The script has a bounded timeout, refuses plaintext remote runs, writes ignored artifacts under `artifacts/phase27/`, and returns the harness exit code. A local plaintext daemon can be tested only with an explicit localhost endpoint:

```powershell
.\scripts\run-phase27-ircv3-interoperability.ps1 -Server localhost -Port 6667 -NoTls
```

The harness itself is the developer-only `nexIRC.Interoperability` console project. It uses two real `NetworkSessionManager`/`WorkspaceActionRouter` instances, so reply, reaction, unreaction, query, reconnect, logging, search, and navigation assertions use product paths rather than hand-written relationship commands.

## Negotiation and server profile

The harness requested and enabled the following capabilities:

| Capability | Observed result |
| --- | --- |
| `message-tags` | advertised and ACKed |
| `echo-message` | advertised and ACKed |
| `server-time` | advertised and ACKed |
| `account-tag` | advertised and ACKed |
| `batch` | advertised and ACKed |
| `labeled-response` | advertised and ACKed |
| `draft/chathistory` | advertised and ACKed |
| `draft/event-playback` | advertised and ACKed |
| `away-notify`, `extended-join`, `multi-prefix`, `account-notify` | advertised and ACKed |

The server also advertised `draft/persistence`, `draft/message-redaction`, `draft/relaymsg=/`, `draft/read-marker`, `draft/multiline`, `draft/extended-isupport`, `znc.in/playback`, and other Ergo-specific or draft tokens. The relevant `005`/ISUPPORT evidence included:

```text
CASEMAPPING=ascii
CHATHISTORY=1000
MSGREFTYPES=msgid,timestamp
NETWORK=ErgoTestnet
CHANTYPES=#
```

`CLIENTTAGDENY` was absent. This is classified as `Absent`, not as an implicit allow-all guarantee for every server. The harness records absent, empty, explicit-list, wildcard, and wildcard-with-exceptions as separate classifications; deterministic Phase 26 tests remain authoritative for deny-list policy because a remote test-service configuration must not be changed.

The server’s observed relationship capability classifications were:

* Replies: outbound supported, relayed, and canonicalized by the server.
* Reactions: outbound supported, relayed, and accepted with `+reply` plus `+draft/react`/`+draft/unreact`.
* Client-only control tag: relayed.
* No capability or deny-list block was observed.

The JSON result records these classifications as `ReplySupport` and `ReactionSupport` alongside the raw capability and deny-list evidence.

## Wire behavior and relationship results

The normative nexIRC contract remains:

```text
@+reply=<msgid> PRIVMSG <target> :<text>\r\n
@+reply=<msgid>;+draft/react=<value> TAGMSG <target>\r\n
@+reply=<msgid>;+draft/unreact=<value> TAGMSG <target>\r\n
```

The server assigned an opaque `msgid` to the canonical parent, the reply, and reaction TAGMSG events. It also added `time`, and during history playback added `batch`. Server tag order differed from client order in observed lines; the semantic parser treats tags as an unordered keyed set. The server preserved the client-only relationship tags while adding server metadata.

Channel validation passed:

* A sent one canonical parent `PRIVMSG`; B received the same server-assigned parent `msgid`.
* B sent a reply through `WorkspaceActionRouter`; exactly one outbound `PRIVMSG` carried the exact `+reply=<parent-msgid>`.
* A resolved the reply parent and received a canonical child `msgid`.
* B’s `echo-message` path projected one reply row, not an optimistic row plus an echo duplicate.
* B sent `👍` through the Phase 26 action path; the server relayed `TAGMSG` to A with `+reply` and `+draft/react` intact.
* A projected one attributed reaction, with no blank transcript row. B’s echo did not double-count.
* B removed the reaction with `+draft/unreact`; A removed only B’s actor/value pair and the empty group disappeared.
* Both actors reacted to `👍`, producing count 2; A removed its own actor, leaving B; B removed its own actor, leaving no group.
* `👍` and `😂` coexisted independently and were removed independently in deterministic order.

The developer-only control experiment sent `+nexirc/phase27-test` on a harmless `TAGMSG`. Ergo relayed it, demonstrating generic permitted client-only tag relay separately from reaction-specific handling.

The safe negative case sent a reaction to a randomized inaccessible target. The server returned numeric `403 No such channel`; no pathological or high-volume traffic was sent.

## Query/private-message validation

The same two clients repeated the relationship flow as a direct query. The canonical DM parent, reply, reaction, and unreaction all routed to the correct query identity. The channel parent did not appear in the query. Echo projection remained single-row, reaction TAGMSG remained row-free, and the JSONL store persisted the DM reply. Search found the reply in the durable `PrivateConversation:<folded-nickname>` key, and `NavigateToHistorySearchResultAsync` successfully jumped back to the query result.

## Reconnect and CHATHISTORY

Client B was reconnected using `NetworkSessionManager.ReconnectAsync`, rejoined the temporary channel, and sent a new `🎉` reaction. The current session accepted and relayed exactly one operation. Client A was then disconnected while B sent an additional ordinary message, after which A reconnected and exercised bounded `CHATHISTORY LATEST`.

The observed history response contained 15 semantic events, including both the canonical parent and a reaction event, and closed with an explicit history-end marker. This run therefore observed reaction playback from this server. The implementation still treats a history-derived reaction summary as only the set of known historical events; it does not scan broadly to invent counts.

The session generation recorded by the replacement session was session-local (the old and replacement session each reported generation 1). Stale work is fenced by the existing session/manager ownership and generation checks; the deterministic networking regression suite covers stale callbacks across an actual generation increment.

## Missing parent and CLIENTTAGDENY limitations

The live server run did not safely reproduce a missing-parent projection that would justify a separate remote recovery request. The result records this as unavailable rather than claiming it. Phase 25/26 deterministic recovery, coalescing, and bounded-request tests remain authoritative.

The remote service’s `CLIENTTAGDENY` was absent and its configuration was not modified. Exact deny-reply, deny-reaction, wildcard, and exception matrices remain covered by deterministic Phase 26 tests. This respects the rule that remote server configuration is out of scope.

## Observability and repairs

`ServerSession` now exposes narrowly scoped developer diagnostic events for its already-redacted raw receive lines and outbound commands. The events are observational, non-owning taps; the application’s bounded event channels remain owned by the normal manager drainer. Subscriber exceptions are swallowed so diagnostics cannot alter protocol state.

The harness writes:

* `artifacts/phase27/live-result.json` — machine-readable result and classification;
* `artifacts/phase27/live-transcript.jsonl` — sanitized relevant CAP, ISUPPORT, JOIN/PART, PRIVMSG, TAGMSG, BATCH, CHATHISTORY, and error traffic.

The sanitizer redacts PASS/AUTHENTICATE material, masks IPv4 literals, and masks the public VAPID value in transcript lines. Volatile artifacts are ignored by Git.

The live run exposed harness-only issues rather than a protocol defect: TAGMSG has one target parameter and no trailing text parameter; reconnect may have an automatic history request already in flight; and the isolated harness must load its logging preferences before constructing the manager. Those cases are now handled by the harness. No Phase 26 wire or projection behavior required a change.

## Normative behavior, server behavior, and nexIRC policy

Normative or contract-level behavior: message tags are keyed metadata; `+reply` is the reply relationship tag; Phase 27 reactions use the draft `+draft/react` and `+draft/unreact` tags; reaction TAGMSG has a target but no chat text; server-assigned `msgid` values are opaque; and `server-time`/`batch` are metadata that may be present on relayed or historical events.

Ergo-specific observations: tag order was rewritten; `msgid`, `time`, and `batch` were added; `CLIENTTAGDENY` was absent; `CHATHISTORY` advertised a limit of 1000 with `msgid,timestamp` references; and the invalid target produced numeric 403. These facts are evidence for this server, not a universal IRC promise.

nexIRC policy: relationship projection requires a canonical parent identity; client-only TAGMSG events update reaction state without creating ordinary transcript rows; exact server identities deduplicate echoes and playback; history recovery is bounded and conservative; and unsupported or denied relationship operations remain disabled rather than guessed.

## Evidence and limitations

The full successful evidence is in the ignored live artifacts generated by the command above. It proves one real server implementation, not multi-server compatibility, so the correct classification is Outcome B rather than Outcome A. The testnet’s debug logging notice is a service limitation, although this run used randomized temporary identities and no credentials. There was no local daemon/container comparison, and no remote `CLIENTTAGDENY` configuration matrix.

Recommended Phase 28: add a second isolated daemon implementation when a portable test runtime is available, then compare the same structured capability and relationship result schema without changing the Phase 25/26 relationship contract.
