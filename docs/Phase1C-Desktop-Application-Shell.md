# nexIRC 5 Phase 1C — Desktop Application Shell and Multi-Network Workspace

Status: implemented in the local Phase 1C commit

Desktop technology: WPF on .NET 10 (net10.0-windows), using the Windows desktop runtime already present on the development host. No third-party UI framework was added. This keeps the first real client shell native, feature-dense, and compatible with the long-lived menu/toolbar/context-sensitive workspace direction identified during Phase 0.

## Application structure

Phase 1C adds two projects:

    nexIRC.Core
        protocol, transport/session contracts, typed events, authoritative state snapshots
            ↓
    nexIRC.Networking
        TCP/TLS transport and deterministic fake transport
            ↓
    nexIRC.Application
        NetworkSessionManager, workspace model, presentation rendering, commands, activity
            ↓
    nexIRC.Desktop
        WPF application, main window, connection dialog, menus, toolbar, context menus, demo mode

nexIRC.Desktop does not parse raw IRC lines and does not own sockets. It composes TcpTlsIrcTransportFactory, creates the application/session manager, and renders application state. Existing Core and Networking abstractions remain the protocol and networking authorities.

## Session orchestration

NetworkSessionManager owns a collection of independent ServerSession instances. Each NetworkWorkspace has its own status view, channel views, query views, session, connection options, and snapshot. The main window never owns a socket or a singleton connection.

The manager supports adding configured-but-disconnected networks, starting a session, disconnecting it, replacing a deliberately disconnected session for manual reconnect, removing a network, projecting session state, and deterministic asynchronous disposal. Automatic reconnect remains the Phase 1B ServerSession responsibility; the manager projects its ReconnectWaiting, Connecting, Registered, and Failed states independently for each network.

Manual replacement resets the workspace's generation baseline and clears projected member/topic state before the replacement session starts. This prevents a newly created session's generation 1 from being rejected as stale after an older session had reached a higher generation.

## Workspace and network tree model

The UI-neutral model contains:

- ServerStatusView, one per network;
- ChannelView, one per channel and scoped to its parent network;
- QueryView, one per private conversation and scoped to its parent network;
- bounded TranscriptEntry collections (500 entries per view);
- snapshot-projected channel topic, synchronization state, modes, and members;
- WorkspaceActivity with None, Unread, and Important levels.

NetworkSessionManager raises typed ActivityRaised notifications when an inactive view becomes unread or important and now exposes the Phase 1D UI-neutral notification subscription boundary. Notification delivery does not parse transcript text.

The WPF TreeView binds to NetworkWorkspace.Views. Network nodes contain only their own status/channel/query children, so equal channel names and nicknames on two networks cannot collide. The tree label exposes the network's current connection state, and inactive views show activity markers.

## Typed event routing and transcript rendering

IrcEventPresentation turns typed Phase 1B semantic events into human-readable entries with timestamps, kind, sender, message text, and metadata. The shell renders messages, notices, joins, parts, quits, kicks, nick changes, topics, modes, NAMES/WHO synchronization, MOTD, numerics, connection transitions, and errors. It does not dump raw protocol lines into the conversation view.

The manager routes channel-targeted events only to that network's channel, query events only to that network's query, and server/session events to that network's status view. A quit or nickname event can be projected to the affected network's channel views. The transcript is a presentation projection; protocol state remains authoritative in ServerSession.Snapshot.

## Commands

IrcCommandDispatcher is outside WPF controls and handles ordinary text plus:

    /server <host> [port]
    /join <#channel>
    /part [#channel] [reason]
    /msg <nickname> <message>
    /query <nickname>
    /nick <nickname>
    /me <action>
    /quit [reason]
    /disconnect
    /whois <nickname>
    /raw <IRC command>
    /quote <IRC command>

Ordinary channel/query text becomes PRIVMSG. /me uses a CTCP ACTION payload through the existing ServerSession.SendCommandAsync safe builder. /raw and /quote use SendRawCommandAsync, so the existing line-size and line-break protections remain in force.

## Adaptive member projection

ChannelView projects members from IrcChannelSnapshot. It never maintains a second membership database. JOIN, PART, QUIT, KICK, NICK, MODE, NAMES, WHO, and reconnect/resynchronization updates arrive through the session snapshot and are re-projected.

Member status uses the advertised IrcPrefixGrammar. The highest advertised rank is displayed, so (qaohv)~&@%+ presents ~, &, @, %, and + correctly rather than assuming only operator and voice. Thread-safe EntriesSnapshot and MembersSnapshot accessors support headless deterministic verification while WPF binds to the observable collections on the UI dispatcher.

## Connection dialog and credentials

The New Connection dialog collects temporary display name, host, port, TLS choice, nickname, username, real name, initial channels, SASL policy, SASL username, and a masked SASL password. The dialog creates a MemorySaslCredentialProvider only when SASL is enabled. It stores the secret in a disposable character buffer, provides fresh Phase 1B SaslCredential instances for exchanges, never places the password in a workspace snapshot or transcript, and clears the PasswordBox after accepting the dialog. Credentials are not persisted; the provider is disposed when the network is removed or the application shuts down.

The Phase 1B session remains responsible for SASL mechanism selection, exchange, redaction, and disposing each active credential after authentication.

## UI-thread boundary and shutdown

WpfWorkspaceDispatcher posts all ServerSession.StateChanged and SemanticEventReceived projections to the WPF Dispatcher. No transport callback mutates a graphical control. The window uses asynchronous commands and does not block on .Result or .Wait().

Closing the window cancels and disposes every manager-owned session, waits for session completion, disposes transport resources through the Phase 1B lifecycle, disposes memory-only credential providers, drains pending workspace dispatches, and then allows the window to close.

## Deterministic demo mode

The desktop executable supports:

    dotnet run --project src\nexIRC.Desktop -- --demo

Demo mode injects two FakeIrcTransport sessions and feeds deterministic CAP, ISUPPORT, registration, JOIN, NAMES, TOPIC, MODE, channel-message, query, notice, and nickname events. It produces an AlphaNet and BetaNet workspace with independent status/channel/query state, adaptive prefixes, topics, member lists, and unread/private activity without Internet access or credentials.

## Multi-network proof

tests/nexIRC.Application.Tests/WorkspaceTests.cs proves two networks can be active at once. The test uses the same #lobby channel and sam query nickname on both networks, verifies Alpha messages do not appear in Beta views and vice versa, disconnects Alpha while Beta remains registered, and removes Alpha while Beta remains owned by the manager.

The same test suite verifies reconnect invalidation: old members disappear, the channel is visibly restoring/stale, and a fresh generation can populate the member list again using a changed adaptive prefix grammar.

## Tests and validation

The authoritative offline suite is:

- Core: 37 passing tests;
- Networking: 20 passing tests;
- Application/workspace: 4 passing tests;
- aggregate: 61 passing tests.

The solution build passes with zero warnings and zero errors. dotnet format --verify-no-changes --no-restore passes after the Phase 1C edits.

A bounded desktop smoke was attempted with the built executable in deterministic demo mode. The process stayed alive with a top-level window titled nexIRC 5 after startup, proving WPF/XAML initialization and demo startup. CloseMainWindow() then completed the asynchronous shutdown path and the process exited. The host's Windows capture helper returned the nexIRC HWND as belonging to an unrelated installed application, so screenshot/accessibility-driven menu/tree/input interaction could not be captured reliably; no visual interaction claim is made from that unavailable evidence.

No live IRC smoke test was attempted. The deterministic fake transport path and existing offline TCP/TLS tests remain the build/test authority.

## Known limitations

- Network profiles, persistence, history/logging, preferences, themes, notifications, scripting, plugins, DCC, ignore/notify lists, advanced highlights, and operator tooling remain out of scope.
- The connection dialog is temporary and does not save profiles or credentials.
- WHOIS is exposed through the command dispatcher and member context menu; there is no dedicated result pane yet.
- Channel List and Preferences are intentionally disabled until their application services exist.
- The shell has basic styling and bounded transcript rendering, not the final nexIRC theme/customization system.
- The current visual smoke could not use host accessibility/screenshot capture because of the helper's HWND ownership mismatch; deterministic process startup and close were verified.

## Recommended Phase 1D

Build the first interaction-depth phase around richer channel/query behavior: persistent in-memory view navigation, command history and completion, proper outgoing-message presentation, WHOIS/LIST result views, server/status formatting, invite/highlight activity policies, and a small notification subscription boundary. Keep extending headless application tests before adding persistence, scripting, DCC, or plugins.
