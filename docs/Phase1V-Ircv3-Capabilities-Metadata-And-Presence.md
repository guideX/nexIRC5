# nexIRC 5 Phase 1V — IRCv3 Capabilities, Metadata, and Presence

Phase 1V adds a bounded, session-owned IRCv3 substrate for modern message
identity, server timestamps, presence, and membership metadata. The phase
keeps the Phase 1U network/session, serialized-state, history-identity, and
deterministic-WPF boundaries intact.

## 1. Starting architecture

The existing path was already layered as:

```text
transport line framing → IrcMessageParser → ServerSession
    → SessionStateStore → NetworkSessionManager → WPF projection
```

`ServerSession` already owned connection epochs, CAP/SASL sequencing, and
reconnect reset. `SessionStateStore` already owned RFC1459 casemapping,
adaptive `PREFIX`, desired-channel lifecycle, and NAMES-derived members.
`IrcMessage` already retained a bounded-by-line-framing tag list and a
last-value tag lookup. Transcript/history records already had a displayed
timestamp.

## 2. CAP negotiation before Phase 1V

Before this phase, CAP used `CAP LS 302`, handled ordinary and multiline LS,
ACK/NAK, and held registration open for the existing SASL policy. Requested
strings were normalized but there was no explicit rejected/unavailable status.
CAP-less servers could leave registration waiting indefinitely, and reconnect
reset behavior was not paired with a bounded CAP fallback. CAP state was
session-owned and reset at connection-generation boundaries, but the public
snapshot did not expose the full state distinction.

## 3. Defects and limitations found

The audit found four useful repairs:

* malformed/empty tag segments needed a safe parse result and unknown escapes
  needed literal preservation;
* valid `time` tags needed one authoritative cached interpretation;
* CAP needed explicit rejected state and a timeout/`421 CAP` legacy path;
* NAMES refreshes needed an explicit request fence. The old first-`353` clear
  behavior could allow a late segment to repopulate a completed cycle, and a
  nickname-only mutation fence could cross channels.

The existing adaptive `PREFIX` grammar and generation fencing were retained,
not replaced.

## 4. Capability state architecture

`IrcCapabilityCatalog` centralizes the understood Phase 1V capability names.
`CapabilitySnapshot` now distinguishes advertised, requested, enabled,
rejected, and unavailable states through `CapabilityNegotiationStatus` and
case-normalized lookup. The live negotiator clears its state at `Start`,
`Reset`, and each new connection generation. The snapshot is exposed only
through the owning `ServerSessionSnapshot`; no global capability flags were
introduced.

`IrcCapabilityChangedEvent` is bounded local diagnostic output. It reports
available, removed, enabled, disabled, and rejected deltas without dumping
raw tags or credentials.

## 5. Capabilities requested

The application default request list is:

```text
message-tags server-time away-notify extended-join multi-prefix
account-notify labeled-response
```

SASL remains policy-driven and is added only when authentication is enabled.
`account-tag` is understood as metadata when supplied, but is not requested by
default because `account-notify` is the smaller coherent account-transition
surface for the current participant model. `batch`, `chathistory`, and
`draft/chathistory` are not requested.

## 6. CAP negotiation lifecycle

Each connection sends `CAP LS 302`, accumulates all LS segments, requests only
the configured understood names that were advertised, and handles partial ACK
or NAK results. CAP END is emitted by one centralized completion path and is
idempotent. SASL remains the only completion gate: required SASL failure is
terminal, while optional/preferred SASL can skip or fail authentication and
continue registration. Servers that return `421 CAP`, or ignore CAP until the
bounded negotiation timeout, complete without extensions and continue normal
registration. Reconnect resets capability and authentication state before the
new generation begins.

## 7. IRCv3 tag parser rules

There is one authoritative parser for:

```text
@tag=value;tag2=value2 :prefix COMMAND params :trailing
```

It retains ordered bounded tag records plus a last-value lookup. It supports
multiple tags, empty values, valueless tags, unknown tags, and the IRCv3
escapes `\:`, `\s`, `\\`, `\r`, and `\n`. Unknown escapes are preserved
literally and malformed/empty segments return a parse error without throwing
or shifting command parsing. Existing line-framer limits remain the input
bound; no per-message regular expressions were added.

## 8. Server-time behavior

The parser strictly recognizes the IRCv3 `time` tag in RFC3339 UTC/offset
forms and caches it as `IrcMessage.ServerTimestamp`. Invalid values leave the
message valid and use the local receive time fallback. The application
transcript uses server time as `TranscriptEntry.Timestamp` while retaining
the local delivery boundary as runtime-only `TranscriptEntry.ReceivedAt`.
Sequence numbers and serialized event processing continue to define delivery
ordering; a historical timestamp cannot reorder lifecycle processing.

The displayed `Timestamp` is the durable history timestamp. `ReceivedAt` is
not serialized into each JSONL record, which preserves the Phase 1T history
segmentation/performance envelope.

## 9. Extended JOIN behavior

Both ordinary `JOIN :#channel` and extended
`JOIN #channel account :Real Name` use the same typed `IrcJoinEvent`. Extended
JOIN populates account and real name; account `*` becomes null. Ordinary JOIN
never requires the capability and preserves any metadata already known.

## 10. Account metadata behavior

`ACCOUNT accountname` and `ACCOUNT *` update the existing casemapped
participant in every matching channel. Account tags on channel messages are
also merged, including clearing on `account=*`. Account metadata is never used
as an identity key; nickname, casemapping, network, and session generation
remain the identity boundary. The existing Phase 1U copy-account and copy-
identity actions therefore read current projected metadata.

## 11. Away-notify behavior

`AWAY :reason` marks a matching participant away and `AWAY` clears the state.
Away state and an optional bounded reason are retained in the member snapshot,
updated in one participant projection, and rendered as a quiet informational
line. PART, KICK, and QUIT remove the member; NICK moves the same metadata to
the new nickname. State is channel/network/session scoped and is rebuilt from
the new generation's JOIN/NAMES traffic.

## 12. Multi-prefix behavior

NAMES continues to parse prefixes through the network's advertised adaptive
`PREFIX` mapping. With multi-prefix data, all supplied modes are retained;
for example `@+Nick` maps to both operator and voice under the advertised
grammar. The WPF list remains visually compact by displaying the highest
prefix, while the full mode set stays available to authority and contextual
action evaluation.

## 13. NAMES/member synchronization

Explicit NAMES requests now mark a channel-local cycle. Repeated segments
merge by casemapped nickname without duplicate members, preserve account,
real-name, and away metadata, and use a channel/casemapping-scoped mutation
fence. Newer removal, join, metadata, nick, or mode changes are not erased by
end-of-NAMES cleanup; late `353`/`366` traffic after a completed cycle is
ignored unless a new request was explicitly started. Mutation markers are
cleared at finalization and at generation reset, keeping the fence bounded.

There is no server-side request label for ordinary NAMES, so a `353` arriving
after a newly initiated request cannot be distinguished from that request's
first segment. This is documented as a protocol limitation; generation and
serialized event fencing still reject stale connection traffic.

## 14. History implications

Server time flows through normal message presentation and logging, so channel,
query, NOTICE, CTCP ACTION, and status identity paths retain their existing
conversation keys and sequence semantics. Replay/search continues to use the
stored displayed timestamp. Timestamp alone is not a message identity key;
the existing network/profile/conversation identity remains authoritative.

## 15. Batch/history decision

Phase 1V adds only a bounded typed BATCH framing foundation. It tracks at most
32 live batches per session, bounds batch identifiers and parameters, emits
typed start/end events, and clears all framing state at generation reset. It
does not request or implement CHATHISTORY. The existing history store does not
yet have a complete playback identity/order/deduplication contract, so full
server history retrieval is deferred.

## 16. WPF presentation

Server status now shows the sorted enabled IRCv3 capability names. Participant
tooltips expose nickname, user/host, account, real name, away state/reason,
and online state. The member list remains compact and uses all prefix modes for
authority even though it displays only the highest visible prefix. Message
timestamps use the existing renderer and display authoritative server time
when available. No broad chat-renderer redesign was made.

## 17. Deterministic test coverage

New focused coverage is in:

* `tests/nexIRC.Core.Tests/Phase1VProtocolTests.cs`: capability states,
  bounded catalog, tag forms/escapes/malformed input, strict server time, and
  ordinary/extended JOIN parameter shape;
* `tests/nexIRC.Networking.Tests/Phase1VSessionTests.cs`: extended JOIN,
  away/account transitions, NAMES race fencing (including a join after a
  refresh starts), CAP-less fallback, timeout, stale-generation CAP traffic,
  and typed batch lifecycle;
* `tests/nexIRC.Application.Tests/Phase1VMetadataTests.cs`: presentation
  timestamps plus history replay/search timestamp retention;
* `src/nexIRC.Desktop/UiSmokeHarness.cs`: the semantic `ircv3-metadata`
  scenario, alongside the existing participant, contextual-actions, and
  sustained-interactivity scenarios.

The focused suites pass 14 Core, 8 Networking, and 3 Application tests. The
final closeout records the full solution totals and all Phase 1R–1U regression
results.

## 18. Live IRC findings and remaining backlog

The deterministic server tests advertise and negotiate the Phase 1V core set;
they are not evidence that a public server offers every capability. On
2026-09-07, the credential-free desktop smoke test connected to Libera and
observed/ACKed `account-notify`, `away-notify`, `extended-join`,
`labeled-response`, `message-tags`, `multi-prefix`, and `server-time`. It
completed registration, JOIN/NAMES synchronization, server-time observation,
WHOIS numerics, and the natural QUIT/disconnect sequence. The test account did
not receive a privilege prefix in NAMES, so multi-prefix was negotiated but
not observable on that participant. No live capability absence is treated as
an implementation failure.

Implemented: general CAP state/lifecycle, tags, server-time, away-notify,
extended-join, account-notify/account metadata, multi-prefix, NAMES fencing,
runtime batch framing, capability status UI, diagnostics, and focused tests.

Advertised/negotiated: only the capabilities explicitly shown by each
deterministic or live server trace. Simulated: all focused fake-transport
protocol and WPF scenarios. Deferred: CHATHISTORY/playback, full BATCH
semantics, labeled-response expansion, account-tag negotiation, echo-message,
message edits/deletes/reactions/typing/read markers, and other out-of-scope
IRCv3 extensions.

Phase 1W should build on the bounded capability registry and typed metadata
events with a playback identity contract first, then revisit CHATHISTORY or
additional IRCv3 extensions only when ordering and duplicate suppression can
be proved end to end.
