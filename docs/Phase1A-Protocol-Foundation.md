# nexIRC 5 Phase 1A — Protocol Foundation

**Status:** Outcome B — Phase 1A complete with bounded limitations
**Implementation date:** 2026-08-30
**Scope:** headless IRC protocol, adaptive server discovery, isolated session state, deterministic transports, and diagnostics

## Result

Phase 1A establishes a new C#/.NET 10 foundation in `nexIRC5`. It does not port code from the legacy VB6/VB.NET/WPF trees and does not add Avalonia, guideXOS, Matrix, DCC, media, scripting, or UI code.

The authoritative implementation is the deterministic test suite. It does not require a public IRC server.

## Project architecture

```text
nexIRC5.sln
├── src/nexIRC.Core
│   ├── Protocol       framing, parsing, outbound construction
│   ├── Networking     UI-neutral transport contracts
│   ├── State          CAP, ISUPPORT, feature, profile, identity models
│   └── Session        ServerSession and isolated conversation state
├── src/nexIRC.Networking
│   ├── TcpTlsIrcTransport      TCP/TLS production transport
│   └── Testing/FakeIrcTransport deterministic scripted transport
├── src/nexIRC.Headless          transcript/live connection diagnostics
├── tests/nexIRC.Core.Tests
└── tests/nexIRC.Networking.Tests
```

`nexIRC.Core` references only the .NET base class library. It has no Avalonia, Windows, guideXOS, Matrix, DCC, media, or scripting dependency. The transport interface uses .NET memory, cancellation, and endpoint abstractions, so a future guideXOS host can supply another implementation.

## IRC byte framing

`IrcLineFramer` consumes arbitrary byte spans and emits `IrcLineFrame` objects only at CRLF boundaries. It preserves partial lines and split CR/LF sequences, accepts coalesced messages, and treats empty reads as no-ops. A line is bounded at a configurable 8,192 bytes by default; the implementation has a hard supported maximum of 16 MiB to prevent accidental unbounded allocation. The limit is measured before CRLF and is independent of `Environment.NewLine`.

Frames retain both the original bytes and UTF-8 text. Invalid UTF-8 is decoded with the platform replacement behavior while the bytes remain available for diagnostics. A disconnect reports an incomplete trailing line without pretending it was a complete IRC message.

## Message parser and outbound builder

`IrcMessageParser` returns a result rather than throwing for malformed input. `IrcMessage` retains:

- exact raw line;
- ordered tags, raw tag values, and IRCv3-unescaped values;
- optional source prefix and its raw/name/user/host pieces;
- original command token and normalized command;
- numeric command as the original integer when the command is three digits;
- middle parameters and a separate `HasTrailingParameter`/`TrailingParameter` pair.

Unknown numeric `742` therefore remains numeric `742`; it is never collapsed into an enum “unknown” value. Unknown commands and malformed input remain observable through the raw and parse-error streams.

`IrcCommandBuilder` creates middle parameters, optional trailing parameters, and CRLF framing. It rejects CR/LF injection, invalid middle parameters, and commands exceeding the configured byte limit. `BuildRaw` is the explicit application escape hatch, but still applies the injection and size checks.

## Transport abstraction and TCP/TLS implementation

`IIrcTransport` provides connect, bounded-memory read, serialized write, disconnect, cancellation, endpoint, remote endpoint, and classified failure information. `TcpTlsIrcTransport` uses `TcpClient`, `NetworkStream`, and `SslStream`.

The connection timeout covers TCP and TLS negotiation. DNS/socket, timeout, TLS authentication, cancellation, remote close, protocol, and intentional failures are represented separately. TLS uses standard platform certificate validation; there is no insecure certificate bypass callback. Outbound writes are protected by a semaphore, and shutdown is idempotent.

`FakeIrcTransport` supports scripted byte chunks, fragmentation/coalescing, delayed steps, injected failures, remote disconnects, reconnect through `FakeIrcTransportFactory`, and complete outbound capture. This keeps protocol tests independent from Internet availability.

## ServerSession and registration state machine

Each `ServerSession` owns its transport, lifecycle task, inbound channels, outbound serialized queue, protocol negotiation, identity detector, and channel/query store. There is no global active server, channel list, user list, nickname, or feature state.

The visible session states are:

```text
Disconnected
  → Connecting
  → TlsNegotiation (TLS endpoints)
  → Connected
  → CapNegotiation
  → Registering
  → Registered
```

Failure and recovery states are `Disconnecting`, `ReconnectWaiting`, and `Failed`.

Registration decisions are centralized in the session connection loop. The initial sequence is CAP LS 302, optional PASS, NICK, and USER. Welcome numeric `001` makes registration explicit. PING receives an immediate PONG. Numeric nickname failures use the configured alternate nickname once, then fail as `RegistrationRejected`. Server `ERROR` produces a semantic error event and a non-transient protocol failure. CAP can arrive before welcome.

Raw lines, parsed messages, parse errors, and semantic events are exposed through bounded asynchronous streams. Raw data is published before parsing, so a later semantic/profile bug cannot erase the original server evidence.

The minimum state projection updates own nickname, JOIN, PART, QUIT, NICK, PRIVMSG, NOTICE, TOPIC, `353` NAMES, topic replies, channel membership, and private-message query creation. Channel state includes a stale flag and connection generation.

## CAP negotiation

`IrcCapabilityNegotiator` normalizes capability names for lookup while retaining raw advertised tokens and raw values. It supports:

- CAP LS 302;
- multiline LS with the `*` continuation marker;
- CAP REQ based on the configured requested set and advertised intersection;
- ACK and NAK;
- NEW and DEL;
- END.

The session exposes available and enabled capabilities through an immutable snapshot. The initial harness requests only a small diagnostic set; applications can supply any capability names, including `multi-prefix`, `server-time`, `message-tags`, account/away/extended-join/chghost/invite/echo/batch capabilities, or SASL. SASL authentication itself is intentionally deferred: Phase 1A establishes the CAP contract and state transitions but does not introduce credential handling or SASL mechanism code.

## ISUPPORT / numeric 005

`ISupportState` retains every raw token, the latest normalized token value, source line, and negated/removed tokens. Unknown tokens remain available for diagnostics and future profile recognition.

Typed interpretations currently include:

- `NETWORK`;
- `CASEMAPPING` (`ascii`, `rfc1459`, `strict-rfc1459`);
- `CHANTYPES`;
- `PREFIX`, including arbitrary mode/prefix pairs such as `(qaohv)~&@%+`;
- `CHANMODES` as four structured mode categories;
- `STATUSMSG`;
- `EXCEPTS` and `INVEX`;
- `LINELEN`;
- `MAXLIST`;
- `TARGMAX`;
- `UTF8ONLY`.

The parser handles negated tokens such as `-EXCEPTS`, removes them from the effective interpretation, and still retains the raw evidence. Membership prefixes and channel-mode categories are not hardcoded to only `@` and `+`.

## ServerFeatureSet

`ServerFeatureSet` safely publishes a combined snapshot of capabilities, runtime ISUPPORT, profile hints, identity, channel types, prefix grammar, channel-mode grammar, line length, and network name.

Feature precedence is:

```text
Actual runtime protocol evidence
        >
explicit user/manual override where appropriate
        >
known network/profile hints
        >
generic defaults
```

For protocol grammar and limits, runtime `005` data always wins. Manual identity labels do not overwrite a stronger runtime software/network observation; a manual label is retained as evidence and used only when runtime evidence is insufficient. Profile hints fill gaps and never cause messages or unknown tokens to be discarded.

## IRCd, network, and services identity

The models are deliberately separate:

- `IRCdProfile` describes daemon-family hints and quirks;
- `NetworkProfile` describes known network hints and may name a likely IRCd/services profile;
- `ServicesProfile` is a separate future NickServ/ChanServ/X/Atheme/etc. concept.

The seed IRCd family set is Generic, UnrealIRCd, InspIRCd, Solanum, ircd-hybrid, Ergo, and Unknown. `ServerIdentityDetector` records typed evidence with source and confidence. Strong `004` software/version evidence can identify a family. `NETWORK` is high-confidence network identity evidence but does not imply an IRCd or services package. Hostname evidence is deliberately low confidence and cannot identify a daemon by itself. Unknown is a normal, fully functional result.

The detector does not inspect MOTD text as high-confidence evidence and does not send software-detection probes. The recognition set is intentionally small and evidence-based; it is not a giant numeric switch table or copied compatibility database.

## Reconnect and recovery

`RunAsync` creates one supervisor task per session. Repeated calls return the same task, preventing duplicate reconnect loops. Reconnect policy has bounded exponential backoff, a maximum attempt count, and cancellation.

Every new connection increments the generation and resets CAP, ISUPPORT, identity evidence, registration state, and negotiated features. Existing channel objects are retained only as explicitly stale, not as authoritative current membership; their membership is cleared and their generation changes. Desired channel names are retained as desired state, but automatic rejoin/resynchronization is not yet implemented.

Intentional disconnect and cancellation stop recovery. Unexpected remote/transport failures can enter bounded reconnect waiting. Registration restarts from the beginning on the next connection.

## Headless harness

`nexIRC.Headless` supports:

```text
nexIRC.Headless --transcript <path>
nexIRC.Headless --server <host> [--port <port>] [--no-tls] [--nick <nick>]
```

Transcript mode prints raw lines, parsed command/numeric information, parse errors, and incomplete trailing input. Live mode prints state transitions, raw lines, semantic events, and a final diagnostic snapshot containing network, detected IRCd/confidence, CAP, ISUPPORT, PREFIX, CHANTYPES, CHANMODES, and channel/query counts.

## Tests and validation

The suite covers framing, parser tags/escaping/prefixes/trailing parameters/unknown values, outbound injection and limits, CAP LS/continuation/REQ/ACK/NAK/NEW/DEL/END, typed and unknown ISUPPORT, detection confidence and precedence, runtime-over-profile behavior, TCP read/write/close, normal registration, PASS/NICK/USER, CAP-before-welcome, collision fallback, server ERROR, PING/PONG, unknown numeric visibility, fragmented/coalesced session input, channel/query projection, two-session isolation, reconnect renegotiation, stale channel state, duplicate-loop prevention, and cancellation.

The final validation commands are:

```text
dotnet format nexIRC5.sln --verify-no-changes
dotnet test nexIRC5.sln
dotnet build nexIRC5.sln
```

No public IRC smoke test is required for success. Any live result is supplemental because deterministic fake-transport tests are authoritative.

## Known omissions and bounded limitations

The following are intentionally outside the Phase 1A completion claim:

- SASL authentication and credential storage;
- complete RFC command/numeric semantic coverage;
- complete channel mode application semantics and mode-list state;
- automatic rejoin, desired-state replay, and full resynchronization after reconnect;
- persistence;
- UI projections, Avalonia, guideXOS host code;
- Matrix, DCC, media, bots, scripting, and extension execution;
- a broad data-driven IRCd/network compatibility catalogue;
- a live public IRC/TLS smoke test.

These omissions do not prevent the protocol/session vertical slice from functioning, but they are why the result is classified as Outcome B rather than an unrestricted Outcome A.

## Recommended next phase

The exact next milestone should be **Phase 1B — protocol completeness and resynchronization**:

1. add a richer typed mode engine driven by the negotiated `PREFIX`/`CHANMODES` grammar;
2. add NAMES/MOTD/list/WHO/WHOIS and common numeric semantic handlers without losing raw events;
3. implement desired-state replay, safe auto-rejoin, and post-reconnect NAMES/topic/user resynchronization;
4. add explicit SASL mechanism interfaces and secure host-owned credential policy;
5. grow profile recognition from evidence fixtures, keeping runtime protocol data authoritative;
6. add transcript persistence/replay fixtures and protocol fuzz/property tests.

Only after that contract is stable should the project begin a separate presentation phase for an Avalonia or guideXOS host.
