# nexIRC 5 — Phase 1K: Rich Participant Actions, Moderation, and IRC Workflows

## Outcome and starting state

Phase 1K keeps Phase 1J's Outcome A history architecture unchanged: JSONL is
authoritative, .hidx and .hsidx remain disposable accelerators, and no
database, manifest, or durable history catalog was added.

The starting repository was D:\dev\nexIRC\nexIRC5, branch main,
2dccfe30242a92624fc4d82d1acf1f13e0731ba5
(Implement nexIRC 5 Phase 1J segmented history and retention scalability).
The starting worktree was clean and origin/main...HEAD was 0 0. No fetch,
pull, merge, rebase, or push was performed.

## Audited architecture

Participant state already flows through ServerSession and SessionStateStore,
which project typed JOIN/PART/QUIT/NICK/MODE/KICK/NAMES, WHO, PRIVMSG/NOTICE,
CTCP, and WHOIS events. NetworkSessionManager owns network-qualified
workspace routing, conversation identity, query reuse, WHOIS correlation,
notifications, and server-authoritative snapshots. ChannelView projects
members and current-user privilege state from the session snapshot.
IrcCommandDispatcher owns command-line behavior, while the WPF shell
previously constructed a small raw-string member menu.

Phase 1K adds ParticipantActionContext, ParticipantActionCatalog, and
ParticipantActionService. The context carries the network, channel,
participant, local/target prefix modes, current connection state, and joined
channels. The catalog is WPF-free and emits grouped, dynamic menu items. The
service performs typed routing and leaves WPF responsible only for dialogs,
clipboard operations, focus, and status text.

## Member menu and enablement

The member menu is grouped into Conversation, Information, Channel Privileges,
Moderation, Ignore, and Invite. CTCP is a nested submenu containing Ping,
Version, and Time. Separators and disabled-reason tooltips keep the menu rich
without permanently displaying irrelevant actions.

Query opening, mention insertion, and copy actions remain useful while
disconnected. WHOIS, CTCP, NOTICE, MODE, KICK, BAN, INVITE, and other outbound
actions require a registered network. Channel moderation additionally requires
the channel to be joined and a clearly sufficient local privilege. Unknown
permission state is disabled conservatively. Self-WHOIS and self-copy remain
valid; self-query and casual self-moderation are not offered.

## PREFIX and privilege actions

IrcPrefixGrammar remains the source of truth. Every advertised mode letter is
retained, including unknown letters. Friendly labels are supplied for q Owner,
a Admin, o Operator, h Half-operator, and v Voice; unknown modes are shown as
Mode x. Commands use the negotiated mode letter, never the display prefix.
Missing or malformed PREFIX data produces no executable privilege menu rather
than assuming authority. Custom grammars such as
PREFIX=(qaohv)~&@%+ and unusual advertised mappings are covered by tests.

Privilege and moderation requests never mutate local member state
optimistically. MODE events from the server continue to reconcile the
projection.

## Query and mention behavior

Queries are keyed by (network, IRC-casemapped nickname) through the existing
network workspace. Repeated opens reuse the same view, closed or historical
queries reopen cleanly, and opening a query sends no IRC line, fabricates no
history, and creates no unread activity. A duplicate nickname on another
network is a different query.

Mention insertion is draft-safe: an empty channel composer receives
nickname: ; an existing draft receives the nickname appended without
replacement or automatic send. Focus returns to the composer when the WPF
surface permits it.

## WHOIS

WHOIS remains a first-class correlated workflow. Requests use
labeled-response when negotiated and otherwise serialize/coalesce safely per
network. Pending state is bounded to 64 operations, completes on numeric 318,
and is retired on timeout or disconnect. Multiple simultaneous labeled
requests remain isolated; unknown numerics are retained without crashing
aggregation.

The presentation aggregates 311 user identity, 312 server, 313 operator,
317 idle/sign-on, 318 completion, 319 channels, 330 account, 338 host
information/secure indication, 671 secure indication, 301 away, 307
registered-account indication, and several recognized extension numerics.
Optional fields are nullable. The WPF WHOIS surface presents structured
fields and actions to open a query, copy nickname/hostmask/account, or copy
formatted WHOIS text. Loading and bounded timeout/disconnect failure states
are visible without blocking the UI.

## MODE, kick, ban, kick+ban, invite, and NOTICE

Typed protocol construction now covers participant WHOIS, NOTICE, CTCP,
member MODE, KICK, ban/unban MODE, and INVITE. The command builder uses the
session's configured line limit and the server's advertised lower limit when
available. CR/LF, NUL, SOH, whitespace, invalid identity tokens, and
oversized lines are rejected.

Kick accepts an optional reason and sends KICK <channel> <nick> [:reason]
without removing the member locally. Ban uses *!user@host when username and
host are known; it never invents host data. Otherwise the WPF flow asks for a
manually supplied/editable mask and validates it before transmission. The
Kick and Ban flow prepares and validates the mask first, sends ban, then kick;
failure never silently degrades to Kick-only. Unban is shown when a useful
identity is known and accepts an editable mask. CHANMODES list-mode data
selects the ban mode where available, with b as the standards fallback.

Invite targets another joined channel in the current network. Inviting a
member to the channel currently containing that member is not presented by the
context menu. NOTICE is typed, validated, and recorded using the existing
outbound presentation conventions; participant NOTICE does not create a fake
query.

## Ignore semantics

Ignore rules are bounded and persisted through the existing configuration
store. A rule can match nickname, wildcard hostmask, or account and can be
global or scoped to a saved network profile/network name. Context actions
provide Ignore and Unignore.

Ignored user PRIVMSG, NOTICE, CTCP presentation, highlights, notifications,
and activity are suppressed. The protocol state store still processes JOIN,
PART, QUIT, NICK, MODE, KICK, WHOIS, server numerics, and all other structural
events, so ignore cannot corrupt membership or protocol correctness.

## CTCP and outbound safety

CTCP participant actions support PING, VERSION, and TIME with SOH framing.
The UI cannot inject arbitrary control payloads, and CTCP DCC is explicitly
deferred. CTCP replies continue through typed semantic events. No unbounded
round-trip timing state is introduced because this phase does not display
client-side timing correlation.

## Multi-network correctness

The deterministic test and demo paths contain two isolated networks with the
same #general channel and duplicate Alex nickname. Every participant context
carries the network ID, and action routing is rejected if the channel/member
context does not belong to the selected network. WHOIS, query, MODE, KICK,
BAN, NOTICE, CTCP, INVITE, and ignore scope are consequently network-qualified;
no global nickname lookup is used.

## Demo coverage

--demo remains entirely fake transport. AlphaNet and BetaNet each expose
#general; both contain Alex, while AlphaNet also shows owner/operator/
voice/ordinary participants and additional channels for invite testing.
AlphaNet seeds deterministic synthetic WHOIS data for Alex, including host,
account, operator, idle/sign-on, channels, and secure state. Existing history,
query, draft, alias, reconnect, and segmented-history demonstrations remain.

The demo is intended for manual inspection of the member menu, dynamic
privilege submenu, query opening, mention, WHOIS surface, copy actions,
ignore/unignore, moderation dialogs, and disconnected enablement. It never
sends real network traffic.

## Tests and validation

New tests cover typed participant command framing and injection rejection,
network-qualified query reuse/routing, arbitrary PREFIX modes and labels,
mention behavior, ordered Kick+Ban, disconnected gating, and ignore
presentation suppression while membership state continues to update. Existing
core, networking, application, and Phase 1J regression tests remain enabled.

The WPF project builds in Release/Debug, and the command
dotnet format nexIRC5.sln --verify-no-changes --no-restore is the formatting
check for this phase. The available Windows computer-use surface was queried,
but no targetable native app surface was exposed in this environment;
therefore interactive member-menu clicks and dialog clicks are not claimed.
A process-start smoke did launch the Release executable with --demo and was
cleanly stopped after startup; this does not substitute for real click
automation.

A conservative live smoke reached Libera.Chat over TLS, registered a unique
temporary nickname, sent self-WHOIS, received 311/312/317/318 plus 338 and
671, then disconnected cleanly. It joined no public channel and targeted no
third-party participant. Live moderation, CTCP, NOTICE, and unsolicited-
message presentation were not exercised.

## Limitations and deferred actions

Deferred from Phase 1K: DCC, scripting/plugin architecture, full IRCv3
coverage, channel browser, full network-settings redesign, moderation
confirmation policy beyond compact local input dialogs, ban-list browsing,
and client-side CTCP latency display. Account projection into NAMES/WHO is
not synthesized when the server does not advertise or return it.

## Phase 1L recommendation

Use production evidence to refine capability/permission reconciliation and
ban-list workflows, add a small view-model smoke harness if native UI
automation becomes available, and consider explicit channel privilege/error
feedback from numeric replies. Leave segmented history and storage
architecture unchanged unless deployment evidence demonstrates a real limit.
