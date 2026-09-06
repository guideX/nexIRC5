# nexIRC 5 — Phase 1L: Server Feedback, Moderation Reconciliation, Ban Lists, and Action UX

## Scope and starting state

Phase 1L was implemented in `D:\dev\nexIRC\nexIRC5` on `main`.
The expected starting commit was `a69c91bbda217d09b5efdc3477460b9139f8379f`,
and the repository was at that commit before editing. The starting worktree was
clean, `origin` was `git@github.com:guideX/nexIRC5.git`, and the branch was
0 commits ahead and 0 commits behind its upstream. No fetch, pull, merge,
rebase, or push was performed.

The audit found that Phase 1K already provided typed participant contexts and
commands, ISUPPORT-driven `PREFIX`/`CHANMODES`, server-authoritative member and
mode projections, bounded labeled/unlabeled WHOIS and LIST correlation, rich
WPF member menus, and deterministic fake transports. The missing half was the
post-send result path: recognized failures were mostly generic/raw numerics,
moderation actions had no bounded typed lifecycle, and ban lists had no first-
class model or surface.

## Operation results and bounds

`IrcOperationResult` in `src/nexIRC.Application/OperationFeedback.cs` models a
network/session-qualified operation with type, target conversation and
nickname, requested mode/mask, start time, state, confirmation, numeric,
friendly explanation, protocol detail, and raw server line. It supports
`ModeChange`, `Kick`, `Ban`, `Unban`, `Invite`, `Whois`, `Notice`, `Ctcp`,
`ChannelModeQuery`, and `BanListQuery`.

Operations end as `Pending`, `Confirmed`, `Rejected`, `TimedOut`, `Cancelled`,
or `Disconnected`. `OperationFeedbackViewModel` keeps at most 128 recent
results. Per-network pending correlation is capped at 64 entries across WHOIS,
LIST, ban-list requests, and action operations. WHOIS remains serialized when
labels are unavailable and coalesces same-target requests; labels are used
opportunistically when the server advertises `labeled-response`.

Default timeouts are 30 seconds for WHOIS, ban-list, moderation, and invite
confirmation. A timeout means confirmation was not observed, not that the
server definitely rejected the request. Timer completion is dispatched through
the workspace dispatcher so WPF-bound collections are updated on the UI thread.
Disconnect retires all network operations and marks them `Disconnected`.

## Numerics and presentation

`IrcNumericCatalog` types these numerics:

* `341 RPL_INVITING`;
* `401 ERR_NOSUCHNICK`;
* `403 ERR_NOSUCHCHANNEL`;
* `404 ERR_CANNOTSENDTOCHAN`;
* `405 ERR_TOOMANYCHANNELS`;
* `407 ERR_TOOMANYTARGETS`;
* `411 ERR_NORECIPIENT`;
* `412 ERR_NOTEXTTOSEND`;
* `421 ERR_UNKNOWNCOMMAND`;
* `442 ERR_NOTONCHANNEL`;
* `443 ERR_USERONCHANNEL`;
* `461 ERR_NEEDMOREPARAMS`;
* `471 ERR_CHANNELISFULL`;
* `472 ERR_UNKNOWNMODE`;
* `473 ERR_INVITEONLYCHAN`;
* `474 ERR_BANNEDFROMCHAN`;
* `475 ERR_BADCHANNELKEY`;
* `476 ERR_BADCHANMASK`;
* `477` conservative network-specific channel-mode restriction;
* `481 ERR_NOPRIVILEGES`;
* `482 ERR_CHANOPRIVSNEEDED`; and
* `485` conservative network-specific privilege restriction.

Recognized failures render as friendly text plus `[NAME numeric: protocol
text]`. Action results also retain the numeric, protocol detail, and complete
raw server line. WHOIS and ban-list result feedback retains the same diagnostic
detail where an ending or failure line is available. Unknown numerics continue
through the existing unknown-numeric/raw presentation path. No fake user chat
messages or success popups are generated.

## Reconciliation behavior

Incoming `MODE` remains the only authority for channel mode/member privilege
projection. Pending mode, ban, and unban operations are matched by network,
connection generation, channel, mode direction, and nickname or exact mask
when present. Unrelated MODE events still update projections normally and do
not complete another network's operation. A confirmed privilege change causes
the existing participant catalog to naturally switch between, for example,
“Give Voice” and “Remove Voice”; send-time optimistic menu flips were not
added.

Incoming `KICK` remains authoritative for member removal and self membership
state. A pending kick may be confirmed by the corresponding KICK after a
nickname change; the target nickname is updated only within that network and
session generation. A self-kick marks the channel not joined, removes the
member projection, preserves the conversation view/history, and does not
speculatively rejoin merely because the desired-channel set still contains
the channel. Existing lifecycle policy remains responsible for reconnect
behavior.

Invite operations correlate `341` acknowledgements and `443` rejection. The
same numeric explanation path covers no-such-nickname, not-on-channel,
insufficient-privilege, and other relevant errors. NOTICE and supported CTCP
(`PING`, `VERSION`, `TIME`) use the same bounded action feedback when a
correlatable server failure is observable; ordinary asynchronous numerics are
not over-correlated.

## Ban lists and CHANMODES

`367 RPL_BANLIST` and `368 RPL_ENDOFBANLIST` are typed as
`IrcBanListItemEvent`/`IrcBanListEndEvent`. Entries contain network-qualified
channel, mask, optional setter, and optional Unix timestamp. Missing setter or
timestamp displays as an em dash. Duplicate masks replace the existing row;
each `BanListResult` is capped at 512 entries and reports truncation.

Requests use the first advertised `CHANMODES` list mode, falling back to `b`,
and send `MODE #channel +<list-mode>`. Labeled responses are used when
advertised; otherwise matching is bounded by network and channel. Separate
channels and networks cannot share pending state. Ban-list completion, timeout,
send failure, cancellation, disconnect, and view close all retire correlation
state.

The WPF Ban List surface is a dedicated workspace view for joined channels. It
has refresh, mask filtering, copy-mask, add-ban, remove-selected, and columns
for mask, setter, and set time. Add and remove validate connected/registered
state, joined channel context, apparent moderation privilege, exact non-empty
masks, control characters, and the configured outbound line limit. Rows are
never inserted or deleted optimistically. A MODE confirmation can complete the
typed add/remove operation; the list itself changes only after a server list
response or bounded refresh. Participant-context Unban is intentionally not
offered from a hostmask alone because a participant may match multiple masks.

## WHOIS, nick changes, disconnects, and labels

WHOIS `401` fails the active view and result instead of leaving it loading.
Normal `318` completion, malformed/incomplete responses, timeout, disconnect,
and concurrent labeled/unlabeled requests are represented by the existing
WHOIS view plus the operation result. Pending action targets update through
observed nickname changes only; no account identity is invented. Every action
stores the connection generation and is retired on disconnect, so a late line
from an old session cannot satisfy a new operation.

The label audit found that the existing architecture already supports outgoing
IRCv3 labeled commands and label propagation through semantic events. Phase 1L
uses that capability for WHOIS, LIST, and ban-list correlation when enabled;
it does not expand CAP negotiation or pretend to provide full IRCv3 coverage.
Unlabeled requests retain the existing bounded serialized/heuristic behavior.

## Demo and accessibility

`--demo` remains entirely offline and deterministic, with duplicate AlphaNet /
BetaNet identities and duplicate channel/nickname targets. It now queues fake
server feedback for successful op/deop, an insufficient-privilege ban, invite
acknowledgement and already-on-channel rejection, a three-entry ban list,
unban plus refresh, nonexistent-nickname WHOIS, kick confirmation, pending
disconnect cleanup, and self-kick state reconciliation.

The main window, member list, participant context menu, WHOIS actions, Ban List
controls, list, and operation feedback status surface have stable WPF
AutomationProperties IDs/names. The operation feedback is intentionally a
status surface rather than a modal popup storm.

## Tests and validation

The new `Phase1LServerFeedbackTests` adds 20 application tests covering numeric
interpretation and unknown fallback, friendly/raw detail, bounded duplicate
ban rows, MODE confirmation and rejection, KICK nickname/self-kick behavior,
invite 341/443, ban-list routing and completion, add/remove validation and
non-optimistic rows, timeout/disconnect cleanup, and WHOIS 401 failure.

Validation completed:

* Core: 45 passed.
* Networking: 23 passed.
* Application: 88 passed, including 68 Phase 1K-era tests and 20 Phase 1L
  tests.
* Aggregate suite: 156 passed, 0 failed, 0 skipped.
* Debug and Release solution builds: passed with 0 warnings and 0 errors.
* `dotnet format nexIRC5.sln --verify-no-changes --no-restore`: passed.
* The Release WPF process started with `--demo` and the deterministic feedback
  seed reached its later scenarios. The available native accessibility surface
  reported no targetable app, so no click-level interaction, visual assertion,
  normal window-close assertion, or exit-code-0 claim is made here.
* A conservative live smoke connected to `irc.libera.chat:6697` over TLS,
  completed registration and numeric 001, sent WHOIS for a temporary unique
  nickname, observed WHOIS 311/312/317/318 plus secure-host details, and
  disconnected cleanly. No channel was joined and no third-party user was
  targeted.

The existing CTCP race correction was audited and left unchanged; it uses a
deterministic synchronization wait rather than an arbitrary long sleep.

## Remaining limitations and Phase 1M recommendation

There is no full IRCv3 transaction layer, account-level identity guarantee,
automatic ban-list update on every daemon after MODE, complete network-specific
numeric catalogue, or native UI smoke harness in the current host. Participant
menu actions that originate from dialogs still rely on the existing compact
input prompts. DCC, scripting, plugins, SASL redesign, storage/history redesign,
channel browser overhaul, and full channel-properties editing remain out of
scope.

Phase 1M should use production evidence to refine per-network moderation
capability/permission policy, add a small WPF view-model/native smoke harness
when a targetable desktop surface is available, and consider explicit server
capability hints for list-mode refresh behavior. Broader IRCv3 support should
remain a separate, deliberately bounded phase.
