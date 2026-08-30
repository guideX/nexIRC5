# nexIRC 5 Phase 1D — Command UX, Rich Views, Highlights and Notifications

Status: implemented in the local Phase 1D commit

Phase 1D extends the Phase 1C WPF desktop shell with reusable input UX,
semantic local echoes, dedicated WHOIS and LIST surfaces, configurable
activity policy, and a notification boundary. Core protocol/session state
remains authoritative; the desktop shell still does not own sockets, parse
IRC lines, or maintain a global connection.

## Architecture

The flow is:

    nexIRC.Core
        ServerSession, typed semantic events, adaptive protocol state
            ↓
    nexIRC.Networking
        production TCP/TLS and deterministic fake transports
            ↓
    nexIRC.Application
        command dispatch, workspace projection, history, completion,
        WHOIS/LIST aggregation, activity policy, notifications
            ↓
    nexIRC.Desktop
        WPF bindings, key handling, templates, menus and demo composition

NetworkSessionManager owns the independent ServerSession and NetworkWorkspace
pair for each network. WHOIS, LIST, activity, notification and completion
operations all carry or derive a network/view identity, so equal channel names
or nicknames on different networks do not share state.

## Input history

InputHistory is a bounded presentation-neutral service. It records trimmed,
non-empty submitted input, suppresses consecutive ordinal duplicates, and
defaults to 100 entries. Up navigates toward older entries; Down navigates
toward newer entries. At the newer end, Down restores the draft that was
present when navigation began. Repeated navigation at either boundary is
stable.

The stored strings are never returned by reference or mutated by editing the
input box. The WPF shell submits both normal messages and slash commands to the
same history.

## Completion behavior

CompletionEngine completes the first slash token from
IrcCommandDispatcher.SupportedCommands. The registry includes server, join,
part, msg, query, q, nick, me, quit, disconnect, whois, list, raw and quote.
The result uses lower-case command text while matching case-insensitively.

In a ChannelView, nickname candidates come from that view's projected
ChannelMemberView collection. Candidate ordering uses the active IRC case
mapping and ordinal spelling as a deterministic tie-breaker. The displayed
nickname is inserted without adaptive status prefixes. At message start the
engine inserts the conventional Nickname colon-and-space form; mid-message it
replaces only the current token. Repeated completion cycles through the
candidate set. The cycle key includes network and view identity.

Tab invokes completion in WPF. Up and Down invoke history when no modifier is
held.

## Outgoing-message semantics

TranscriptEntryKind distinguishes incoming messages from:

- OutgoingMessage for a channel local echo;
- OutgoingPrivateMessage for query and /msg local echoes;
- OutgoingAction for /me;
- OutgoingNotice for future notice sending.

The rendering projection formats those kinds with a directional marker or
conventional action form. The command dispatcher appends a local echo only
after ServerSession accepts the outbound command into its asynchronous
transport queue. This is an accepted-for-sending echo, not a server delivery
acknowledgement. If queueing fails, no successful outgoing message is added.
Sensitive PASS, AUTHENTICATE and SASL data remain excluded by the existing
Core redaction rules.

## WHOIS aggregation

WhoisView owns a WhoisResult for one network-scoped nickname request.
NetworkSessionManager routes typed IrcWhoisEvent values to the matching
workspace view and keeps the corresponding protocol numeric in the server
status transcript for advanced diagnostics.

The result tolerates partial and out-of-order fields and currently projects:

- nickname, username, hostname and real name;
- server and server description;
- channels, retaining server-supplied membership prefixes;
- account/login information;
- operator/registered-nickname information;
- away text;
- idle duration and localized sign-on time when parseable;
- secure/TLS indication;
- bounded additional numeric fields.

Common WHOIS numerics including 301, 307, 310, 311, 312, 313, 317, 318, 319,
330, 335, 338, 378, 379 and 671 are recognized by the Core semantic
projection. Unknown additional 300-series numerics received while a result is
loading are retained as additional information rather than causing a parser
or view failure. Numeric 318 marks the result complete. Repeating WHOIS for a
nickname resets its existing view; different networks always use different
workspace/result instances.

## LIST aggregation

ChannelListView owns a ChannelListResult with Loading and Completed states.
LIST start, item and end semantic events are routed to that result. Rows are
sorted by channel name and expose channel, visible users and topic. An empty
LIST completes normally with zero rows. Repeating LIST clears and reloads the
same network-scoped result.

The result is bounded to 2,000 rows. Duplicate channel rows update the
existing row, and additional unique rows are discarded after the bound while
WasTruncated remains true. This keeps a large or hostile server response from
creating unbounded application memory. The WPF view presents a table and
double-clicking a row runs a normal /join command through the dispatcher.

## Status formatting

Status entries continue to retain typed semantic kinds while presenting
readable categories. Capability changes, authentication, registration,
reconnects, errors, MOTD, notices, list orchestration, WHOIS orchestration,
informational numerics, synchronization and ordinary connection lifecycle
messages are visibly separated by TranscriptEntryKind and the WPF template.
Unknown commands and numerics remain readable fallbacks. Raw and parsed event
streams remain available in ServerSession for advanced diagnostics.

## Highlight and activity policy

HighlightActivityPolicy is an in-memory, UI-neutral policy. Its defaults are:

- direct private messages are Important;
- a nickname mention in an inactive channel is Important;
- ordinary inactive channel traffic is Unread;
- traffic arriving in the active view is not marked unread;
- nickname and custom-word highlighting are independently switchable.

Matching folds text through the current IRC case mapping and checks both sides
of a match for nickname/word characters. Thus punctuation such as commas,
colons or parentheses around a nickname is supported, while Ann does not match
announcement. Custom words are trimmed and deduplicated. The current
snapshot nickname is used for each event, so nick changes automatically move
highlighting to the new nickname.

Important channel messages receive a small transcript highlight treatment and
the existing network-tree Important marker. No persistent preference store is
introduced in this phase; the WPF Tools menu exposes the two enable/disable
switches for the in-memory policy.

## Notification subscription model

IIrcNotificationService is a narrow application boundary. IrcNotification
contains network ID, view ID/kind, notification type, activity level, sender,
short summary, timestamp, active-view state and semantic source type.

NetworkSessionManager publishes notifications after semantic projection.
Subscribers are independent bounded drop-oldest channels with background
consumers. Publication uses non-blocking TryWrite, subscriber exceptions are
contained, and a slow subscriber cannot hold up IRC processing or another
subscriber. The service is suitable for future WPF/tray/native notification,
sound, script, plugin, logging or mobile bridge adapters without scraping
transcript text. Native Windows toasts and tray integration remain deferred.

## WPF integration

MainWindow remains a composition and input boundary only. It binds the
application workspace tree and content selector, handles Enter/Tab/Up/Down,
and invokes asynchronous ViewModel operations. Dedicated templates exist for
ServerStatusView, ChannelView, QueryView, WhoisView and ChannelListView.
Outgoing and action entries use distinct visual treatment, important
highlights use a restrained background/foreground treatment, and status
categories receive different colors.

The server/status view continues to show the complete bounded transcript. The
WHOIS view presents structured fields and additional numerics. The LIST view
provides a sortable-by-order table of bounded rows. All transport callbacks
still cross WpfWorkspaceDispatcher before changing bindable workspace objects.

## Demo mode

The deterministic command remains:

    dotnet run --project src\nexIRC.Desktop -- --demo

The expanded scenario creates AlphaNet and BetaNet with overlapping
#lounge/member names, adaptive AlphaNet PREFIX data, ordinary unread channel
traffic, an important nickname mention, a private-message query, server
notices, WHOIS fields, a populated LIST result, local channel text, local
/me action, and local /msg output. It uses FakeIrcTransport only and requires
no credentials or external network.

## Testing

Phase 1D adds deterministic Application tests for history boundaries,
duplicate suppression and draft restoration; command/nickname completion,
cycling, case mapping and cross-network isolation; WHOIS field accumulation,
completion and two-network isolation; LIST ordering, repetition and bounds;
activity boundaries and active-view behavior; subscriber isolation; and typed
outgoing local echoes.

The existing Core and Networking tests remain authoritative for framing,
parsing, adaptive state, session lifecycle, SASL redaction and reconnect
behavior. No live IRC connectivity is required.

## Known limitations

- Local echo means accepted into the asynchronous outbound queue, not
  confirmed by server echo or delivery acknowledgement.
- Repeated WHOIS for the same nickname reuses one refreshed workspace view;
  overlapping same-nickname requests on one network are not separately
  labeled because the current Core event contract has no request label.
- Native Windows notifications, tray alerts, sounds, persistence and a
  preferences dialog are deferred.
- LIST exposes the first 2,000 unique rows and does not yet implement server
  labels, filters or a richer discovery/search surface.
- WHOIS vendor-specific numerics are preserved as bounded additional fields
  when they arrive during a pending result, but there is no vendor-specific
  field catalogue.
- The optional interactive UI exercise is limited to deterministic process and
  window lifecycle checks; no accessibility automation is claimed.

## Recommended Phase 1E

Add a small persistent profile/preferences substrate and use it for server
profiles, highlight words and view preferences. Then add labeled-response
correlation for overlapping WHOIS/LIST operations, richer channel/member
context actions, and the first opt-in notification adapter while preserving
the current UI-neutral boundary.
