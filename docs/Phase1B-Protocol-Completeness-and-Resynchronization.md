# nexIRC 5 Phase 1B — Protocol Completeness and Resynchronization

**Status:** complete in the Phase 1B local commit
**Baseline:** Phase 1A at `de4843c`
**Scope:** protocol/session correctness, adaptive channel state, authentication, and deterministic reconnect evidence

## Desired and observed state

`ServerSessionSnapshot` separates the desired primary nickname, desired
channels, requested capabilities, and SASL policy from state learned from the
current connection. Current nickname, registration, enabled capabilities,
channel membership, topics, modes, member status, MOTD, and query messages are
observations tied to a connection generation.

When a connection ends, the session invalidates all connection-owned
observations: channels become stale and are cleared of members, topics, and
modes; registration and enabled capabilities are cleared; authentication is
no longer current. Desired channels and the primary nickname remain available
for the next connection. A new generation must establish fresh evidence.

## Connection epochs and stale events

Each transport lifetime receives a monotonically increasing connection
generation and an epoch identity. Inbound frames, parse errors, semantic
events, outbound diagnostics, and optional push callbacks are accepted only
from the current epoch. A delayed inbound line, transport failure, or
disconnect callback from an earlier transport cannot mutate or fail the
current connection. The fake transport exposes deterministic callback
injection for this regression contract.

## CAP lifecycle

The capability model keeps separate available, requested, and enabled sets.
It supports multiline `CAP LS`, capability values, `ACK`, `NAK`, `NEW`, `DEL`,
unknown capability names, arbitrary ordering, and reset on every new
connection. CAP completion may be held by a completion gate while SASL runs;
the session sends `CAP END` only after authentication succeeds or an optional
policy has explicitly chosen to continue.

The model is name-agnostic and is suitable for future IRCv3 capabilities such
as account-notify, away-notify, batch, chghost, echo-message, extended-join,
invite-notify, labeled-response, message-tags, monitor, multi-prefix,
server-time, setname, and userhost-in-names.

## SASL lifecycle and security

SASL policy is `Disabled`, `Optional`, `Preferred`, or `Required`. A
non-disabled policy requests `sasl` automatically, selects a configured
mechanism accepted by the advertised SASL value, sends `AUTHENTICATE PLAIN`,
waits for the server challenge, and sends the Base64 PLAIN response. Response
chunks are at most 400 bytes; an exact multiple is terminated with
`AUTHENTICATE +`. Numerics 900/903/907 represent success, while
904–906/908 represent failure.

Credentials are supplied by the host through a disposable
`ISaslCredentialProvider` contract. The session never stores credentials in a
snapshot. Credential buffers are disposed/zeroed after the exchange and
provider/mechanism failures use fixed diagnostic text. Required SASL failure or
unavailability prevents registration; optional and preferred policies can
continue without authentication.

`PASS` and `AUTHENTICATE` payloads are redacted before outbound diagnostic
events and transcript persistence. The transcript is lossless for ordinary
protocol bytes, with this deliberate security exception: sensitive
authentication payloads are replaced by structural redaction markers so a
password or reconstructable credential blob is never saved or printed.

## IRC identity and membership

`IrcCaseMappingComparer` centrally implements `ascii`, `rfc1459`, and
`strict-rfc1459`. RFC1459 folds the punctuation pairs `[]/{}`, `\\/|`, and
`^/~`; strict-rfc1459 excludes the last pair; ASCII folds only A–Z. Channel,
member, query, desired-channel, and member-mode lookup paths use the active
mapping and safely rebuild their keys if `CASEMAPPING` changes.

JOIN, PART, QUIT, KICK, and NICK update only the affected observed state.
Remote nickname changes update every observed channel and preserve member
status. Local nickname changes update the session nickname and all channel
references. A local KICK clears observed membership but preserves desired
channel intent. Explicit client PART removes desired intent; no aggressive
automatic kick rejoin is enabled.

Registration collision handling accepts an ordered primary-plus-fallback
candidate set and handles common 433/436/437 rejection numerics without
inventing random protocol behavior.

## Adaptive channel modes and ISUPPORT

`PREFIX` is parsed into arbitrary status ranks, including owner/admin/operator/
half-op/voice forms such as `(qaohv)~&@%+`. `CHANMODES` drives list,
always-parameter, set-only-parameter, and flag modes. Multiple operations,
add/remove operations, list-mode parameters, member-prefix modes, and unknown
mode letters preserve argument alignment and state safety.

Typed ISUPPORT modeling covers the Phase 1B inputs used by session code:
`CASEMAPPING`, `CHANMODES`, `CHANTYPES`, `NETWORK`, `PREFIX`, `STATUSMSG`,
`MODES`, `NICKLEN`, `CHANNELLEN`, `TOPICLEN`, `KICKLEN`, `AWAYLEN`, `MONITOR`,
and `TARGMAX`; unknown tokens remain preserved and harmless.

## Typed events and resynchronization

The UI-neutral layer exposes typed MOTD, LIST, WHO, WHOIS, NAMES, topic, KICK,
MODE, registration, capability, SASL, nickname, membership, and channel
synchronization events. Raw lines, parsed messages, parse errors, unknown
commands, and unknown numerics remain available for diagnostics and future
extensions.

After registration the session replays desired channels with `JOIN`. Each
self JOIN schedules one bounded per-generation sequence:

```text
NAMES <channel>
TOPIC <channel>
WHO <channel>
```

NAMES continuation replies replace stale membership on the first `353`, then
accumulate until `366`. WHO enriches existing members without discarding
prefix state. Fresh topics, modes, members, and WHO data from generation B
replace the invalidated observations from generation A; old identities never
remain authoritative.

## Transcript replay and headless diagnostics

JSONL transcript entries retain timestamp, direction, generation, decoded line,
and raw-byte framing for ordinary traffic. Authentication and PASS payloads
are redacted as described above. The headless harness replays both JSONL and
raw framed transcripts, reports parse/framing errors and incomplete lines,
and prints a bounded summary containing epoch, registration, authentication,
current nickname, CAP available/requested/enabled sets, desired and observed
channels, topics, modes, member counts, and status counts.

## Testing strategy

All authoritative tests are offline and deterministic. Scenario coverage
includes multiline CAP and renegotiation, SASL PLAIN success/failure/policy
behavior, secret redaction, RFC1459 mapping differences, collision fallback,
membership lifecycle, adaptive modes, stale transport callbacks, explicit
desired-versus-observed invalidation, full two-generation SASL reconnect
resynchronization, transcript round trips, and bounded arbitrary-input
parser/framer fuzzing.

## Remaining limitations

Phase 1B does not implement UI, multi-network orchestration, scripting, DCC,
database persistence, plugins, bouncer integration, or configurable automatic
kick rejoin. SASL PLAIN is the only built-in mechanism; the host-owned
mechanism contract is intentionally extensible. CAP `NEW`/`DEL` is modeled and
reported, but dynamic post-registration capability policy is left to a later
phase. Live IRC smoke testing requires external network access and credentials
and is not part of the authoritative suite.
