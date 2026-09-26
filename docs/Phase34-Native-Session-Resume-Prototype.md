# Phase 34 — Native Session Resume Prototype

## 1. Goal

Phase 34 prototypes stronger inbound continuity for a nexIRC-aware server while
keeping the existing IRC connection lifecycle intact. The protocol resumes a
logical session, replays server events after an authoritative boundary, and
reports `StrongReplay` only after an explicit completion marker has crossed the
existing synchronization barrier.

The implementation is an in-process deterministic fixture and client protocol
prototype. It is not a production server daemon.

## 2. Capability and version

The experimental capability is `nexirc/resume=1`; the normalized capability
name is `nexirc/resume`. It is requested through the normal IRCv3 `CAP LS` /
`CAP REQ` flow. The capability is optional and is only considered usable when
the current connection generation both enables it and has a retained logical
resume session.

Version 1 places the protocol version in the CAP value. The client accepts an
empty value on `CAP ACK` after a server advertised version 1, but does not
interpret any other version as usable.

## 3. Distinct identities

The prototype keeps these identities separate:

| Identity | Meaning |
| --- | --- |
| IRC connection generation | A client transport/registration attempt, fenced by `ServerSession` epoch ownership. |
| Resumable logical session | A server-issued opaque token retained in session memory across transport loss. |
| IRC `msgid` | Canonical identity of one IRC message/event where supplied. |
| Authoritative replay boundary | The opaque ordered position in the server event stream. |
| Recovery strategy | The mechanism selected for one synchronizing generation. |
| Recovery result/evidence | The typed quality claim published to the existing continuity barrier. |

The token and boundary are never derived from a `msgid`, timestamp, message
text, or local durable row number.

## 4. Token security model

The fixture uses deterministic `fixture-resume-token` values for repeatability.
Production tokens should be unpredictable, account-bound, network/server-bound,
expiring, invalidatable, replay-resistant, and rotated or otherwise retired
according to the server's session policy. Resume must be sent only over TLS in
production, and raw tokens must not be written to logs, transcripts, metrics,
or exception text.

The client performs no heavyweight cryptography. Diagnostics use a short
SHA-256 fingerprint for correlation. Version 1 keeps the token in process
memory only; it is not persisted in durable chat history and is lost when the
application exits.

## 5. Replay boundary model

Boundaries are opaque values such as `resume-0`, `resume-1`, and `resume-2` in
the fixture. The client never parses their apparent numeric suffix. Each replay
event carries:

* `resume-seq`: the event's authoritative position;
* `resume-prev`: the position immediately preceding it; and
* the ordinary IRC `msgid` tag when the event has one.

The client accepts a new event only when `resume-prev` equals its committed
boundary. A repeated sequence is accepted only for the same canonical `msgid`
and is suppressed. An out-of-order sequence, conflicting duplicate, unknown
anchor, or unsafe value fails native recovery without a false strong claim.

## 6. Initial session establishment

After registration, a native fixture sends:

```text
:server NEXIRC SESSION <opaque-token> <authoritative-boundary>
```

The client stores the token and boundary in memory. Initial registration still
produces `NotRequired`; merely having a future resumable session is not a
recovery event.

## 7. Reconnect handshake

For a later generation, the selected `NexIrcResume` strategy sends:

```text
NEXIRC RESUME <opaque-token> <last-committed-boundary>
```

The server responds with one of:

```text
:server NEXIRC RESUME ACCEPT <accepted-boundary>
:server NEXIRC RESUME REJECT <reason>
:server NEXIRC RESUME UNSUPPORTED
:server NEXIRC RESUME NEW
```

The accepted boundary must equal the requested boundary. Rejection reasons in
the fixture include unknown token, expired token, account mismatch, boundary
too old, invalidated session, and server restart.

## 8. Replay framing

Native replay uses an IRC `BATCH` with a distinct type so it is not confused
with ordinary `CHATHISTORY` semantics:

```text
:server BATCH +resume1 nexirc/resume Channel:#room
@batch=resume1;msgid=m2;resume-seq=resume-2;resume-prev=resume-1 :alice PRIVMSG #room :text
:server BATCH -resume1
```

The batch parameter is the logical conversation key. `PRIVMSG`, `NOTICE`,
`TAGMSG` reactions/unreactions, reply metadata, and the existing canonical
relationship machinery use the normal semantic pipeline. No resume-specific
relationship state is introduced.

## 9. Completion barrier

After all replay batches close, the server sends:

```text
:server NEXIRC RESUME COMPLETE <final-boundary>
```

The client accepts completion only when the response belongs to the current
connection generation, the replay was accepted, all native batches are closed,
and the final boundary equals the boundary reached by canonical replay
acceptance. Only then does `NexIrcResumeRecoveryStrategy` return recovered
`StrongReplay` evidence. Protocol parsing never sets `Synchronized` directly;
the existing continuity owner publishes the result through the existing
synchronization barrier.

## 10. Live traffic during replay

The deterministic fixture holds live events that arrive during a native replay
and releases them after `RESUME COMPLETE`. Held events retain their own linked
authoritative sequence values, so the client commits them after the replay
boundary without depending on arrival timing. A production server may instead
interleave replay and live traffic if both use one authoritative sequence
stream; version 1 prototypes the held-live architecture.

## 11. Strategy selection and fallback

For a required recovery generation, precedence is:

1. `NexIrcResume` when the current generation enabled `nexirc/resume` and a
   session-memory logical session exists;
2. `Ircv3ChatHistory` when usable IRCv3 history is negotiated;
3. `BestEffortNoHistory` otherwise.

Native rejection before replay is a bounded, explicit fallback point. The
manager invokes at most one generic fallback strategy in the same generation;
there is no recursive strategy invocation. A rejection after replay has begun,
an invalid sequence, malformed completion, or disconnect during replay is not
treated as fallback-safe because doing so could duplicate or omit canonical
state.

Capability removal therefore selects generic recovery, regardless of a stored
token. Capability addition without a prior token selects generic recovery for
the current interruption and establishes a native session only for future
interruptions.

## 12. Boundary persistence and advancement

The minimum session-memory state is the opaque token, the committed opaque
boundary, and the server binding represented by the owning `ServerSession`
endpoint. Account binding is a server contract and is not inferred by the
client. No token is written to durable JSONL conversation history.

The boundary advances only after the event has produced a canonical semantic
event in the serialized protocol state pipeline. Network receipt, parsing, or
the arrival of a `resume-seq` tag alone cannot advance it. Live and replay
events use the same commit rule. A replayed boundary event is allowed at least
once and is deduplicated by sequence plus canonical `msgid`.

## 13. Token rotation and concurrent resumes

Version 1 retains the same token after successful resume. Token rotation is
explicitly deferred so the completion and invalidation rules stay bounded.

The single-owner policy is also bounded: a server may reject a competing resume
attempt rather than create multi-device synchronization. The fixture exposes a
deterministic concurrent-rejection profile. Production policy may choose
replacement of the old connection, but that is a future protocol version.

## 14. Failure handling

The fixture covers malformed token responses, disconnect during replay,
malformed completion, out-of-order sequences, conflicting sequence reuse, and
unknown anchors. Generation fencing makes old transport callbacks and stale
completion messages inert. Cancellation and shutdown cancel the attempt without
owning or changing continuity lifecycle state.

An expired token, lost server state after restart, or invalidated session is a
typed `Rejected` native result. Before replay it is fallback-safe; it is never
reported as native success. A partial replay is `Partial` or `Failed` and does
not casually restart through CHATHISTORY.

## 15. Recovery evidence mapping

Native `StrongReplay` requires all of the following:

* the same logical session token was accepted;
* the requested prior boundary was recognized and echoed;
* authoritative replay events were ordered and accepted through the canonical
  semantic pipeline;
* duplicate delivery was suppressed without a second canonical mutation;
* all replay batches closed;
* an explicit completion boundary matched the committed boundary; and
* every operation belonged to the current connection generation.

Missing any of these requirements prevents a native strong claim.

## 16. Channel, query, and relationship behavior

Native batches carry a logical conversation key, so the existing channel/query
routing remains in charge. Channel objects and query identities are reused by
the normal application projection. Replayed `PRIVMSG` and `NOTICE` entries,
reply metadata, reactions, unreactions, multiple reaction values, and multiple
actors use the existing canonical/durable relationship code. A parent included
before a child resolves normally; a parent retained outside the replay window
remains bounded unresolved after explicit completion.

The prototype deliberately does not replay every IRC state event. A future
server contract may include JOIN/PART, NICK, ACCOUNT, AWAY, topic, and mode
state, but conversation continuity is the Phase 34 boundary.

## 17. Diagnostics

The bounded continuity diagnostics now identify native strategy selection,
request start/completion/failure, redacted session fingerprint, requested and
final boundaries, replay counts, duplicate suppression, and fallback reason.
Raw native tokens are redacted from inbound/outbound diagnostic transcript
lines. The deterministic fixture's transport capture intentionally retains
wire values for protocol assertions and is test infrastructure only.

## 18. Deterministic fixture and tests

`DeterministicServerHistoryFixture` now supports native capability advertising,
session issuance, resume requests, rejection profiles, linked replay sequence
tags, explicit completion, at-least-once boundary replay, held live traffic,
state loss, and failure injection. `nexirc/resume` is a separate BATCH type
from `chathistory`.

Focused tests cover initial negotiation, session identity, transcript lines,
selection precedence, strong evidence, fallback-safe rejection, live boundary
commit, replay ordering, duplicate suppression, held live traffic, expired and
restart rejection, out-of-order failure, and unchanged lifecycle enum values.

## 19. Production defects found and repaired

The existing state store recognized only `chathistory` as a historical batch.
That would have either discarded native replay or treated it as live state. It
was repaired to accept the distinct `nexirc/resume` batch through the same
historical semantic pipeline. The diagnostic redaction helper also previously
had no native-token rule; it now fingerprints native identities without
emitting raw secrets.

## 20. Backward compatibility

Ordinary IRC servers do not advertise `nexirc/resume`, so the client sends no
native resume command and continues to use CHATHISTORY or best-effort recovery.
Existing IRCv3 history and no-history profiles remain on their original code
paths. The native capability is additive and server-name independent.

## 21. Sequence diagram

```text
Client                                      Native fixture server
  |--- CAP LS 302 ------------------------------->|
  |<-- CAP * LS ... nexirc/resume=1 -------------|
  |--- CAP REQ :nexirc/resume ... -------------->|
  |<-- CAP * ACK :nexirc/resume ------------------|
  |--- CAP END --------------------------------->|
  |<-- 001 / SESSION token boundary --------------|
  |                                               |
  |                 transport loss                |
  |                                               |
  |--- register on generation N+1 --------------->|
  |--- NEXIRC RESUME token boundary ------------->|
  |<-- NEXIRC RESUME ACCEPT boundary -------------|
  |<-- BATCH + nexirc/resume ---------------------|
  |<-- ordered events with seq/prev --------------|
  |<-- BATCH - -----------------------------------|
  |<-- NEXIRC RESUME COMPLETE final-boundary ------|
  |    canonical commit + recovery result         |
  |    existing barrier -> Synchronized           |
```

## 22. Remaining limitations

Resume state is process-memory-only, version 1 does not rotate tokens, and
the fixture does not model authenticated account binding or durable server
retention. The client does not implement outbound guaranteed delivery,
acknowledgements, offline send, or exactly-once outbound semantics. Production
deployment still requires TLS enforcement, server-side token lifecycle and
retention policy, account/network binding, audit-safe secret handling, and an
interoperability specification.

## 23. Production nexIRC Server implications

A production server can implement the protocol as an IRC extension layered on
the normal registration and capability machinery. It should maintain a bounded
logical-session record keyed by an unpredictable token, bind that record to the
authenticated account and network, retain an authoritative event log with
explicit replay positions, serialize one owner per logical session, and emit a
completion boundary only after replay is complete. The server must define
retention failure and restart behavior explicitly; capability support alone
must not imply that every prior token remains resumable.

## 24. Lifecycle proof

The lifecycle enum before and after Phase 34 is:

`Disconnected`, `Interrupted`, `Recovering`, `Synchronizing`, `Synchronized`,
`Terminal`.

Native resume runs inside the existing path:

`Interrupted` → `Recovering` → registration/CAP negotiation → `Synchronizing`
→ `NexIrcResumeRecoveryStrategy` → typed recovery result/evidence →
`Synchronized`.

No lifecycle state or lifecycle branch was added for native resume.
