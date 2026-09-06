# nexIRC 5 Phase 1M — Channel Authority, Mode Management, Topic Control, and Desktop Smoke

## Baseline and scope

Phase 1M starts from `429c1d60af8a4239e9800c9512daca874a7d2284`, the Phase 1L
server-feedback/reconciliation implementation. The Phase 1L audit found the
operation/result model, server-authoritative MODE/KICK/ban-list/WHOIS/invite
paths, participant projections, WPF automation metadata, and deterministic
fake transports already in place. Phase 1M extends those paths without adding
DCC, scripting, services automation, a history redesign, or a full channel
administration console.

## Channel authority

`nexIRC.Application.ChannelAuthority` is a WPF-free, network-, channel-, and
connection-generation-qualified advisory projection. It considers registration state, joined
state, the current user's observed member entry, negotiated `PREFIX`, target
privilege, current channel modes, and the channel's negotiated `CHANMODES`
grammar through the shared action layer. It reports change-channel-modes,
per-member-prefix changes, kick, ban, unban, invite, topic, and list-query
decisions. The model is explicitly advisory: the IRC server remains the
security boundary and can reject any request.

Every decision has one of three certainty states:

* `Allowed` — likely allowed from the observed protocol state;
* `Denied` — clearly insufficient or impossible from the observed state;
* `Unknown` — server-dependent or missing information, surfaced honestly.

Missing registration, a channel not joined, missing/malformed `PREFIX`, or an
unknown current-user member entry causes conservative unknown/denied results.
The participant menu and Channel Properties use the same projection, so UI
enablement does not duplicate an `@`/`+o` assumption.

## PREFIX hierarchy and relative moderation

The order in the server advertisement is authoritative. For example,
`PREFIX=(qaohv)~&@%+` is ranked `q > a > o > h > v`; custom mode letters and
prefix symbols are retained. Friendly names are supplied for common modes and
unknown advertised modes remain visible as `Mode <letter>` actions.

For a known target, a clearly higher-ranked target disables moderation and
privilege changes. A lower-ranked or ordinary target is plausibly actionable;
an equal-ranked target remains `Unknown` because IRCd policy differs. A
voice-only or ordinary current user is not treated as a moderator. When target
privilege is absent, the menu remains conservative rather than claiming a
definitive answer.

## CHANMODES and channel-mode projection

`IrcChannelModeGrammar` retains the four negotiated classes:

1. list modes;
2. modes that always take a parameter;
3. modes that take a parameter when set;
4. flag modes with no parameter.

Absent `CHANMODES` uses the existing bounded conventional fallback. Malformed
advertisements are ignored safely. The parser consumes parameters according to
the negotiated category, supports mixed signs, member PREFIX modes, list
modes, parameter modes, and unknown modes, and keeps unknown changes
observable. `-k` and `-l` can be parsed without a removal parameter where IRC
mode semantics permit it. `ResetChannelModes` clears channel-wide state while
preserving the separate member-prefix projection. Tracked list-mode parameters
are capped at 512 per mode, matching the existing Ban List boundedness.

`ChannelView` exposes active channel modes, safe mode parameters, and bounded
`ChannelModeProjection` rows. Member PREFIX modes are deliberately separate.
Key-like mode `k` is shown as `set (hidden)` when active; its plaintext is not
stored in the channel snapshot.

## Initial and reconnect synchronization

After a self JOIN, and during reconnect/rejoin restoration, the session queues
the bounded sequence `NAMES <channel>`, `TOPIC <channel>`, `MODE <channel>`, and
`WHO <channel>`. This is one-shot synchronization, not polling. Numeric 324
rebuilds the channel-wide mode state, while preserving member PREFIX modes;
connection-generation reset rebuilds the complete channel projection.

## Channel Properties and mode editing

`ChannelPropertiesWindow` is a focused WPF dialog showing network/channel
identity, topic, topic setter/time, apparent privilege, authority state, and
mode projections. It provides a direct Ban List link and stable automation IDs.
The channel context menu exposes Channel Properties, Edit Topic, mode/topic
queries, Ban List, lifecycle actions, and copy/history actions appropriate to
the current channel view.

Flag edits and modeled parameter edits go through
`ChannelActionService`, `IrcChannelCommandBuilder`, and Phase 1L operation
tracking. No channel mode or topic is changed optimistically: the projection
changes only after the server MODE/TOPIC event. Numeric limits are parsed with
invariant culture and bounded to 1..1,000,000. Other parameters reject
whitespace/control injection and outbound line length remains enforced by the
existing command builder.

Key-like parameters are sent only for the explicit request. They are not
retained in channel state, operation diagnostics, topic/history entries, or
the mode projection. Other safe parameter modes are displayed only when the
server has provided a safe current value. List modes remain generically
projected; the polished workflow continues to be the existing bounded Ban List
service, including its 512-entry cap and server-authoritative add/remove
operations.

## Topic behavior

The state store handles 331 (no topic), 332 (topic), 333 (setter/unix time),
and incoming TOPIC events. Topic metadata is network/channel-qualified and
timestamp parsing is bounded. Topic events remain semantic channel events with
`TranscriptEntryKind.Topic`, not ordinary chat messages; metadata-only 333
events do not add noisy transcript lines.

The typed TOPIC builder supports query, set, and clear operations, rejects
control characters/line breaks, and uses negotiated line-length validation.
The editor preserves the current topic, is authority-aware, and waits for the
server event before the projection changes. Permission rejection is reconciled
by the Phase 1L operation layer and leaves state unchanged.

## Participant and Ban List integration

Phase 1K participant privilege, kick, ban, unban, and invite menus now consume
the shared authority projection. Phase 1L Ban List retrieval and selected-entry
remove behavior are reused from Channel Properties; no duplicate list service
or validation path was introduced. Server errors such as 482 remain friendly
and raw-diagnostic-bearing operation rejections, without globally downgrading
authority from one rejection.

## Deterministic desktop smoke

The desktop accepts the safe command shape:

```text
--demo --ui-smoke participant
--demo --ui-smoke moderation
--demo --ui-smoke channel-properties
--demo --ui-smoke multi-network
```

Smoke mode constructs only the deterministic AlphaNet/BetaNet fake transports,
refuses `--ui-smoke` without `--demo`, validates the scenario name, invokes
real view-model/action services, injects only fake protocol responses, and
prints a `PASS_UI_SMOKE <scenario>` marker after assertions. Failures print
`FAIL_UI_SMOKE <scenario>: ...` and return nonzero. Each scenario is bounded,
uses event/property signals plus awaited tasks, and the WPF window is closed by
the application after completion so it does not leave an orphan process.

The fixture contains duplicate `#general` and `Alex` entities, different
PREFIX/CHANMODES grammars, distinct topics/setter/times, active modes,
parameterized key/limit state, Ban List entries, and synthetic success and
482 rejection paths. Multi-network smoke verifies that authority, mode
operations, topic operations, outbound traffic, and projections stay scoped to
the intended network.

The in-process harness uses the real WPF windows and their view models; it does
not use HWND coordinates, private-field mutation, real IRC traffic, or
arbitrary sleeps. New automation metadata covers the Channel Properties window,
topic field/edit action, modes section and dynamic mode controls, Ban List
button, channel context menu/actions, and operation status text.

## Validation and limitations

Phase 1M adds deterministic core protocol tests for typed channel commands,
malformed advertisements, mode categories, mixed-sign parameter consumption,
sensitive key handling, and resync separation, plus application tests for
network-qualified authority and Channel Properties reconciliation. The
completed aggregate verification passed Core 50, Networking 23, and
Application 90 tests: 163/163 overall. Tests that enumerate WPF-bound
collections while an immediate test dispatcher is receiving background session
events can remain timing-sensitive; the completed aggregate run was green, and
the focused harness cleanup recommendation remains recorded below.

The four deterministic smoke commands each returned exit code 0 and emitted
their corresponding `PASS_UI_SMOKE` marker. `--ui-smoke` without `--demo` and
an unknown smoke scenario each returned exit code 2 with `FAIL_UI_SMOKE`.
Debug and Release solution builds passed, as did
`dotnet format nexIRC5.sln --verify-no-changes --no-restore`. Normal `--demo`
startup produced a live `nexIRC 5` WPF window and closed gracefully. The
conservative TLS Libera.Chat check registered, completed self-WHOIS through
numeric 318, and disconnected cleanly without joining a public channel.

The deterministic fake-network smoke is the primary GUI evidence. Ordinary
`--demo` startup and the conservative TLS/self-WHOIS Libera.Chat smoke are
separate checks; neither is used to claim real-server mode/topic authority or
to perform moderation/topic changes against public users or channels.

## Phase 1N recommendation

Keep the authority model and server-authoritative reconciliation as the base.
The next phase should prioritize a serialized test/WPF dispatcher boundary and
focused lifecycle/read-state validation, then consider additional generic list
mode diagnostics only if they remain bounded. Avoid turning Channel Properties
into a broad services or administration console.
