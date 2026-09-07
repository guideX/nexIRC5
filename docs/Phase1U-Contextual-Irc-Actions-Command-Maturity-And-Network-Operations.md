# nexIRC 5 Phase 1U — Contextual IRC Actions, Command Maturity, and Network Operations

Date: 2026-09-06

Repository: `D:\dev\nexIRC\nexIRC5`

Branch: `main`

## Outcome

Primary outcome: Outcome A — the existing participant/channel action work now
has a shared application action boundary, mature slash-command parsing, and
deterministic protocol coverage. Network and channel context menus use the
same state-aware action descriptors as the command path.

Secondary outcome: Outcome C — the audit found that the previous command
dispatcher had correct-looking handlers but duplicated parsing and routed
some context operations directly from WPF. The parser and routing boundary
were repaired without changing transport ownership or the accepted Phase 1R–1T
lifecycle architecture.

## Existing command inventory

The pre-Phase-1U dispatcher exposed 31 command names, including aliases and
advanced commands. The inventory was:

| Classification | Commands | Finding |
|---|---|---|
| Complete or materially complete | `/server`, `/join`, `/rejoin`, `/part`, `/msg`, `/query`, `/q`, `/nick`, `/me`, `/quit`, `/disconnect`, `/whois`, `/list`, `/banlist`, `/notice`, `/ctcp`, `/op`, `/deop`, `/voice`, `/devoice`, `/kick`, `/ban`, `/unban`, `/invite`, `/mode`, `/topic`, `/clear`, `/close`, `/help` | Already had application/session routing and existing regression coverage, though parsing and contextual routing were uneven. |
| Alias only | `/q` | Alias of `/query`; retained for IRC-client muscle memory. |
| Advanced/raw | `/raw`, `/quote` | Existing intentional escape hatch through `ServerSession.SendRawCommandAsync`; still bounded by protocol injection and line-size validation. |
| UI-only equivalent before 1U | Network connect/disconnect/reconnect and several channel tree actions | WPF handlers invoked manager/session methods directly; 1U routes these through `WorkspaceActionRouter`. |
| Missing before 1U | `/who`, `/names`, `/away` | Added with contextual defaults and deterministic outbound tests. |

The final supported command list is:

`/server`, `/join`, `/rejoin`, `/part`, `/msg`, `/query`, `/q`, `/nick`,
`/me`, `/quit`, `/disconnect`, `/whois`, `/who`, `/names`, `/list`,
`/banlist`, `/notice`, `/away`, `/ctcp`, `/op`, `/deop`, `/voice`,
`/devoice`, `/kick`, `/ban`, `/unban`, `/invite`, `/mode`, `/topic`,
`/clear`, `/close`, `/raw`, `/quote`, and `/help`.

## Action architecture

`WorkspaceActionRouter` is the reusable application boundary. It exposes:

- `WorkspaceActionId` for semantic operations;
- `WorkspaceActionTargetKind` and `WorkspaceActionTarget` for network, channel,
  query, and nickname identity;
- `WorkspaceActionDescriptor` for labels, target identity, capability/state
  requirements, enabled state, and an accessible disabled reason;
- execution methods that validate again immediately before sending;
- the existing `ParticipantActionService` and `ChannelActionService` as the
  lower-level protocol-aware services used by the same router.

The intended path is:

`WPF menu or slash command → WorkspaceActionRouter → validated workspace/session → ServerSession outbound queue → authoritative server event → workspace projection`.

No WPF control writes directly to an IRC transport. `ServerSession`, manager
ownership, generation fencing, serialized dispatch, and authoritative
projection remain unchanged.

## Parser and context rules

`IrcCommandParser` is UI-neutral and testable without WPF. It normalizes the
command name to uppercase, accepts case-insensitive built-in lookup, retains
the raw argument tail, tokenizes optional IRC `:trailing` parameters, and can
return the remainder after a fixed number of tokens.

Examples:

- `/msg Alice hello there` sends `hello there` as one trailing message;
- `/topic #channel this is the new topic` retains all topic spaces;
- `/away :gone for lunch` treats the colon form as one message;
- `/part` in a channel uses that channel; `/part #other reason` uses the
  explicit channel;
- `/part reason` in a current channel is treated as a part reason. A pending
  `/join` may be cancelled by `/part` using the authoritative session desired
  channel state, while the context menu remains disabled until the channel is
  actually joined;
- `/me` requires a channel or query view;
- `/whois` requires an explicit nickname;
- `/topic` without text requests the current topic, while `/topic text` in a
  channel sets it. `/topic #channel :` deliberately clears the topic.

Unknown commands, malformed input, missing arguments, wrong conversation type,
unregistered networks, unsafe text, and unsupported capability/privilege state
produce local status feedback rather than silent no-ops or exception dialogs.

## User-visible action matrix

| Context action | Equivalent command | State/capability behavior |
|---|---|---|
| Nick → Open Query / Message | `/query Nick` | Reuses the network-qualified query using negotiated IRC case mapping. |
| Nick → WHOIS | `/whois Nick` | Uses the existing correlated WHOIS view and numeric ordering. |
| Nick → Send Notice | `/notice Nick text` | Uses the participant service and local outgoing-notice presentation. |
| Nick → Copy Nickname / Hostmask / Account / Identity | none | Clipboard-only utility; unavailable fields are disabled. |
| Nick → Mention | none | Inserts into the active composer and never sends traffic. |
| Nick → Give/Remove privilege | `/op`, `/deop`, `/voice`, `/devoice`, or adaptive mode equivalent | Uses advertised `PREFIX` mode letters and authority decisions. |
| Nick → Kick / Ban / Kick + Ban | `/kick`, `/ban` | Requires joined state and known moderation authority; ban mask is explicit/conservative. |
| Channel → Join / Rejoin | `/join`, `/rejoin` | JOIN is disabled when already joined and is network-qualified. |
| Channel → Part | `/part [#channel] [reason]` | Enabled only for a joined channel in the menu; command path can cancel a pending JOIN. |
| Channel → Request topic / Edit topic | `/topic [#channel] [text]` | Request is server-authoritative; edits require current topic authority. |
| Channel → Request modes / Refresh member list | `/mode [#channel]`, `/names [#channel]` | Enabled only for a registered joined channel. |
| Channel → Copy name / Clear / History | `/clear` for clear | Local workspace operations preserve stored history unless explicitly removed. |
| Network → Connect | `/server` creates a new network | Disabled while connected or transitioning. |
| Network → Disconnect | `/disconnect` | Disabled when fully disconnected or waiting for reconnect. |
| Network → Reconnect | context action | Reuses manager replacement and generation fencing; never creates a second live session. |
| Network → Open status | none | Activates the network’s status view. |

Existing participant menus retain the useful CTCP, ignore, invite, ban-list,
and adaptive privilege actions from Phases 1K–1M. Their underlying validation
remains in `ParticipantActionService`; 1U makes the command and workspace
surfaces converge on the same action boundary.

## Command behavior

- `/query` and `/msg` resolve the correct network and reuse the existing query.
  A channel target in `/msg` is projected as a channel destination rather than
  accidentally creating a private query.
- IRC casemapping is authoritative for query reuse. Under RFC1459, `Nick[` and
  `nick{` resolve to the same logical query; raw display spelling remains the
  first accepted spelling until a server NICK event changes it.
- WHOIS uses `NetworkSessionManager.RequestWhoisAsync`, preserving labels or
  serialized unlabeled correlation, the existing WHOIS view, numeric ordering,
  and `318` completion behavior.
- `/notice` preserves a target and trailing text; channel notices remain in a
  channel view and nick notices use a query view.
- `/me` uses typed `IrcParticipantCommandBuilder.BuildAction`, producing
  `PRIVMSG <target> :\x01ACTION <text>\x01`. Local history uses the existing
  semantic outgoing-action kind.
- `/nick` sends normal `NICK` through the session. The server remains
  authoritative for success, collision, rejection, projection, and reconnect.
- `/join` updates desired-channel state through `ServerSession.JoinChannelAsync`
  and avoids duplicate channel views. `/part` removes desired state and sends
  normal PART with an optional preserved reason.
- `/mode` retains the existing adaptive single-mode command surface and uses
  `PREFIX`/`CHANMODES` grammar for safe flag/parameter operations.
- `/topic` preserves spaces and empty-topic intent; final topic state is only
  changed by authoritative topic events.
- `/kick`, `/invite`, `/away`, `/quit`, `/raw`, and `/quote` use the normal
  session outbound queue. Moderation operations keep existing server-feedback
  reconciliation.
- `/ctcp` remains intentionally bounded to `PING`, `VERSION`, and `TIME`.
  Full CTCP scripting is outside Phase 1U.

## Capability and privilege handling

Member actions use the server-advertised `PREFIX` grammar, including arbitrary
mode/prefix pairs such as `(qaohv)~&@%+`. They do not assume only `@` and `+`.
Channel mode actions use `CHANMODES` categories for list, parameter-always,
parameter-when-set, and flag modes. A moderation menu item is disabled when
the current local privilege is unknown or insufficient, and the service checks
the same authority again before sending.

Ban and kick+ban require an explicit safe mask or use the known member hostmask
as an editable proposal. The client never fabricates an account/host mask from
incomplete identity data and never silently emits an over-broad ban.

## WPF integration and feedback

Network context menus now consume `WorkspaceActionDescriptor` instances and
show disabled reasons through WPF automation names/tooltips. Channel context
actions route through `WorkspaceActionRouter` for JOIN, REJOIN, PART, topic
request, mode request, and NAMES refresh. Nick menus continue to use the
production `ParticipantActionCatalog` and service.

`MainWindowViewModel.Actions` is the application action hub used by both the
dispatcher and desktop surface. Status feedback is written through the normal
view-model status bar and operation-feedback model. Ordinary errors remain
local status text; there are no exception dialogs for malformed commands.

## Identity, lifecycle, and regressions

Query identity remains network-qualified and casemapping-aware. History keys,
drafts, notification routing, reconnect generation boundaries, stale callback
discarding, transport ownership, Reset batching, cross-network active-view
fencing, QUIT flushing, and Phase 1T endurance behavior are intentionally
untouched. Contextual reconnect calls `NetworkSessionManager.ReconnectAsync`,
which stops the current entry, retains desired channels, replaces the session,
and starts one fenced generation.

## Validation

Deterministic application tests cover parser behavior, trailing arguments,
context inference, missing/wrong-context feedback, network routing, query
reuse, RFC1459 identity, emitted AWAY/WHO/NAMES/NOTICE/ACTION/PRIVMSG lines,
context action enablement, disabled-action revalidation, and fake transport
ordering. Core tests cover typed ACTION framing and unsafe-text rejection.

The desktop adds the `contextual-actions` smoke scenario. It uses the same
view-model/router path as WPF menus with demo fake transports and verifies
network status activation, channel NAMES refresh, nick query opening, and
WHOIS completion.

The bounded live proof used Libera over normal TLS with a temporary generated
nickname and no credentials, channel joins, moderation, notices, or messages.
The accepted rerun reported DNS/TCP/TLS success, registration `001`, self-WHOIS
completion at `318`, natural `MainWindow.Close`, QUIT observed, disconnected
state, stopped outbound reader, and process exit. The first attempt reached
disconnected and stopped-reader state but missed the observer’s QUIT event; it
was rerun immediately and passed with `quit=True`.

The final closeout report records exact focused, Core, Networking, Application,
aggregate, Phase 1R/1S/1T, UI smoke, release-build, format, and live-server
results.

## Limitations

- There is no explicit reconnect-cancellation operation in the existing
  manager, so Reconnect Waiting is shown as unavailable for Disconnect until a
  dedicated cancellation lifecycle is designed.
- WHO/NAMES responses use the existing semantic presentation; this phase does
  not add a dedicated WHO results window.
- `/mode` intentionally remains bounded to one safely modeled channel mode per
  invocation.
- `/ctcp` remains limited to the existing safe command set.
- No moderation operation is exercised against unrelated live users/channels.

## Recommended Phase 1V direction

Choose from current product gaps after observing this phase. The strongest
candidate is IRCv3 capability maturity (CAP expansion, message tags,
server-time, away-notify, extended-join, multi-prefix, and carefully selected
history/batch features). User customization or a safe automation foundation
are also reasonable product-facing alternatives; neither should weaken the
Phase 1R–1T lifecycle and endurance gates.
