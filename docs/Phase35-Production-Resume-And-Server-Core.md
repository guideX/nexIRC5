# Phase 35 — Production Resume and Minimal Server Core

Phase 35 moves `nexirc/resume` v1 from the Phase 34 deterministic fixture to a
real loopback nexIRC Server implementation. The client wire contract remains
compatible with Phase 34. The server implementation is intentionally narrow:
it owns registration, resumable logical sessions, bounded in-memory replay,
and the conversation message path, not the complete IRC daemon surface.

## Final v1 contract

The optional capability is still `nexirc/resume=1`. It is negotiated through
ordinary IRCv3 `CAP LS 302`, `CAP REQ`, `CAP ACK`, and `CAP END` messages.
Native resume is additive; a client that does not request it stays on ordinary
IRC behavior.

The server issues a session only after compatible registration:

```text
:server NEXIRC SESSION <opaque-token> <boundary>
```

A later registered connection requests the retained logical session with:

```text
NEXIRC RESUME <opaque-token> <boundary>
```

An accepted request echoes the requested boundary. Replay is framed in one or
more `nexirc/resume` batches. Each replayed message preserves its original
`msgid` and carries server-owned predecessor metadata:

```text
:server NEXIRC RESUME ACCEPT s0
:server BATCH +resume1 nexirc/resume #room
@batch=resume1;msgid=m1;resume-seq=s1;resume-prev=s0 :alice PRIVMSG #room :hello
:server BATCH -resume1
:server NEXIRC RESUME COMPLETE s1
```

`sN` values are opaque on the wire. They are monotonically allocated integers
inside one server logical session, formatted as `s` followed by the decimal
position. The client never parses the suffix or derives a boundary from a
`msgid`.

The server uses these rejection strings: `UNKNOWN_TOKEN`, `EXPIRED_TOKEN`,
`ACCOUNT_MISMATCH`, `BOUNDARY_TOO_OLD`, `SESSION_INVALIDATED`, `TOO_LARGE`,
and `MALFORMED`. `TOO_LARGE` is a backward-compatible additive v1 reason:
older Phase 34 clients classify it as an early malformed rejection and use
their safe pre-replay fallback; the Phase 35 client exposes the typed
`ReplayTooLarge` reason.

## Review decisions

No command, capability, version, boundary, batch, or completion change was
made from Phase 34. The only additive wire vocabulary is the pre-replay
`TOO_LARGE` rejection for an explicitly bounded replay request. Completion
still means that all replay batches have closed and the client has committed
exactly the replay tip. The server then releases held live events, each linked
from that completion tip. This keeps Phase 34 client ordering and generation
fencing intact while making the live-event bound explicit.

Token rotation is deliberately not enabled in v1. The Phase 34 client accepts
`NEXIRC SESSION` as initial session establishment but does not atomically
replace its stored token during an active resume attempt. Partial rotation
would create a stale-token race, so the server retains the same token until a
versioned rotation contract is specified. Phase 36 should add a negotiated
replacement-token field with an atomic client commit point.

## Distinct identities

These values are deliberately not interchangeable:

| Identity | Phase 35 meaning |
| --- | --- |
| Physical network connection | One TCP transport, owned by one server connection object. |
| IRC registration | The `CAP`/`NICK`/`USER` exchange ending in numeric `001`. |
| Logical resumable session | A server-owned in-memory record that survives transport loss. |
| Resume token | A random 256-bit bearer secret, looked up by a SHA-256 hash. |
| Canonical IRC `msgid` | Stable identity of one message or relationship event. |
| Authoritative replay sequence | Server-owned `sN` position and predecessor link. |
| Retained replay window | Bounded event ring associated with one logical session. |
| Recovery strategy | Client selection: native resume, CHATHISTORY, or no-history. |
| Recovery result/evidence | Client lifecycle-quality result; strong native evidence requires explicit completion. |

## Server architecture

The server repository is `D:\dev\nexIRC\nexIRCServer`. Its layers are:

1. `ServerConnection` owns one TCP transport, registration fields, negotiated
   capabilities, and the cancellation/inert fence.
2. `ResumableSessionStore` owns hashed-token lookup, expiry, invalidation,
   account binding, ownership takeover, and server-generation identity.
3. `LogicalSession` owns the distinction between the current transport owner
   and the retained logical identity.
4. `ReplayStore` owns authoritative allocation, bounded retention, replay-after,
   and retention-floor checks.
5. `NexIrcServer` owns capability negotiation, narrow IRC routing, replay
   execution, held-live release, and loopback listener lifecycle.

The server stores no raw passwords and does not put raw resume tokens in its
diagnostics. Diagnostics use the first eight bytes of a SHA-256 token digest,
rendered as a 16-character lowercase hexadecimal fingerprint. The client
transcript redaction path applies the same fingerprinting to both inbound
`SESSION` and outbound `RESUME` lines.

## Session and replay policy

The memory-only session record contains a GUID record identity, a SHA-256 token
hash, token fingerprint, account identity, server-generation GUID, owner
connection ID, creation/activity/expiry times, lifecycle state, replay tip,
retention floor, and bounded replay/held-live storage. It never stores a raw
token or password.

The default limits are 1,024 retained events, 1 MiB retained serialized
estimate, 256 held live events, and 512 events in one replay request. Writes
also have a bounded timeout and a 512-byte IRC line limit. A held-live or
retained-storage overflow is rejected and diagnosed; it never grows an
unbounded queue. Tests use `ManualServerClock`, not wall-clock sleeps, for
expiry.

The retention floor is the predecessor of the oldest retained event. A
boundary older than that floor returns `BOUNDARY_TOO_OLD`; it never receives a
completion marker and can never become strong replay evidence. Unknown,
future, or malformed boundaries are rejected before replay.

The minimal account binding is explicit: the default resolver maps the
registered USER identity to an `acct:<username>` account identity. This is a
narrow deterministic identity abstraction, not nickname equality and not a
replacement for SASL. A token accepted for `acct:alice` is rejected for
`acct:bob`.

The first native registration becomes the owner of its logical session. A
successful resume atomically replaces the owner; the old connection is marked
inert and its transport is closed. Its late callbacks cannot detach or mutate
the new owner. A reconnecting native connection receives a provisional new
session announcement, but live broadcasts are withheld from that provisional
record until the connection either resumes the prior session or becomes a
new active native session.

## Replay and live ordering

The server appends an event before delivering it. The event contains its
sequence, predecessor, target, sender/account, command, tags, body, canonical
`msgid`, and relationship metadata. Replay uses the retained event as-is, so
`msgid`, `+reply`, `+draft/react`, and `+draft/unreact` values are not
regenerated.

During replay, new live payloads enter a bounded held-live queue without
being assigned a sequence. The server closes replay batches, sends
`NEXIRC RESUME COMPLETE` for the actual replay tip, then assigns and releases
held payloads in order. Therefore a completion boundary is true at the
completion point, and a live event generated during replay is linked after it.
The loopback test observes `s2` completion followed by held event `s3`.

Channel targets (`#room`) and direct/query targets (`nex`) remain ordinary IRC
targets. The batch parameter identifies the logical conversation; replayed
messages retain their normal target and sender semantics. No resume-specific
relationship model was introduced.

## Security review

Mitigations include cryptographic random token generation, SHA-256 server-side
lookup, constant-time hash comparison, account binding, bounded lifetime,
explicit invalidation, one-owner takeover fencing, bounded replay and output,
generic unknown-token behavior, and diagnostic/transcript redaction. Native
resume is advertised only when TLS is configured or an explicit loopback/test
mode opts into plaintext. The checked-in sample server is loopback-only test
mode; it is not a production TLS listener.

Remaining limitations are bearer-token theft, memory-only restart loss, no
durable token/replay atomicity, no cluster routing, no SASL implementation in
this minimal server, and no rate limiter beyond bounded writes and session
validation cost. Phase 36 should add failed-resume rate limiting, TLS listener
integration, and durable atomic session/replay persistence.

## Persistence and clustering design

Durable restart support must atomically persist the token hash (never raw token
unless protected by a dedicated secret store), account/network binding,
server/session generation, expiry, current sequence, retention floor, and
ordered replay records. A commit must make the event, sequence allocation, and
retention eviction visible together. The wire protocol need not change if the
new process restores the same logical-session record and sequence namespace.

A cluster would need a single owner lease per logical session, consistent token
lookup, ordered replay storage, one sequence allocator, and reconnect routing
to the owner or a takeover coordinator. v1's opaque token and server-defined
sequence are compatible with that design, but the current implementation is
single-process only.

## Validation and stall closure

The Phase 34 full Application run was reproduced with a 30-second hang
diagnostic. The benchmark was skipped; 167 tests passed, Phase1I failed at its
sidecar-corruption assertion, and the test host was quiet while the bounded
Phase1R endurance test was running. Phase1R passed alone in 38 seconds. The
root defect was a warm search-index cache that trusted same-length/mtime
metadata after a rapid same-length sidecar replacement. The cache now reloads
and content-validates the sidecar before reuse. The focused Phase1I repair test
passes. The same full run exposed a Phase1Z evidence-order race: generic
account evidence could be published before the extended-JOIN source-specific
evidence. Account-aware incoming query binding now accepts an evidence-source
override, so extended JOIN evidence is published as `LiveExtendedJoin` at the
binding point.

The complete Application suite was then run without the hang collector and
passed: 190 passed, 1 intentionally skipped benchmark, 191 total, in 2m05s.

## Successful resume

```text
Client                         nexIRC Server
  |--- CAP LS 302 ------------------->|
  |<-- CAP * LS ... nexirc/resume=1 --|
  |--- CAP REQ :nexirc/resume ... --->|
  |<-- CAP * ACK :nexirc/resume ------|
  |--- CAP END; NICK; USER ---------->|
  |<-- 001; NEXIRC SESSION token s0 --|
  |                                    |
  |          transport loss            |
  |                                    |
  |--- registration on TCP #2 -------->|
  |--- NEXIRC RESUME token s0 -------->|
  |<-- NEXIRC RESUME ACCEPT s0 --------|
  |<-- BATCH +... nexirc/resume -------|
  |<-- ordered s1..sN with msgids -----|
  |<-- BATCH -... ---------------------|
  |<-- NEXIRC RESUME COMPLETE sN ------|
  |    client canonical commit         |
  |<-- held live sN+1.. ---------------|
```

## Rejected resume with fallback

```text
Client                         nexIRC Server
  |--- NEXIRC RESUME token s0 -------->|
  |<-- NEXIRC RESUME REJECT TOO_OLD ---|
  |    typed rejected; no replay began |
  |--- bounded generic fallback ------->|
  |    CHATHISTORY or no-history       |
```

The client never claims `StrongReplay` after rejection, expiry, restart loss,
account mismatch, a too-old boundary, malformed ordering, incomplete batches,
or a completion mismatch.

## Recommended Phase 36

Add negotiated token rotation, durable session/replay persistence, TLS listener
support, SASL-backed account binding, resume-attempt rate limiting, and a
versioned interoperability document before exposing native resume outside
trusted loopback/testing environments.
