# Phase 40 — Multi-Device Logical Session Attachments

## Starting audit and design

### Repository and Phase 39 prerequisite audit

Both repositories started on `main` with clean worktrees and matched their
`origin/main` tracking refs (zero ahead/behind). The client started at
`c1112a2810b325c52238cd2039ae80a003ed9bc5`; the server started at
`99ce7233362fbbd55a2307e6aa371fad1bf2a55f`. No Phase 39 report exists in
either repository. The latest available Phase 38 client report
states that it did not separately test the ACK-written/server-not-yet-processed
boundary and calls out server-process restart and Application replay projection
as later work. The latest server tests exercise client process death against a
live server and in-process server reconstruction from a durable snapshot; they
do not kill and restart the server process.

Baseline Debug test results before Phase 40 changes:

- Client Core: 131 passed.
- Client Networking: 77 passed.
- Client Application: 209 passed, 1 benchmark skipped.
- Server: 23 passed.

The Phase 39 durability prerequisite is therefore **partially evidenced, not
fully verified**. No reproducible defect appeared in the baseline suites. The
unproven process/projection/ACK boundaries remain explicit acceptance limits;
Phase 40 must not describe them as passed without a dedicated test.

### Existing model

The server's `LogicalSession` combines canonical replay state, the only current
and pending token, one `ActiveOwnerId`, and one session-wide replay/held-live
state. `NexIrcServer` invalidates the previous connection on every accepted
resume. Client resume state is one protected record per network profile, and
the authoritative boundary is stored by that client. Nickname and SASL account
are separate; the server uses the SASL account for durable resume authorization.
The server persistence snapshot excludes physical owners and in-flight queues.

### Phase 40 design

`LogicalSession` owns the authenticated account, canonical event sequence and
IDs, retained replay window, and bounded durable attachment credential records.
Each `SessionAttachment` owns a stable random GUID, an independently rotating
resume token, a child-attachment grant, and a transient current transport
binding. The attachment GUID is an identifier, not an authenticator, and
survives reconnect/server restart as part of durable credential metadata.
Socket, process, nickname, negotiated capabilities, replay state, and output
queues remain transient.

The existing `nexirc/resume=1` flow keeps its single-attachment takeover
semantics. A separate optional `nexirc/attachments=1` capability enables
attachment creation. An attachment grant authorizes creating an additional
attachment after SASL; the server returns a new attachment ID, resume token,
and child grant. Normal resume rotates only the resume token for the presented
attachment.
Whole-session invalidation removes all attachment credentials; revoking one
attachment removes only its credential and live owner.

The authoritative event sequence remains session-global. Replay boundaries and
replay/held-live queues are per attachment. Every canonical event is persisted
once and fanned out to each eligible attachment; a replaying attachment queues
that event locally until its replay-completion marker. Physical attachment
owners are never restored after server restart. Live attachment count and
durable credential count are bounded by the same configurable per-session cap.

IRC nickname ownership and JOIN/PART remain physical-connection semantics in
the minimal server. SASL account is the security identity; attaching does not
claim that IRC has one shared nickname or channel membership across sockets.

## Implemented model

| Scope | State |
| --- | --- |
| Logical session | SASL account identity, canonical retained event stream, authoritative sequence and `msgid`s, expiry/invalidation, bounded attachment records |
| Attachment | Stable random GUID, independently rotating resume token, child-attachment grant hash, transient transport owner, connection binding, per-attachment replay and held-live queues, delivery boundary |
| Physical connection | Socket, negotiated capabilities, nickname, IRC JOIN/PART state, write gate and timeout; never restored as live after server restart |
| Client profile | Per-attachment boundary, resume token and protected child grant; optional per-connection state-store override supports independent client stores |

The initial attachment ID is the logical session GUID. Each additional
attachment receives a random GUID distinct from that ID. IDs are identifiers,
not secrets, and credential metadata persists them across reconnects and
server snapshot reconstruction. Socket handles, process IDs, nicknames,
capability state, queue contents, and physical owners remain transient.

The canonical event stream and retention window are shared. Replay position,
resume token and in-flight queues are attachment-local. A server event is
recorded once, assigned one `msgid` and sequence, and delivered to each live
attachment. Events arriving during replay are held only for that attachment.
An attachment's routine token rotation or detach does not rotate, detach, or
revoke its peers.

Nickname ownership and channel membership remain ordinary IRC connection
semantics. The SASL account, not the nickname, authorizes attachment. The
minimal server does not provide shared JOIN/PART membership across sockets;
Phase 40 does not present the attachments as one IRC connection.

## Capability and credential flow

The optional TLS capability is `nexirc/attachments=1`, advertised with
`nexirc/resume=1` when the server's SASL authenticator is configured. A client
that requests only resume retains the existing legacy takeover behavior. After
initial registration with attachment support, the server sends:

```text
NEXIRC SESSION <attachment-token> <tip>
NEXIRC SESSION ATTACHMENT <attachment-id> 1
NEXIRC SESSION KEY <primary-attachment-grant>
```

After TLS, CAP and SASL, another client sends
`NEXIRC ATTACH <attachment-grant> <saved-boundary>`. The server checks its
account binding, boundary and attachment cap, creates an independent attachment
token and child grant, replies with `NEXIRC ATTACH ACCEPT`, then uses the
existing ordered replay and `NEXIRC RESUME COMPLETE` barrier. Rejections are explicit, including
`ACCOUNT_MISMATCH`, `UNKNOWN_TOKEN`, and `ATTACHMENT_LIMIT`. A different
nickname is accepted for the same SASL account. The server-side attachment
revocation API removes one attachment credential and closes only that owner;
whole-session invalidation remains a distinct operation. Revocation is core
API functionality only; there is no wire command or device-management UI.

The chosen credential model is attachment-scoped resume tokens and
attachment-scoped child grants. Initial registration issues the primary
attachment's grant; `ATTACH ACCEPT` issues a separate grant for the new
attachment. Token rotation or revocation on device A therefore does not
invalidate device B, and revoking B removes the grant B could use to create
another attachment. Grants remain bearer secrets, so they stay protected and
bound to account/network. The server stores only hashes/fingerprints and the
client stores grant bytes protected; raw grant/token material is redacted from
transcripts. Per-session attachment and credential count defaults to four.
Current/pending attachment token rotation uses the existing acknowledgement
grace behavior independently per attachment.

Client resume persistence is version 2 and stores the optional attachment GUID
and protected attachment grant. Version 1 records migrate explicitly when they
have only legacy primary-token fields; structurally incompatible v1 records
are reported corrupt rather than reinterpreted. Server snapshot storage is
version 2 and accepts version 1 records as a legacy primary attachment. It
persists no live owners or transient replay/output queues. Old clients continue
using the primary resume flow. Client code exposes `CreateNewAttachmentOnConnect`
and a per-network state-store override, but Phase 40 does not add UI for
importing a grant or selecting an independent device store.

## Verification evidence

- Initial TLS/SASL attachment plus a second simultaneous same-account
  attachment succeeded with distinct IDs and attachment tokens. The tests use
  different nicknames. Each attachment received a distinct child grant. A
  wrong SASL account, invalid grant, limit-plus-one attachment, revoked resume
  token, and revoked attachment grant were rejected deterministically.
- With a configured limit of two, the primary and one additional attachment
  succeeded; a further attachment was rejected. Revoking one attachment closed
  its transport and freed a credential slot while the primary remained usable.
- One channel event, one query event, a reply, a reaction and an unreaction
  shared the same canonical `msgid` and authoritative sequence on both
  attachments. Messages sent from either attachment were processed once and
  echoed to both with matching identity/sequence.
- The client `ServerSession` integration test attached client B using a
  separately protected copy of A's grant. B sent a live event, A disconnected,
  and B continued to send and receive. A's saved cursor stayed at `s1` while B
  advanced to `s3`; reconnecting A replayed exactly its two-event gap. A and B
  then received the next live event once with the same `msgid` at `s4`.
- A dedicated rotation test rotated A's token, verified B stayed live, resumed
  B with its prior independently rotated token, then resumed A with its own
  replacement token. Rotation on one attachment did not invalidate the peer.
- A two-attachment server snapshot was loaded into a newly constructed server
  object. Both credentials resumed independently, their replayed event retained
  its `msgid`/sequence, a restored child grant created a third attachment, and
  no attachment had a restored physical owner. This is in-process object
  reconstruction from persisted state, not an OS server-process kill/restart
  test.
- A v1 server snapshot restored as a legacy primary attachment and did not
  expose a session-creation grant. Client v1 migration, malformed-v1 rejection,
  protected grant persistence and transcript secret redaction passed.
- Limits in the implementation are four attachments by default, 256 held-live
  events per attachment, 512 replay events per request, 1 MiB retained replay
  bytes by default, 512-byte outbound lines, and a five-second per-connection
  write timeout. Fan-out writes are started independently. No deliberately
  stalled-reader/backpressure integration test was run, so slow-consumer
  isolation is implemented but not accepted as empirically proven.
- Core: 132 passed. Networking: 79 passed. Full Application: 212 passed and one
  opt-in benchmark skipped on each of two consecutive runs. Server: 29 passed
  (TLS, SASL, legacy resume/rotation, persistence, replay, attachments, limit,
  revocation and rotation isolation). The Phase 36 restart test initially
  caught a legacy behavior change: provisional-session cleanup had been added
  to ordinary RESUME. That cleanup was removed from the legacy path and kept on
  successful ATTACH; the full server suite then passed. No unrelated baseline
  regression remains reproducible.
- Client solution Debug and Release builds succeeded with zero warnings/errors
  (including Desktop, Headless, Interoperability and test assemblies). Server
  application Debug and Release builds succeeded with zero warnings/errors.
  `git diff --check` succeeded in both repositories; Git printed only its
  configured LF-to-CRLF normalization notices.

## Primary outcome and remaining limits

**Primary outcome: `MULTI_DEVICE_CLIENT_VERIFIED_SERVER_LIFECYCLE_GAP`**.
Client attachment behavior and independent replay cursors are exercised over
real TLS/SASL transports, with the peer remaining live while the other
`ServerSession` disconnects and recovers. Server credential persistence is
covered by reconstructing the server object from disk. No reproducible server
attachment defect was found; the outcome records an acceptance-evidence gap,
not a demonstrated functional failure.

Phase 40 does not yet prove the complete acceptance criterion:

- The client disconnect/reconnect scenario does not kill and launch a separate
  client OS process while its peer remains live.
- The multi-attachment restart case reconstructs a server object in the same
  test process; it does not terminate and restart the server OS process.
- There is no deliberately stalled attachment test, shared JOIN/PART/channel
  state, attachment-management UI/wire revocation command, or true multi-process
  client-profile/grant-sharing flow. A second client can be configured through
  the core API with an independent state store and
  `CreateNewAttachmentOnConnect`, but the application has no device-pairing or
  grant import/export experience. The integration test seeds the peer's
  protected state directly in a test store; it does not prove secure
  cross-device grant transfer.
- Wrong-network and non-TLS attachment rejection were not separately exercised
  in the new Phase 40 integration tests. External third-party server
  interoperability and retention-floor divergence were not newly tested.
- A-only offline while B live is demonstrated. The inverse, both attachments
  offline at distinct boundaries, broader stagger permutations, and duplicated
  cross-device application-history projection remain unverified.

Phase 39 remains **partially evidenced**: no report exists, and ACK-written but
not yet processed plus real server-process restart were not demonstrated in
the available Phase 38 report/tests. The implementation keeps these gaps
visible and does not claim the A outcome. Recommended Phase 41 work is a
process-level harness for two real clients and a fresh server process, plus a
slow-reader test and explicit retention-floor/dual-offline boundary scenarios.

## Repository closeout

The client started at `c1112a2810b325c52238cd2039ae80a003ed9bc5` and the
server at `99ce7233362fbbd55a2307e6aa371fad1bf2a55f`; both were clean `main`
checkouts exactly matching their local `origin/main` refs. The server change
was committed locally as `819f2e659afd1601f8088df73fcc9858c2cf0f8c`
(`Add nexIRC multi-device session attachments`). The client change is committed
separately as `Support nexIRC multi-device session attachment`; its resulting
HEAD is included in the final task closeout. Both repositories remain on `main`
with one local commit ahead and zero behind `origin/main`, and clean worktrees.
No fetch, pull, merge, rebase, branch switch, reset, or push was performed.
