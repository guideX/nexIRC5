# Phase 36 — Durable Secure Native Resume

The client now understands the negotiated v1 token-rotation extension while
preserving the existing continuity and recovery-strategy abstractions.

After a validated `NEXIRC RESUME COMPLETE`, the server may send
`NEXIRC SESSION ROTATE <token> <boundary> <generation>`. The client validates
the opaque token, requires the boundary to equal its authoritative current
boundary, atomically replaces the in-memory token, and sends
`NEXIRC SESSION ACK <generation>`. If the connection fails before ACK, the
server's grace overlap leaves the prior token usable. The client never logs
the replacement token: transcript/raw-line redaction uses a diagnostic
fingerprint.

Phase 36 intentionally left bearer-token persistence to the follow-on Phase 37
client-state boundary. Phase 37 adds protected metadata plus Windows DPAPI
storage and defines its rotation crash semantics in
`docs/Phase37-Protected-Client-Resume-And-Crash-Resilience.md`.

TLS remains platform-validated through `SslStream` in
`TcpTlsIrcTransport`; certificate-validation bypasses are test-only. The
server-side production policy requires TLS and authenticated SASL identity for
durable native resume. If native resume is unavailable or rejected, the
existing IRCv3 CHATHISTORY and best-effort recovery strategies remain in use.

The durable logical session, physical connection, authenticated account,
current token, pending/retired token, canonical IRC `msgid`, authoritative
sequence, retained replay window, current owner, and recovery result remain
separate concepts. Existing replay validation continues to require ordered
predecessors, canonical IDs, and explicit completion before reporting exact
recovery.

The interoperable wire contract is specified in the server repository at
`docs/protocol/nexirc-resume-v1.md`.
