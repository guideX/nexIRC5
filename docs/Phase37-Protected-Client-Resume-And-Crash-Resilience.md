# Phase 37 — Protected Client Resume and Crash Resilience

Phase 37 adds bounded client-side durability to the Phase 36 native-resume
contract. The wire protocol remains `nexirc/resume=1`; no protocol version 2
was introduced.

## Durable record

The client persists one version-1 record per stable profile/network binding.
The record contains only:

- persistence format version and protocol capability version;
- the stable network binding (`profile id`, normalized host, port, and TLS
  policy);
- the SASL user name/account hint used to prevent local cross-account reuse;
- protected current-token bytes and, during rotation, protected pending-token
  bytes;
- current, pending, and acknowledged token generations;
- the authoritative replay boundary;
- creation/update timestamps and an optional expiry field;
- an optional server-generation field reserved for a future protocol extension.

The client deliberately does not persist raw tokens, passwords, SASL payloads,
IRC history, message objects, a socket, process identity, nickname, or
reconstructible UI/runtime state. The server remains authoritative for account
identity, logical-session identity, retention, expiry, and sequence validity.

## Secret protection and storage

`IResumeStateStore` and `IResumeSecretProtector` are separate boundaries.
Windows desktop composition uses `JsonResumeStateStore` for bounded metadata and
`WindowsDpapiResumeSecretProtector` for token material. DPAPI `CurrentUser`
binds the protected bytes to the logged-in Windows user profile; the network
binding is supplied as additional DPAPI entropy. nexIRC does not invent or
persist an encryption key.

The default location is `%LOCALAPPDATA%\nexIRC\resume`. Each network record
uses a SHA-256 filename derived from its binding. The JSON contains DPAPI
ciphertext only. Writes use a write-through temporary file followed by atomic
replacement and one backup. Corrupt primary metadata can recover from the
backup; an unusable record is quarantined or ignored and ordinary IRC fallback
continues.

## Commit semantics

Initial issuance is installed in memory and durably saved as soon as the
server's `NEXIRC SESSION` announcement is received. Replay/live boundary
advancement attempts a bounded metadata commit after the canonical event is
accepted. A failed boundary write leaves the previous durable boundary intact;
the protocol's canonical `msgid` and sequence validation makes a later replay
duplicate-safe.

Rotation is the important crash boundary:

1. `SESSION ROTATE` is validated against the current boundary and generation.
2. The old current token and replacement pending token are protected and
   durably saved together.
3. Only after that save succeeds is the replacement installed in memory.
4. The client sends `SESSION ACK`.
5. Until `ACK OK`, a restart loads the old/pending pair and offers the pending
   credential within the server's bounded overlap.
6. `ACK OK` is committed by removing the old overlap. If that cleanup write
   fails, the overlap remains persisted and safe; the promoted pending token
   is still usable on the next restart.

Therefore a process death before persistence keeps the old credential, a death
after persistence keeps a usable overlap, and a death after ACK leaves a
promoted replacement. No client-side write claims a token transition that was
not passed to the store's commit boundary.

## Startup and fallback

Startup loads metadata and unprotects token material before connecting, but the
token is not sent at that point. After TLS, capability negotiation, and the
authenticated SASL account context are established, the record binding is
checked and native resume is attempted. A nickname change does not affect the
SASL account hint.

Missing, malformed, unsupported, inaccessible, expired, invalidated, stale,
wrong-account, wrong-network, retention-floor, and unprotection failures are
safe local recovery outcomes. Unusable state is discarded or replaced by a
fresh server-issued session where available. Temporary persistence failures
retain the in-memory session without acknowledging an uncommitted rotation.
Native rejection uses the existing bounded CHATHISTORY/best-effort strategy;
it never claims exact native replay after a rejected or malformed exchange.

Profile removal calls protected-state cleanup. Normal disconnect preserves the
record so a later application restart can resume it.

## Redaction and threat assumptions

Raw bearer tokens are redacted from raw-line events, outbound events,
transcripts, parse-error records, parsed diagnostic messages, rotation/resume
diagnostics, and persistence errors. SASL payloads are treated the same way.
Only short token fingerprints are used where diagnostics need correlation.

DPAPI protects against ordinary theft of the ciphertext by an attacker who
cannot use the same Windows user protection context. It does not protect a
logged-in user from arbitrary same-user code, memory inspection, or a
compromised nexIRC process. TLS certificate validation remains platform-owned
in production; the client does not downgrade native credentials to plaintext.
Server-side rate limiting, authenticated account binding, expiry, invalidation,
retention floors, and bounded replay remain authoritative.

## Verification

Deterministic coverage includes protected-store round trips, atomic backup
recovery, unsupported metadata versions, rotation state modeling, redaction,
fresh client-instance restart, exact replay gap recovery, stable canonical
message ids, sequence continuity, TLS certificate validation, SASL identity,
and post-replay token cleanup. The server test suite includes a real TLS+SASL
loopback scenario in which a fresh client instance restores state, replays two
missing events, receives rotation, persists ACK cleanup, then a second fresh
client restores the rotated credential and replays a subsequent event through
the same logical session.

Known limitation: the current client record cannot persist a server generation
that the v1 wire protocol does not expose. Server generation compatibility is
therefore enforced by the server's token/account/boundary validation. This
phase intentionally does not add clustering, distributed ownership, or
cross-node migration.
