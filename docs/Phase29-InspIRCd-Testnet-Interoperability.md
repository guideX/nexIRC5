# Phase 29 — Official InspIRCd Testnet Interoperability

## Outcome

`MULTI_SERVER_INTEROPERABILITY_VERIFIED`

The same production `WorkspaceActionRouter` reply/reaction architecture was
validated with two real nexIRC clients against the official InspIRCd testnet,
while the retained Phase 27 evidence remains valid for Ergo. The run did not
add server-brand behavior, msgid-shape assumptions, tag-order dependencies, or
an alternate harness implementation.

The successful InspIRCd testnet validation proves second-implementation
interoperability, but does not constitute execution of the prepared local
InspIRCd 4 capability-degradation matrix.

## Why the official testnet was used

Phase 28 prepared disposable local InspIRCd 4 profiles, but execution on this
Windows host was blocked. The official InspIRCd 4 Windows package required
administrator installation; a user-scoped attempt did not produce a runtime,
and no existing InspIRCd, Docker, Podman, or WSL runtime was available. Phase
29 therefore used the explicitly public testing service rather than repeating
that installation attempt.

The selected data-only profile is `inspircd-testnet`:

| Property | Value |
| --- | --- |
| Endpoint | `testnet.inspircd.org:6697` |
| Transport | TLS |
| Certificate validation | Normal production validation |
| Setup ownership | External service |
| Server lifecycle/configuration ownership | None |
| Client/channel cleanup ownership | Temporary clients and channel only |

The runner is [run-phase29-inspircd-testnet-interoperability.ps1](../scripts/run-phase29-inspircd-testnet-interoperability.ps1).
It creates two randomized temporary clients, one randomized channel, unique
Phase 29 markers, a bounded run, and clean PART/QUIT disposal. It does not join
project/support channels, register accounts, flood, fuzz, request operator
privileges, or modify remote policy.

## Observed server

The fresh bounded run was executed on 2026-09-12 (local time), with protocol
timestamps on 2026-09-13 UTC. The server name in the numeric/CAP traffic was
`testnet.inspircd.org`. Numeric `002` reported `InspIRCd-5`; the safe ordinary
`VERSION` query returned numeric `351` with `InspIRCd-5.`. The observed
implementation/version is therefore recorded as `InspIRCd-5`, not inferred
from the Phase 28 local package.

`001` welcome, CAP LS/ACK, VERSION, 005 ISUPPORT, JOIN/PART, PRIVMSG, TAGMSG,
and bounded error traffic were captured. The committed report does not contain
live artifacts; the ignored JSON/JSONL files contain the sanitized run
evidence.

### CAP

Advertised by CAP LS:

```text
account-notify account-tag away-notify batch cap-notify chghost
draft/oper-tag echo-message extended-join extended-monitor
inspircd.org/poison inspircd.org/stats-tags invite-notify labeled-response
message-tags multi-prefix no-implicit-names
sasl=ANONYMOUS,EXTERNAL,PLAIN server-time setname standard-replies
sts=duration=31557600,preload userhost-in-names
```

ACKed and enabled:

```text
account-notify account-tag away-notify batch echo-message extended-join
labeled-response message-tags multi-prefix server-time
```

No requested capability was NAKed. `draft/chathistory` and
`draft/event-playback` were not advertised and therefore were not requested.
The profile intentionally contains no expected-capability claim; this live
CAP state is authoritative.

Relevant 005 ISUPPORT included:

```text
CASEMAPPING=ascii
NETWORK=InspIRCd Testnet
CHANTYPES=#
CHANNELLEN=60
NICKLEN=30
USERLEN=10
LINELEN=512
PREFIX=(qaohv)~&@%+
TARGMAX=ACCEPT:5,JOIN:5,KICK:5,NAMES:5,NOTICE:5,PART:5,PRIVMSG:5,TAGMSG:5,WHOIS:5
TOPICLEN=330
UTF8ONLY
SAFELIST
SAFERATE
SECURELIST=0
REMOVE
```

`CHATHISTORY` and `MSGREFTYPES` were absent from the observed ISUPPORT. The
feature model's `Timestamp`/`MessageId` fallback reference list is not reported
as server advertisement.

| Capability/policy | Observed result |
| --- | --- |
| `message-tags` | Enabled |
| `echo-message` | Enabled |
| `msgid` | Observed on live events; not a CAP token |
| `server-time` | Enabled |
| `account-tag` | Enabled |
| `batch` | Enabled |
| `labeled-response` | Enabled |
| `draft/chathistory` | Unsupported by this server |
| `draft/event-playback` | Unsupported by this server |
| `CLIENTTAGDENY` | Absent |

## Channel relationship flow

Clients A and B joined one randomized temporary channel. A sent one ordinary
parent through `WorkspaceActionRouter`; B received it with the canonical
server msgid `597~1789175860~59` in the fresh result. The ID is opaque and was
used byte-for-byte as the relationship key. InspIRCd's observed IDs have the
shape `digits~digits~counter`; no syntax, UUID, numeric, ordering, or case
assumption was added.

B replied through the normal Phase 25 application send path. One outbound
`PRIVMSG` carried exactly `+reply=<parent-msgid>`, InspIRCd relayed it to A,
and A resolved the parent. The reply received a canonical child msgid.
`echo-message` projected one B row; it did not duplicate the row.

B reacted to the same parent with `👍` through the normal Phase 26 path. One
outbound `TAGMSG` carried exactly `+reply=<parent-msgid>` and
`+draft/react=👍`. InspIRCd relayed the tags to A, with server-added `time`,
`msgid`, and `inspircd.org/echo` metadata. No blank transcript row was
created, the parent displayed one attributed reaction for B, and B's echo did
not double-count it.

B then removed the reaction using exactly `+reply=<parent-msgid>` and
`+draft/unreact=👍`. The relay removed B's actor/value state, removed the empty
group, created no blank row, and did not duplicate the echo. The durable JSONL
store retained the relationship events.

Two-actor aggregation passed: both clients produced `👍 2`; A's unreaction
left B at `1`; B's unreaction removed the group. Multiple values also passed:
`👍` and `😂` coexisted with independent counts, removing one preserved the
other, and the deterministic first-known ordering remained stable despite
server tag-order changes.

The harmless namespaced control tag `+nexirc/phase29-test` was sent once and
was relayed. This demonstrates generic permitted client-only tag relay; it is
not exposed as a nexIRC feature. `CLIENTTAGDENY` was absent, so no remote policy
variant was attempted.

## Direct query flow

The same two clients repeated the minimal relationship flow as a private
message query. The canonical DM parent, reply, reaction, and unreaction all
routed to the correct `PrivateConversation:<folded-nickname>` identity. The
channel parent did not leak into the query and no unrelated query received the
events. Reply search found the durable DM child and
`NavigateToHistorySearchResultAsync` reopened the correct query context.

## Reconnect and history

Client B disconnected and reconnected through the normal session manager,
renegotiated CAP, rejoined the temporary channel, and sent one new `🎉`
reaction. Exactly one current-session operation was sent and relayed. Stored
relationship state remained available after reconnect. Session-generation and
stale-callback fencing remain covered by the deterministic Networking tests;
the public test did not inject an artificial stale callback into an old socket.

The testnet did not advertise `draft/chathistory` or `draft/event-playback`.
The bounded history scenario is therefore `Unsupported`, not `Failed`; no
CHATHISTORY command was sent. Historical reaction replay was consequently not
available from this server. The conservative nexIRC policy reconstructs only
events actually supplied by a server and does not invent history or reaction
counts.

The safe missing-parent attempt was not naturally reproducible on the shared
testnet. It is recorded as `Live reproduction unavailable`; deterministic
Phase 25/26 recovery and request-coalescing tests remain authoritative. No
artificial public-network manipulation was performed.

## Ergo comparison

The Phase 27 Ergo artifact and report remain valid. The comparison is semantic;
raw tag ordering is intentionally excluded.

| Property | Ergo | InspIRCd testnet |
| --- | --- | --- |
| Server/version | `Ergo / ergo-v2.19.1` | `InspIRCd / InspIRCd-5` |
| `message-tags` | Enabled | Enabled |
| `echo-message` | Enabled | Enabled |
| `msgid` | Observed, opaque | Observed, opaque `597~...~...` form |
| `server-time` | Enabled | Enabled |
| `account-tag` | Enabled | Enabled |
| `batch` | Enabled | Enabled |
| `labeled-response` | Enabled | Enabled |
| CHATHISTORY | Enabled; `CHATHISTORY=1000` | Unsupported; absent |
| Event playback | Enabled | Unsupported; absent |
| `CLIENTTAGDENY` | Absent | Absent |
| Unknown client tag relay | Passed | Passed |
| Reply relay | Passed | Passed |
| Reaction relay | Passed | Passed |
| Unreaction relay | Passed | Passed |
| Echo deduplication | Passed | Passed |
| Query relationships | Passed | Passed |
| Reconnect | Passed | Passed |
| Historical reactions | Observed | Unsupported by server |

The important cross-server difference was not tag order. InspIRCd returned
server-added tags in a different order and used a different opaque msgid
format, while omitting Ergo's history capabilities. The keyed parser,
capability gating, durable relationship state, and conservative history policy
handled these differences without a production defect.

## Explicit Ergo-assumption audit

Production code was searched for Ergo branding, Ergo-formatted IDs, UUID/GUID
identity assumptions, tag ordering, fixed CAP ordering, required account/time
tags, required CHATHISTORY/event playback, archived reaction assumptions,
echo-message dependence, and implicit absence of `CLIENTTAGDENY`.

The remaining Ergo references are limited to the intentional server identity
detector/profile catalog and the Ergo test profile. Relationship parsing,
projection, sending, query routing, durable state, reconnect, and history code
contain no Ergo-specific branch. The only InspIRCd-specific identity is the
test harness profile name; there is no `if (server == "InspIRCd")` production
behavior.

## Defects, repairs, and fixtures

No production cross-server defect was found, so no production repair was made.
Phase 29 made only interoperability-tool/reporting changes:

- added the data-only `inspircd-testnet` profile and bounded TLS runner;
- captured `001`, ordinary `VERSION`/`351`, CAP/ISUPPORT, and sanitized
  version/welcome evidence;
- made the shared harness use Phase 29 markers and classify its successful
  extended live run as Outcome A;
- corrected a harness-only DM reaction evidence flag;
- generated semantic Ergo/InspIRCd comparison JSON;
- added four deterministic `Phase29ProtocolTests` for InspIRCd's opaque
  tilde-separated msgids, server-added tags, trailing TAGMSG target, unreact,
  and tag-order independence.

The deterministic fixtures normalize volatile nicknames, timestamps, and IDs;
the ignored live artifacts retain the exact fresh run evidence.

## Regression evidence

After the Phase 29 changes:

- full Core: `118/118` passed;
- full Networking: `46/46` passed;
- focused Phase 25–29 Core: `20/20` passed;
- focused Phase 25/26 Networking: `4/4` passed;
- focused Phase 25/26 plus history/query/navigation Application: `40/40` passed;
- full Application: `180 passed, 1 skipped` (the existing opt-in Phase 1O benchmark);
- isolated Phase 1R: `1/1` passed;
- Debug solution build: passed, zero warnings/errors;
- Release solution build: passed, zero warnings/errors;
- WPF `message-reply` smoke: passed;
- WPF `message-reaction` smoke: passed;
- `git diff --check`: clean.

The live Phase 29 run itself passed all required live scenarios: canonical
parent, reply relay, reaction relay, unreaction, two-actor aggregation,
multiple values, unknown-tag relay, query relationships, reconnect,
post-reconnect reaction, durable reopen, and cleanup. CHATHISTORY was recorded
as `Unsupported`; missing-parent live reproduction was unavailable.

## Evidence and cleanup

Ignored artifacts from the fresh run:

- [inspircd-testnet-result.json](../artifacts/phase29/inspircd-testnet-result.json)
- [inspircd-testnet-transcript.jsonl](../artifacts/phase29/inspircd-testnet-transcript.jsonl)
- [server-comparison.json](../artifacts/phase29/server-comparison.json)

The transcript is sanitized, relevant-only JSONL. It redacts authentication
material and IPv4 literals; it contains no credentials or unrelated traffic.
Both temporary clients sent PART where joined and completed disposal/QUIT;
the result records `PartSent=true`, `QuitOrDisposeCompleted=true`, and
`ClientsDisposed=true`.

The Phase 28 local InspIRCd 4 profiles were retained unchanged. Their prepared
variance cases—no echo, no msgid, no message-tags, restricted/disabled
client-only tags, and no server-time—remain unexecuted controlled future work.

## Remaining limitations and recommended Phase 30

Remote `CLIENTTAGDENY` policy changes, no-echo/no-msgid capability degradation,
malformed protocol tests, resource/flood tests, and controlled alternate
history/event-playback profiles remain local-only by design. The public
testnet also cannot safely provide a reproducible missing-parent or
CHATHISTORY/event-playback matrix.

Recommended Phase 30: run the retained local InspIRCd 4 matrix from a portable,
already-approved runtime; compare its controlled capability and
`CLIENTTAGDENY` profiles with the two live implementations; and, if a
portable history-capable InspIRCd environment is available, add bounded
history/event-playback reconstruction coverage without changing the shared
relationship contract.
