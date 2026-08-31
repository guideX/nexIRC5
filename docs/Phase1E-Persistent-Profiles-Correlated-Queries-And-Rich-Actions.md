# nexIRC 5 Phase 1E — Persistent Profiles, Correlated Queries and Rich Actions

Status: implemented in the local Phase 1E commit

Phase 1E gives the client a durable, bounded configuration foundation while
preserving the Core → Networking → Application → Desktop layering from
Phases 1A–1D. Configuration and query orchestration are application services;
WPF only edits those services and projects their state.

## Architecture added

`ConfigurationService` owns a validated in-memory `NexIrcConfiguration`.
`IConfigurationStore` is the persistence boundary, with an in-memory store for
the deterministic demo and `JsonConfigurationStore` for the desktop. The
`INetworkProfileRepository` facade provides bounded profile CRUD without
exposing file-format concerns to WPF.

`NetworkSessionManager` accepts the configuration service, applies the
highlight preferences, restores saved profiles, and keeps every session,
operation, view, notification and profile identity network-scoped. The
protocol Core remains authoritative for registration, desired-channel
resynchronization, adaptive PREFIX/CHANMODES data and IRC case mapping.

## Configuration schema and storage

The current schema is version `1`. The default desktop file is:

    %LOCALAPPDATA%\nexIRC\configuration.json

The JSON document contains `schemaVersion`, global `preferences`, and a
bounded `profiles` array. Serialization is UTF-8, indented, camel-cased and
uses a maximum JSON depth of 16. Profile ordering and list ordering are
preserved, making ordinary saves deterministic. Passwords, SASL secrets and
credential-like fields are intentionally absent.

Limits are deliberately finite: 1 MiB per configuration file, 64 profiles,
128 auto-join channels per profile, 8 alternate nicknames, 64 custom
highlight words, 256 characters for ordinary strings, 512 for real names,
and 200 characters for channel names. Hostnames and identity values reject
line breaks and invalid empty values. Profiles, words and channels are
normalized and duplicate channels use RFC1459 folding.

Missing optional JSON properties receive safe defaults. Unsupported schema
versions and malformed, empty, unreadable or oversized files fall back to a
fresh default configuration, return a diagnostic, and attempt to move the
original file to a timestamped `.corrupt-...json` evidence file. A save writes
to a unique temporary file, flushes it to disk, closes it, and replaces the
primary file; temporary cleanup is best effort and cannot hide the original
save error. Reads are copied through a bounded buffer before deserialization.

## Profiles and startup behavior

Each `NetworkProfile` has a stable GUID independent of its display name,
hostname or current session. It stores display name, host, port, TLS choice,
preferred and alternate nicknames, username, real name, auto-join channels,
opt-in auto-connect, and bounded reconnect settings. Two profiles can use the
same network, channel or nickname without sharing application state.

The Saved Network Profiles window supports viewing, adding, editing, deleting
with confirmation, connecting, TLS, identity fields, auto-join editing,
auto-connect and reconnect preferences. The Preferences window exposes
nickname/custom-word highlights, global/highlight/private-message desktop
notifications, connection-failure notifications, and the implemented view
visibility settings are retained by the main shell.

At startup, real saved profiles are restored. Only profiles with
`autoConnect=true` are connected. Joining is deferred until numeric 001 has
completed registration. Desired channels are deduplicated with IRC case
mapping, and the existing Phase 1B pending/joining/resynchronization guards
prevent duplicate JOINs across startup and reconnect. Demo mode uses an
in-memory store and never writes the user's file.

## Secret-handling policy

There is no plaintext password or SASL credential persistence. The connection
options can still accept a host-owned `ISaslCredentialProvider` at runtime,
but the Phase 1E profile schema does not serialize it. A secure OS secret
abstraction is intentionally left for a later phase.

## WHOIS correlation and lifetime

`IrcQueryOperation` gives every logical WHOIS/LIST request a bounded ID,
network identity, target, start time, optional request label and correlation
mode. Outstanding operations are capped at 64 per network and expire after
30 seconds; disconnect, replacement, close and send failure retire their
state.

When the server advertises IRCv3 `labeled-response` and the client has it
enabled, WHOIS requests use a short unique client label such as
`nexirc-000001`. The Core preserves the response `label` tag, so out-of-order
responses and overlapping same-nickname WHOIS operations route to separate
views. Optional and unknown WHOIS numerics are retained while the matching
operation is loading, and numeric 318 completes it.

Without labels, IRC does not provide a universal way to distinguish identical
outstanding requests. nexIRC therefore coalesces same-target WHOIS requests
and serializes different-target requests on each network. An operation is
never silently overwritten or presented as perfectly correlated when the
server did not provide that guarantee.

## LIST lifecycle

LIST start, item and end events retain their response label when present.
Labeled requests replace a prior in-flight request deterministically and late
responses carrying the retired label are ignored. Without labels, a repeated
in-flight LIST is coalesced because its server replies are inherently
ambiguous. A completed LIST can be requested again; its result is cleared
before the new aggregation begins. Empty results complete normally, stale
rows do not merge, disconnect clears operation state, and the existing bound
of 2,000 unique rows remains enforced. LIST row identity and ordering use
the active network's IRC case mapping.

## Rich channel, member and query actions

Channel context actions now include Join, Part, Rejoin, copy channel name,
request mode/topic, Open LIST, clear local view and close local view. Closing
a local view does not PART or disconnect; those remain separate commands.

Member context actions include Query, WHOIS, mention insertion, copy
nickname, Notice and CTCP VERSION/TIME/PING. When the local member has a
server-advertised moderation prefix, adaptive mode actions expose operator,
deoperator, voice, devoice and kick. Moderation mode selection derives from
PREFIX ordering rather than assuming only `@` and `+`. Generated commands go
through the selected `ServerSession`, so identical channels and nicknames on
different networks cannot cross-route.

Incoming CTCP is a distinct semantic event. ACTION renders as an action line,
other CTCP renders as a CTCP line, and outgoing CTCP has its own typed local
echo; none is presented as ordinary message text. Query creation and reuse
are keyed by network plus IRC-folded nickname. Incoming and outgoing private
messages therefore reuse the correct network-scoped view.

## Notifications

The existing bounded asynchronous `IIrcNotificationService` remains outside
the protocol domain. Phase 1E adds an optional `DesktopNotificationAdapter`
using the built-in Windows taskbar balloon mechanism, avoiding a large toast
framework dependency. It is replaceable, opt-in, catches OS/callback failures,
and cannot block IRC processing. It respects global, highlight, private
message and connection-failure preferences, suppresses active-view and own
outgoing-message notifications, and uses bounded summaries.

Because this is an unpackaged WPF executable, the adapter does not claim
Windows 10/11 identity-dependent toast activation or click routing. The
taskbar balloon is the demonstrated practical fallback.

## Demo behavior

`dotnet run --project src\nexIRC.Desktop -- --demo` remains deterministic and
uses only fake transports plus an in-memory configuration store. It creates
AlphaNet and BetaNet with stable in-memory profiles, overlapping #lounge and
Mira identities, adaptive AlphaNet and BetaNet PREFIX data, channel/member
context data, private messages, CTCP, WHOIS, LIST, highlights, local message,
action and query paths. Demo-only notifications are enabled in memory so the
notification adapter boundary is exercised without modifying the user's
configuration file.

## Tests and validation

The Phase 1E deterministic suite totals 89 tests: 39 Core, 22
Networking and 28 Application. New coverage includes configuration defaults,
round trips, malformed/unsupported/oversized recovery, stable profile CRUD,
normalization, secret omission, labeled and unlabeled WHOIS overlap, LIST
replacement and cleanup, adaptive rich commands, CTCP presentation,
own-message activity suppression and the existing subscriber isolation
behavior. The full suite passes. The Release solution build passes with zero
warnings/errors, and `dotnet format nexIRC5.sln --verify-no-changes
--no-restore` passes.

The Release WPF executable starts in deterministic `--demo` mode, exposes a
responsive `nexIRC 5` top-level window, and shuts down cleanly with no orphan
process. The host Windows capture helper misidentified the HWND as belonging
to an unrelated installed application, so accessibility/screenshot-driven
profile, preference and context-menu interaction could not be safely claimed.

A bounded live smoke reached Libera.Chat over TLS, completed CAP negotiation
and numeric 001 registration with a temporary anonymous nickname, then
disconnected cleanly without joining or messaging. The headless wrapper was
interrupted with Ctrl+C and returned nonzero despite the session reporting a
clean `Registered -> Disconnected` transition; live LIST/WHOIS were not sent.

## Known limitations and next phase

- Secure OS-backed credential storage is not implemented; no credentials are
  persisted.
- Unlabeled IRC WHOIS/LIST replies retain the protocol's unavoidable
  ambiguity; nexIRC coalesces or serializes rather than inventing correlation.
- Labeled LIST uses deterministic replacement rather than retaining multiple
  simultaneously visible LIST result panes.
- The desktop notification adapter is a taskbar balloon fallback, without
  toast activation or notification-click routing.
- Profile import/export, favorites, MRU history, richer search/filtering and
  full preferences/theming are deferred.
- Accessibility-driven WPF interaction remains environment-dependent.

Phase 1F should build on this foundation with secure secret-provider
integration, profile import/export, favorites/recent destinations and deeper
notification/view routing while retaining the network-scoped operation model.
