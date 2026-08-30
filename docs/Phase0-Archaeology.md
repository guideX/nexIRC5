# nexIRC 5 Phase 0 — v2/v3/v4 Archaeology and Feature Audit

**Audit date:** 2026-08-30  
**Scope:** read-only static archaeology of the three legacy source trees.  
**Decision:** nexIRC 5 should be a clean C#/.NET application. The legacy clients are donors of behavior, algorithms, assets, and product personality—not a source-level foundation.

## 0. Executive summary

The three generations represent three different strengths:

| Generation | Strongest contribution | Main limitation |
|---|---|---|
| v2 | Product personality: unusually deep IRC conveniences, custom MDI UI, media playback/mixer, scripting, DCC, embedded IRC server, themes, and menu customization | VB6/ActiveX/Win32 implementation, global mutable state, one active IRC context, fragile parsing, and a large amount of UI-coupled code |
| v3 | The best legacy session model: multiple status/server connections, a central numeric-processing layer, asynchronous socket wrapper, DCC, services configuration, channel folders, and a large daemon-aware compatibility catalogue | Still global/fixed-array/WinForms-oriented; “server profiles” are mostly compatibility filters and strings, not polymorphic IRCd implementations; `ProcessISUPPORT` is a TODO |
| v4 | A more approachable tabbed presentation, a real Matrix HTTP/sync prototype, message-handler extensibility, CTCP support, native Olm experimentation, and the beginning of IRC/Matrix bridge behavior | WPF-only UI, one mostly hardcoded bridge, bridge logic in view models, weak IRC protocol coverage, unsafe async/lifetime handling, and incomplete Matrix encryption |

### Recommended v5 direction

1. Build an independent, headless `nexIRC.Core` around **one isolated session object per IRC server**.
2. Make IRC framing, parsing, CAP, `005`/ISUPPORT, numerics, modes, reconnect/resynchronization, and raw-event preservation first-class protocol services.
3. Treat server profiles as declarative hints and quirks, never as substitutes for runtime discovery.
4. Put Matrix, DCC, media, bots, scripting, persistence, and platform services behind separate projects/interfaces.
5. Make the workspace a presentation model with two first-class hosts: Avalonia MDI and Avalonia tab/sidebar. A future guideXOS host consumes the same application state and commands.
6. Preserve v2’s “Team Nexgen enhanced IRC” feel through behavior—quick actions, command conveniences, rich notifications, themes, media/now-playing, custom menus, scripting, and diagnostics—not through VB6 controls or global state.

The recommended first implementation milestone is a **headless protocol vertical slice**: a tested IRC line framer/parser, a single modern TLS session, registration, CAP/005 discovery, raw/numeric events, isolated channel/query state, and deterministic reconnect/resynchronization behavior. No Avalonia or guideXOS UI is needed to prove this foundation.

## 1. Evidence, scope, and repository inventory

### Method

The audit used source, project files, configuration, help files, scripts, resources, compiled-artifact presence, and Git metadata. No legacy project was converted, upgraded, modified, or built. Binary build artifacts were counted for inventory but not treated as source evidence. Where source and configuration conflict, this report labels the result as uncertain and recommends an explicit v5 decision.

### Source trees examined

| Version | Exact path | Language/project format | Runtime/framework | Approximate size | Entry point and major dependencies | Completeness/build assessment |
|---|---|---|---|---:|---|---|
| v2 | `D:\dev\nexIRC\nexIRC2` | VB6 `.vbp`, `.frm`, `.bas`, `.cls`, `.ctl`; `NexIRC.vbw` | VB6 native/COM era; Windows-only ActiveX and Win32 APIs | 542 tracked files; roughly 14,912 `.bas`, 2,948 `.cls`, 15,582 `.ctl`, 49,353 `.frm` source lines; roughly 22 MB working tree | `NexIRC.vbp`; startup `mdiNexIRC`; `MSWINSCK.OCX`, `RICHTX32.OCX`, `MSCOMCTL.OCX`, `MSSTDFMT.DLL`, IE frame, ADO, Scripting Runtime, stdole, a local `acidmax` typelib | Source/assets/docs/checked-in `NexIRC.exe` make it a substantially complete historical product. Not reproducibly buildable in a modern environment without VB6, registered OCXs, old typelibs, and the original Windows setup. |
| v3 | `D:\dev\nexIRC\nexIRC3` | VB.NET `.vbproj`, VS2010 solution | .NET Framework 4.8; x86 WinForms | 341 tracked files; roughly 115 VB source files, 36 `.resx`, and about 28,728 VB source lines excluding generated/build output | `nexIRC.sln` → `nexIRC\nexIRC.vbproj`; startup `nexIRC.My.MyApplication`; Telerik WinForms 2011 controls including RadDock, RadGridView, RadChart, RichTextBox, and themes | Much more structured and likely historically buildable, but current reproducibility depends on old Visual Studio/.NET Framework and Telerik binaries referenced through `D:\dev\bkup\Telerik\...`. Static analysis only. |
| v4 | `D:\dev\nexIRC\nexIRC4` | SDK-style C# solution with several class libraries and WPF executable | Libraries `net7.0`; UI `net7.0-windows`; WPF | 249 tracked files; roughly 200 C# files / 11,095 lines, 8 XAML files / 1,178 lines; roughly 31 MB working tree including build output | `nexIRC4.sln` → `nexirc4\nexIRC4.csproj`; WPF `App.xaml`; MahApps.Metro, ControlzEx, BoxIcons, WPF behaviors, MvvmHelpers, Newtonsoft.Json, Microsoft.Extensions.Configuration, native `Olm.dll` | Most modern project format and has local `bin/obj` evidence of prior builds, but it is Windows-only, has no test project, has unusual project dependencies, and native Olm/platform/package assumptions. Not built during this audit. |
| v5 placeholder | `D:\dev\nexIRC\nexIRC5` | Empty/non-Git directory at audit time | None | 0 files before this report | None | Appropriate clean destination for future work; only this planning document was added. |

### Git status/history

| Tree | Status at audit | HEAD/history evidence |
|---|---|---|
| v2 | Clean: `## master...origin/master` | HEAD `f653dab`, 2026-04-21; remote `git@github.com:guideX/nexirc2.git`; history contains older 2013–2016 development. |
| v3 | Dirty and two commits ahead: `## main...origin/main [ahead 2]`; untracked `.vs/...` and untracked `nexIRC.Core\` C# experiment | HEAD `9ec452c`, 2026-02-10; remote `https://gitlab.com/guideX/nexirc3`. The untracked C# folder is not part of the canonical VB project/solution and is treated as an experiment. |
| v4 | Dirty: modified `nexIRC4\ViewModels\ChannelViewModel.cs` | HEAD `89a1c41`, 2026-03-17; remote `https://gitlab.com/guideX/nexirc4`. The working-tree change comments out the `nexIRC.Strava` using and leaderboard calls; those changes were preserved. |

No build was run. The old VB6 toolchain/COM registration, v3 Telerik dependency, v4 Windows/native dependency, and the explicit “do not build unless clearly safe” constraint make static analysis the safer Phase 0 choice.

## 2. nexIRC v2 audit

### 2.1 Architecture

v2 is a large VB6 MDI application centered on global modules, form instances, ActiveX controls, and INI-backed settings. The primary client parser is `mdlIRCStuff.bas`, while forms directly own sockets, controls, timers, and rendering. `mdlStatus.bas` maintains a fixed pool of status windows and `clsChannel.cls` provides a more object-like channel model, but the application also maintains parallel arrays and UI list state in modules such as `mdlChannels.bas`.

The visible application is `mdiNexIRC`/`mdiMain`, with child forms for status, channel, query, DCC, browser, player, playlist, mixer, scripts, options, and diagnostics. A notable architectural surprise is that v2 contains a second product inside the client: `frmIRCServer.frm` plus `mdlIRCServer.bas` implement an embedded IRC server with users, channels, modes, K-lines/O-lines, server messages, links, and service creation.

Important anchors:

* `mdlIRCStuff.bas:130` — `ParseIRCData`; `:171` — `ProcessInput`; `:306` — `Command`; `:777` — CTCP handling; `:865` — numeric dispatch.
* `mdlStatus.bas:72` — `CloseConnections`; `:159` — `NewStatusWindow`.
* `clsChannel.cls` — channel collections and user-level helpers.
* `mdlIRCServer.bas:230` — `Restart`; `:311` — `Rehash`; `:581` — `CreateServices`; `:652` — link messaging.

### 2.2 IRC feature inventory

#### Connection, identity, registration, and server handling

* Winsock/`MSWINSCK.OCX` connection handling with explicit connect/disconnect forms and an active server form.
* Server/network catalogue in `data/config/fixed/servers.ini`, recent/favorite server UI, connection manager, auto-connect list, and mIRC `servers.ini` import.
* Auto-connect and auto-perform commands; per-network auto-join stored in `autojoinchannels.ini`.
* Nickname, alternative nickname, real name, ident/identd support; `Ident` settings default to port 113 and support automatic alternate-nickname selection.
* Reconnect-on-disconnect, rejoin behavior, autojoin-on-invite, connect-on-startup, MOTD/status startup choices, and disconnect/quit presets.
* The code has a fixed pool of status windows—`lTCPUBound=32`—and a connection manager, but global settings such as `lSettings.sActiveServerForm`, `sNickname`, `sServer`, and active channel arrays mean this is not a clean modern multi-server/session model. The user-facing behavior should be treated as primarily one active IRC context with partial/multi-window scaffolding.

#### Commands, server replies, and numerics

`mdlIRCStuff.Command` and `mdlIRCStuff.numeric` cover a broad RFC1459/RFC2812-era surface:

* PING/PONG, PASS/USER/NICK registration, raw commands, command echoing, and unknown/raw output.
* JOIN, PART, PRIVMSG, NOTICE, NICK, QUIT, KICK, MODE, TOPIC, INVITE, ERROR, LIST, MOTD, LUSERS, STATS, WHOIS, WHO, NAMES, and LINKS-related flows.
* Numerics for welcome/server information, MOTD, NAMES, LIST, topic/topic metadata, WHOIS, LUSERS, ISON/notify, bans, channel modes, links, and common errors. Unknown numerics fall back to text output rather than being lost.
* Channel user-level tracking for normal users, voice, ops, bans, exceptions, invites, channel keys, and limits through `clsChannel` and `mdlModes.bas`.
* User mode handling includes at least invisible/away/operator/receive-wallops style behavior in the client and a smaller mode set in the embedded server.
* CTCP ACTION, DCC, VERSION, and PING. ACTION is rendered as a distinct event; VERSION responds with nexIRC identity; PING measures elapsed time.
* WHOIS formatting, query opening, secure-query prompts, ignore checks, notify checks, nick coloring, timestamps, highlighted text, server/raw/unknown panes, and log-save commands.

#### Channel/query/user conveniences

* Channel windows with custom nick list, channel topic, channel mode display, context menus, operator/voice/ban/invite/exception actions, channel list/folder, and auto-join management.
* Query/private-message windows with optional secure-query acceptance, add-to-ignore, add-to-notify, and query logging.
* Nickname completion (`UseNickCompletor`), typed-command history, keyboard navigation, nick list double-click query behavior, notify-window and quick-notify behavior.
* Blacklist/auto-kick behavior and separate ignore list. `mdlIgnore.bas` stores up to 150 nicknames and can suppress or display ignored traffic; `mdlNotify.bas` stores up to 150 contacts and sends ISON/notify requests.
* Configurable IRC text colors, rich text rendering, save/save-as log behavior, custom status panels, and status bar auto-sizing.

#### DCC and file sharing

* CTCP DCC SEND and DCC CHAT discovery in `mdlIRCStuff.bas`/`mdlCommands.bas`.
* Send/receive forms, DCC chat forms, accept/deny prompts, download manager, file-offer settings, configurable prompts, auto-ignore, port scanning, and file-exists behavior.
* `frmDCCFILE`, `frmSendFile`, `frmDCCAccept`, `frmDCCChat`, `frmDCC_Chat`, `frmDCC_Accept`, and `frmDownloadManager` are the primary UI artifacts.
* Audio-server integration can offer playlist files in response to commands or playback. `!list`, file-offer settings, and channel-specific announcements are visible in menus/settings and `mdlMultimediaInterface.ActivatePlayback`.

#### Scripting, macros, commands, and bots

* VBScript/nIRC script editor, script manager, script range runner, text editor, and status window as an interactive scripting surface.
* `data/scripts/nexirc/` contains command scripts and event hooks for `oninvite`, `onjoin`, `onkick`, `onmode`, `onmp3`, `onmsg`, `onnick`, `onnotice`, `onpart`, and `onquit`.
* Numeric script slots exist under `data/scripts/nexirc/numeric/`, indicating a deliberately extensible event/numeric surface even where individual files are empty placeholders.
* `clsFMenu.cls` loads INI-defined menus, builds Win32 popup menus and icons, substitutes variables such as active channel/current audio/active server, then executes commands or script files. It is a data-driven menu/macro engine rather than just static UI.
* `mdlBotCommands.bas`, `frmBots`, and `frmAddBotCommand` support configured bot nicknames, bot types, custom commands, stored bot passwords in the registry, and “perform bot command” actions. `botcommands.ini` includes common eggdrop-style operations such as ADDHOST, INFO, WHO, IDENT, VOICE, WHOIS, PASS, OP, INVITE, GO, KEY, and LOGIN.
* Auto-perform commands run after connection through `mdlAutoPreform.bas` and can be persisted in INI.

#### Embedded IRC server and services

`frmIRCServer.frm`, `mdlIRCServer.bas`, `mdlIRCServer_Channel.cls`, `mdlIRCServer_User.cls`, `modIRCUserCommands.bas`, and `modOperServ.bas` implement a local server mode with:

* listening sockets and local users;
* JOIN/PART/PRIVMSG/NOTICE/NICK/QUIT/KICK/TOPIC/INVITE;
* op/deop, voice/devoice, ban/unban, exception/invite lists, channel modes, user modes, topic state, and channel listing;
* K-line/O-line/oper-style management, server notices, wall/wallops, rehash/restart, link messages, and service creation;
* server links and mirrored link operations through `SendLinks`.

This is a major forgotten feature. It is not merely an IRC client “server utility” dialog; it is a partial IRC daemon embedded in the application. It should be archived as a separate historical subsystem and not placed in `nexIRC.Core`.

### 2.3 v2 media system — detailed audit

The media system is v2’s most distinctive subsystem and deserves separate treatment. It is both a local player/mixer and an IRC file-sharing/announcement feature.

#### Playback engine and supported media

* `mdlMultimedia.bas` wraps WinMM MCI (`mciSendString`, `mciGetErrorString`) for open, play, pause, stop, resume, close, seek, current position, total length, frame count, percentage, FPS, window placement, audio-channel selection, and output sizing.
* `clsMovieX.cls`/`clsMovieX1.cls` and `MovieX.ctl` wrap a custom MCI child-window control with open/play/pause/stop/resume, fullscreen/window hosting, seek/rate/volume, stereo channels, CD-door actions, and playback events.
* `frmPlayer.frm` advertises MCI-era support for MP3/MP2/MP1, WAV, AIFF/AU, AVI, MPG/MPEG/MPE, MOV/QuickTime, VOB, WMA/WMV, MIDI/RMI, and several legacy extensions. Actual codec support depends on the Windows MCI installation, so the filter list is not a reliable guarantee of successful playback.
* `PlayFile` in `mdlMultimediaInterface.bas:282` opens `frmPlayer`, loads the file into `ctlMovieX1`, and starts playback. A separate older playback path is retained as commented code, including an earlier MP3/visualizer control.
* `SetAutoRepeat`/`TimerFunction` use a Win32 timer to repeat a frame range. `ActivateContinuousPlay` implements sequential or shuffled playlist advancement and repeat behavior.

#### Playlist and media discovery

* Playlists are stored as INI files under `data/playlists`; playlist menus enumerate `.m3u` and `.ini` files.
* `LoadPlaylist`, `SavePlaylist`, `AddToPlaylist`, `AddFolderToPlaylist`, `DefragPlaylist`, remove/search helpers, and a visible `frmPlaylist` provide a usable playlist workflow.
* Folder scanning is primarily `*.mp3`; playlist capacity is a fixed 10,000 entries. Missing files are removed during defragmentation.
* `LoadM3U` is present but contains only a “Read M3U” comment, so true M3U parsing was not implemented in the examined source path. INI playlists are the reliable path.
* `SearchForMedia`, `frmSearchThroughPlaylist`, `frmSearchWithinPlaylist`, `frmAddMedia`, and folder-add dialogs expose media search/add behavior.

#### Metadata and now playing

* The UI exposes current media, file title, and a status label for bitrate/channel/sample information; `mdiMain.frm` contains references to the former MP3 control’s `BitRate` and `SamplesPerSecond` display.
* The active source path does not contain a complete ID3/tag metadata reader. No robust artist/title/album extraction implementation was found. Treat “now playing” as filename/title plus optional codec-status text unless the compiled control or omitted historical source proves otherwise.
* `ActivatePlayback` integrates playback with IRC announcements. Settings include `AudioServer`, `OfferWhenPlayed`, `FileOfferInChannel`, `EnableList`, and `LogAudioDownloads`; `data/config/fixed/text/default.ini` contains replacement strings used to format those announcements.
* The audio-server behavior can announce a played/shared filename and size in joined channels, respond to a playlist request, and offer a matching local file through DCC. This behavior is valuable even though the implementation should be redesigned.

#### Visualizer, spectrum, waveform, and mixer

* `data/config/fixed/spectrum.ini` defines ten spectrum bands, oscilloscope type, colors, toolbar images, and visual options. `frmOptions.frm` exposes “Spectrum Analizer”, “Logo Twitch on Peaks”, shuffle, continuous play, mixer, and visual controls.
* `frmMobileMixer.frm` is a compact dockable audio panel with continuous-play/shuffle/playlist controls and WinMM mixer controls.
* `frmMixer.frm` uses WinMM mixer APIs to control speaker/wave/microphone/CD/AUX/telephone/MIDI/PC-speaker/line-in, bass, treble, balance, mute, and peak-meter-like progress indicators.
* There is evidence of a built-in spectrum/oscilloscope product feature in configuration, UI labels, old control references, and compiled-resource-era controls. However, the currently active playback path has substantial visualization code commented out, and a self-contained FFT/waveform algorithm was not found in the examined source. Do not claim a portable algorithm exists yet; mark it **INVESTIGATE FURTHER**. The behavior/UX is worth preserving; the Win32 control implementation is not.

#### Media commands and sharing

* Menu commands cover quick play, video window, mixer, alarm, playlist show/add/search/refresh, play/pause/stop, and media controls.
* Channel menus include offer media/file/image actions; `frmQuickImage` suggests image sharing alongside audio/file sharing.
* Current-playback announcement and file-offer behavior is configurable globally and can be displayed per channel. This should become a policy/template service in v5, not a side effect of the player.

#### Media salvage conclusion

Reuse the **behavioral specification**: playlist lifecycle, shuffle/repeat, continuous playback, file-offer/now-playing templates, channel policy, quick mixer, and visualizer concepts. Reimplement playback/metadata/visualization with platform adapters. Do not port MCI strings, fixed 10,000-entry arrays, Win32 timers, ActiveX controls, or UI callbacks into shared core.

### 2.4 v2 GUI and personality

The visual personality comes from `mdiMain.frm`/`mdiNexIRC.frm`, custom controls, rich text, themed colors, and many small command shortcuts:

* true VB6 MDI parent with child status, channel, query, DCC chat, web, player, playlist, mixer, scripts, options, notify, secure-query, help, stats, port scanner, and server windows;
* toolbar buttons for playlist navigation, play/pause/stop, DCC chat/send, scripts, channel folder, settings, audio, connect/disconnect, and status information;
* File/Tools/Channels/Server/DCC/Scripts/Audio/Browser/Bots/System Stats/Customize/Window/Help menus;
* Window tile horizontal, tile vertical, cascade, arrange icons, minimize/minimize all, close, and auto-maximize commands;
* dockable playlist/mobile mixer and dynamic status-bar panels;
* custom controls `ctlTBox.ctl`, `TBox.ctl`, `ctlListView.ctl`, `ucCoolList.ctl`, `ucSlider.ctl`, `ctlXPButton.ctl`, `isButton.ctl`, `ctlFormDragger.ctl`, `ctlPrg.ctl`, and subclassing/Win32 helpers;
* themes/skins in `data/themes/blue.pak`, `pink.pak`, `data/images/xp`, toolbar/icon resources, RTF/HTML help, and editable image assets;
* built-in IE browser in `frmWeb.frm`/`mdlOLE.bas`, homepage/navigation/search/back/forward/refresh actions;
* tips explicitly mention nickname completion, a built-in IRC server, audio-server playlist/offer-on-playback, themes, log save, browser shortcut, eggdrop commands, DCC, direct skin editing, VBScript, and notify security.

The personality is not just appearance. It is the combination of “everything is one click away,” rich status output, media integrated into chat, configurable command surfaces, and a willingness to include unusual tools.

### 2.5 v2 other subsystems and surprises

* `mdlPortScan.bas`/`frmPortScanner.frm`: server port probing, including common IRC ranges such as 6660–6670 and 7000.
* `frmStats.frm`, `frmSystemStatsConsole.frm`, `frmSysInf.frm`, `mdlStats.bas`: Windows/WMI-style hardware, operating system, BIOS, disk, display, audio, network adapter, and device diagnostics; results can be sent to IRC.
* `frmWeb.frm`: embedded IE browser, local 404 fallback, configurable homepage.
* `frmDecode.frm` and `MDec.ocx`: a file decode utility. `KeyGen` appears both in `mdlCrypt.bas` and `mdlFunctions.bas`; it is an obfuscation/registration-style key generator, not a modern security boundary.
* `frmAlarm.frm`: a scheduled/alarm-style media or notification surface; implementation should be inspected if this behavior becomes a v5 requirement.
* sound playback through `sndPlaySound`, configurable sounds, popup/balloon notifications, splash/about/registration flows, and setup wizard.
* No clear general plugin loader was found. The closest extension points are scripts, dynamic menus, bot command tables, and custom controls.

### 2.6 v2 implementation techniques worth preserving

* A rich event vocabulary around IRC and UI actions, including script hooks for many IRC events.
* A channel model with explicit user-level collections, mode state, bans, exceptions, invites, key, and limit.
* Data-driven server lists, auto-join, notify, bot commands, menus, text strings, themes, and playlists.
* Custom rich-text/log rendering and IRC color preservation.
* Playback state helpers for percentage, seek, repeat, continuous play, and channel announcement policy.
* A local server implementation that demonstrates the breadth of channel/user/mode behavior the product historically wanted to support.
* User-focused conveniences: nick completion, secure queries, notify lists, quick actions, recent connections, keyboard history, and saved window layouts/behavior.

### 2.7 v2 technical debt and anti-patterns

* `mdlIRCStuff.bas` mixes framing/parsing, numeric semantics, channel state, UI output, CTCP, DCC, script dispatch, security policy, and reconnection.
* Parsing is based on `Split` and positional indexes, with no robust IRC line framing or parameter model. The numeric switch is large and fixed; `mdlNumerics.bas` itself is effectively empty.
* Global `lSettings`, active-form references, fixed arrays, and form-level socket controls make session isolation difficult.
* `On Local Error Resume Next` is widespread and often hides malformed input/state corruption.
* `mdlModes.bas` and other older duplicate paths coexist with newer code.
* MCI/WinMM, IE, ActiveX rich text/list controls, subclassing, registry settings, and Windows-only timers make portability impossible.
* Playlist capacity and other limits are hardcoded; M3U loading is unfinished.
* The embedded server shares client-era global infrastructure and should not be entangled with the new client core.
* INI settings are scattered, passwords/credentials are stored through legacy mechanisms, and no migration/versioning model is evident.

## 3. nexIRC v3 audit

### 3.1 Architecture and project layout

v3 is a VB.NET WinForms rewrite with a clearer separation than v2:

* `Classes\Communications\AsyncSocket.vb` / `StatusSocket.vb` provide asynchronous socket wrappers and status-specific dispatch.
* `IRC\Status\Status.vb` stores one status object per connection, including socket, visible/window state, server/network indexes, channel state, notices, MOTD, raw/unknown/unsupported buffers, private messages, and links.
* `IRC\Numerics\clsProcessNumeric.vb` is the central server-line/numeric processor; `clsIrcNumericHelper.vb` aggregates multi-line replies such as welcome, WHOIS, LUSERS, ISON, DCC, and nickname changes.
* `IRC\Channels\clsChannels.vb`, `clsChannelList.vb`, and `clsChannelFolder.vb` hold per-status channels and channel-list/folder behavior.
* `IRC\Settings\Settings.vb` loads a broad family of INI files into global settings structures. It is more organized than v2 but remains a global singleton/fixed-array design.
* `Forms\mdiMain.vb` plus `IRC\MainWindow\clsMainWindowUI.vb` drive the WinForms MDI shell, left connection tree, window toolbar, DCC/query prompts, channel folder, and menu commands.
* `Classes\clsScript.vb` is a custom nIRC scripting interpreter with variables, subroutines, storage types, actions, math functions, and delegates.

### 3.2 IRC and multi-server features

* Multiple simultaneous status/server connections are a real v3 feature. `Status` owns per-status objects and `StatusSocket` carries a status index into the processor.
* Each status can have channels, queries/private messages, notices, MOTD, links, raw/unknown/unsupported text, and channel-folder relationships.
* The command configuration in `data/config/commands.ini` is data-driven for WHOIS, WHO, WALLOPS, VERSION, USERHOST, TRACE, TOPIC, TIME, STATS, SQUIT, SILENCE, QUIT, PRIVMSG, NOTICE, NICK, NAMES, ACTION, AWAY, PART, ADMIN, LIST, and related commands.
* Numeric handling covers a significantly broader RFC/daemon-era surface than v2, including welcome, server information, ISUPPORT display, LUSERS, WHOIS, WHOWAS, LIST, NAMES, channel modes, bans, links, authentication notices, ISON/notify, and many daemon-specific replies.
* Common incoming commands include NICK, QUIT, JOIN, PART, MODE, PRIVMSG, NOTICE, CTCP ACTION, DCC SEND, and DCC CHAT.
* Channel state includes users, op/voice/status prefixes, topic, channel list, channel folder, and mode-related behavior.
* The MDI shell has a connection tree, status/channel/query windows, window toolbar, recent servers, connection manager, channel folder, channel list, DCC bars/prompts, notices, and a more formal settings surface.

### 3.3 Server-type separation: what v3 actually does

This is the most important archaeology finding in v3.

`data/config/compatibility.ini` defines 17 compatibility entries: RFC1459, RFC2812, NEXIRC, KineIRCd, ircu, QuakeNet, Hybrid, IRCnet, Bahamut, AustHex, Ultimate, GameSurge, Ratbox, PTlink, aircd, Unreal, and Charybdis. Several have named profiles such as Standard, Obsolete, ircu, Hybrid, GameSurge, and DreamForge.

`IRC\Numerics\clsIrcNumerics.vb` is a large numeric/string catalogue with comments about conflicting meanings and daemon implementations. `IRC\Numerics\IrcStrings.vb` stores each replacement string’s `sSupport` field and `ReturnStringCompatibile` checks enabled compatibility descriptions around lines 404–414. This is a valuable product idea: the client knows that IRC servers differ and can filter/display behavior by server family.

However, the implementation is not a true runtime polymorphic IRCd architecture:

* `clsProcessNumeric.vb` still contains a large hardcoded `Select Case` over numerics.
* `clsIrcNumericHelper.ProcessISUPPORT` at approximately line 196 is literally `TODO`.
* `ServerInfo.vb` exposes only minimal `ChanTypes` and `Prefix` properties and does not form a complete negotiated capability model.
* No set of `UnrealServer`, `HybridServer`, `ircuServer`, etc. strategy classes was found.
* Profile names are mostly configuration/string compatibility labels; the runtime parser remains shared and hardcoded.

**v5 evolution:** keep the idea, replace the mechanism with a negotiated `ServerFeatureSet` built from CAP, RPL_ISUPPORT/005, RPL_MYINFO/004, registration behavior, known server-profile hints, and manual overrides. Model server quirks declaratively and preserve unknown numerics/events.

### 3.4 v3 DCC, services, scripts, and media

#### DCC

`IRC\DCC\clsDccChat.vb`, `Forms\frmDCCChat.vb`, `frmDCCSend.vb`, `frmDCCGet.vb`, `frmDCCSend`/`frmDCCGet` prompts, ignore dialogs, and `frmDownloadManager` provide:

* active/passive DCC chat sockets;
* DCC SEND offers and receives;
* listening port selection or randomized port range;
* configurable local/external IP, buffer size, prompts, auto-ignore, file-exists action, download directory, and automatic dialog close;
* nickname and file-extension ignore lists, with defaults including executable/script/web/config extensions;
* progress bars, ACK-style transfer feedback, file streams, and auto-close delay.

The implementation is a useful behavioral reference but has serious binary-transfer and security concerns: the send path uses old socket/file APIs, the code is UI-owned, and the file offer parser is positional. v5 should make DCC opt-in, sandboxed, size-limited, cancellation-aware, and independently testable.

#### Services and bots

`IRC\Settings\clsServices.vb` models service definitions with name, type, network, commands, command parameters, and custom types. `services.ini` contains X/Undernet and NickServ/FreeNode/Newnet entries. `IRC\Services\Bots.vb` provides a network-specific `NickBot` with FreeNode-oriented NickServ login/register/ghost behavior. `frmNickServLogin`, `frmXLogin`, and services settings expose the UI.

The good idea is configurable service conventions. The limitation is hardcoded network-name checks and plaintext INI credentials. v5 should represent services as profiles/actions with secure credential references and server-discovered conventions.

#### Scripting

`Classes\clsScript.vb` is a substantial custom interpreter. It has variables and storage, subroutine discovery, control/action/math types, file reading, named execution, and delegate-based integration. This is one of v3’s strongest extensibility concepts, but it should be reimplemented as a deliberately sandboxed command/event automation system; do not execute arbitrary UI/object access as v2/v3 do.

#### Media

v3 retains `Forms\frmVideoPlayer.vb` and a `clsVideo` control with open/close/play/stop/pause/resume/repeat, seek offset, speed, volume, balance, left/right mute, and a 333 ms display timer. It is a useful video-control reference but no v2-equivalent now-playing/audio-server/visualizer architecture was found in v3.

### 3.5 v3 implementation techniques worth preserving

* Per-status connection/session objects and status-indexed dispatch.
* A dedicated numeric processor and helper layer, rather than keeping every numeric in a form event.
* A large commented numeric catalogue that records real-world daemon conflicts.
* Async socket abstraction and socket controller concept.
* Data-driven outgoing command templates, network/server lists, compatibility entries, services, DCC policy, text strings, and channel folders.
* A shared MDI child behavior class and centralized main-window UI coordinator.
* DCC prompt/ignore policy and service login flows as explicit configurable subsystems.
* Script interpreter architecture with named routines and typed action categories.

### 3.6 v3 technical debt

* `AsyncSocket.vb` uses legacy APM (`BeginConnect`, `BeginReceive`, `BeginSend`, callbacks), fixed buffers, and old event semantics. It is a donor for interfaces, not a direct transport implementation.
* `clsIrcNumericHelper.ProcessDataArrival` splits on `Environment.NewLine`; IRC data can arrive fragmented and servers commonly use CRLF independent of host newline conventions. The transport needs a byte/line framer.
* `BeginSend`/old send paths do not present a clear serialized outbound queue or partial-send guarantee.
* Numeric parsing remains positional `Split` plus `Select Case`; malformed/unknown input is handled through exception paths and UI strings.
* `Throw ex` is common and resets stack context.
* `lStatus`, `lSettings`, arrays, and WinForms controls remain global and mutable. Array sizes are configured/hardcoded rather than collection-driven.
* Compatibility support is string/config filtering, not actual capability negotiation; `ProcessISUPPORT` being TODO is decisive evidence.
* NickServ/X behaviors are hardcoded by network description; credentials are stored in INI.
* DCC uses UI forms and old file/socket logic directly.
* Telerik 2011 binaries and .NET Framework/x86 assumptions make build portability poor.

## 4. nexIRC v4 audit

### 4.1 Project architecture

v4 is a C# rewrite split into:

* `nexirc4` — WPF/MahApps UI and view models;
* `nexIRC.IrcProtocol` — IRC client, messages, parser, collections, TCP/WebSocket connection abstractions, CTCP;
* `nexIRC.MatrixProtocol` — Matrix DTOs, HTTP services, login/room/event APIs, sync polling, event models, wrapper, bridge message helper;
* `nexIRC.Olm` — native Olm P/Invoke experiment and local native libraries;
* `nexIRC.Model`, `nexIRC.Enum`, `nexIRC.Business`, and `team-nexgen.core` — shared models/helpers/event aggregator/configuration utilities.

The intended layering is better than v2/v3, but actual references leak UI/platform concerns: `nexIRC.IrcProtocol` references `nexIRC.Business`; `nexIRC.Business` references `PresentationFramework`; Matrix and model projects reference business/team-nexgen helpers. It is not yet a clean platform-independent core.

### 4.2 IRC functionality

The v4 IRC surface is a compact client prototype:

* `Client` owns an `IConnection`, user, channels, queries, peer users, server messages, raw and parsed events.
* Registration sends PASS (if configured), NICK, and USER. `RPL_WELCOME` raises registration completion.
* Incoming handlers cover JOIN, KICK, NICK, PART, PING/PONG, PRIVMSG, QUIT, TOPIC, RPL_NAMREPLY, and RPL_WELCOME.
* CTCP supports ACTION, CLIENTINFO, ERRMSG, PING, TIME, and VERSION in `Ctcp\CtcpCommands.cs`.
* `IRCCommand.cs` only enumerates a small command set; `IRCNumericReply.cs` contains a limited standard numeric enum.
* `Client.HandleNumericReply` mainly displays server text and explicitly treats 005 as display text; it does not build an ISUPPORT/capability model.
* There is a `WebSocketClientConnection`, but the IRC path shown is TCP/StreamReader/StreamWriter and no IRC reconnect/state machine exists.
* `ClientCollection` and `ClientWrapper` support multiple pseudo-users for bridge relay, not general multi-server session management.

### 4.3 Parser and transport

`ParsedIRCMessage.cs` is a useful attempt at a compact parser, but it has correctness gaps:

* it uses `IndexOf` over a mixed `{' ', ':'}` delimiter set;
* it assumes the last parameter is the trailing parameter and trims it;
* it does not represent IRC tags, multiple spaces, empty parameters, or all IRC grammar details;
* `IRCPrefix.cs` uses unconstrained `Split('@')` and `Split('!')` and can index missing parts;
* unknown numerics become `UNKNOWN` rather than a preserved typed numeric value.

`TcpClientConnection.cs` reads with `StreamReader.ReadLineAsync`, sends with an unsynchronized `StreamWriter`, and invokes `Disconnected` on receiver faults. `ConnectAsync` starts a receiver through fire-and-forget, and `SendAsync` swallows errors through `ExceptionHelper`. There is no cancellation token, send queue, reconnect policy, registration state machine, or explicit transport error model.

### 4.4 Matrix architecture and bridge behavior

v4 contains the first real Matrix client prototype in the lineage:

* `MatrixClient.cs` performs login through `UserService`, stores an access token, configures room/event services, lists/join/leaves rooms, creates trusted private rooms, sends messages, and starts/stops sync.
* `BaseApiService.cs`, `UserService.cs`, `RoomService.cs`, and `EventService.cs` use `HttpClient`/JSON against Matrix Client-Server API paths.
* `PollingService.cs` uses Matrix `/sync` long polling with a first-sync timeout and later-sync timeout, tracks joined/invited/left rooms in a concurrent dictionary, refreshes users, and raises sync batches.
* Event/domain objects cover text messages, room creation, joins, invites, unknown events, encryption events, room keys, and encrypted messages.
* `MatrixRoomEventFactory.cs` converts DTOs into domain events.
* `nexIRC.Olm\OlmHelper.cs` P/Invokes native `Olm.dll` and contains group/session decryption primitives. The UI explicitly says Megolm decryption “doesn’t work yet” and the decryption call is commented out in `MainViewModel.cs`.

The bridge itself is primarily event wiring in `MainViewModel`, `ServerViewModel`, `ChannelViewModel`, and `MatrixWrapper`:

* one configured Matrix node/user/device/channel and one configured IRC server/default channel are passed into a wrapper;
* Matrix messages from the configured room are converted into IRC text and IRC messages are converted into Matrix text;
* a ten-second UI `DispatcherTimer` delay gates Matrix message forwarding after connection;
* `MessageHelper.GetMessageDetails` strips known Matrix domains, parses pseudo-nick prefixes and reply text, detects a relay loop by comparing a normalized sender to the configured Matrix nickname, and returns `SendMessage`, `RawMessage`, mention, reply, and target fields;
* the mapping and translation rules are string-based and include hardcoded domain assumptions and a special-case `runningliberachat` workaround;
* `UseMultipleNicknames` creates relay pseudo-users through `ClientCollection`, with an Ident listener on port 113;
* errors are generally handled by logging/UI helper calls rather than a bridge state machine, delivery queue, retry policy, or durable event identity.

**Recommended v5 bridge model:** independent endpoint adapters, durable per-server/channel↔room mappings, a bridge envelope with origin/event IDs, explicit formatting conversion, loop prevention by origin metadata, rate/length policies, per-mapping queues, reconnect/replay rules, and an E2EE adapter that is complete before encrypted bridging is advertised.

### 4.5 Bots and external integrations

v4 does not contain a clean bot framework or a separate Strava service project. The bot-like behavior is hardcoded into the UI/view-model message handlers:

* `MainViewModel.cs:445–467` recognizes Matrix-side `!strava speed`, `!strava elev`, `!strava slope`, `!strava`, and `!help` in `##running`, relays the command to IRC, then posts a requester acknowledgement.
* `MainViewModel.cs:470+` and `ChannelViewModel.cs:141+` implement a `!pace` calculation from time/distance and US/EU units.
* `ChannelViewModel.cs:127+` contains `##runningtesting`/`!leaderboard` logic. In the current dirty working tree, the `nexIRC.Strava` using and scraper calls are commented out; no standalone current Strava API implementation was found in the tracked tree.
* The source contains commented credential material associated with the old leaderboard experiment. It is not reproduced here; it should be treated as compromised historical material and removed/rotated outside this read-only phase.

The correct v5 disposition is to preserve the bot behavior as a requirement candidate, but reimplement it as `nexIRC.Bots` command modules with permissions, rate limits, external API clients, persistence, scheduler hooks, and transport-independent responses.

### 4.6 v4 UI

The UI is a dark/glassy MahApps Metro WPF presentation:

* `nexirc4\Views\MainWindow.xaml` has a toolbar for connect/disconnect, join/part, nick, users, clear, settings, and about.
* The main presentation is a left-placed WPF `TabControl` bound to `Tabs`; each tab is a server, channel, or query view.
* `ChannelView.xaml` has a message area, a right-side user list, a splitter, double-click-to-query, and message input.
* `QueryView.xaml` has messages and input; `ServerView.xaml` displays server messages and command processing.
* Settings expose IRC server/identity, Matrix node/user/password/device/channel, auto reconnect, multiple nicknames, double-relay detection, timestamps/colors, and a two-column IRC↔Matrix auto-join mapping editor.
* The layout is conceptually closer to a modern tabbed IRC client than v2/v3, but it is not yet a true HexChat-style hierarchical server/channel tree. There is no MDI/docking implementation.

## 5. Cross-version feature matrix and v5 dispositions

Quality is an archaeology judgment: **strong** means useful behavior/structure is evidenced; **mixed** means valuable behavior with substantial coupling; **weak** means incomplete or unsafe; **none** means not found in the examined source.

| Feature | v2 | v3 | v4 | Quality across donors | v5 disposition |
|---|---|---|---|---|---|
| IRC TCP connection | Winsock/ActiveX, active-form/global | AsyncSocket + per-status socket | `IConnection` + TCP StreamReader | v3/v4 concepts useful; all need transport rewrite | REIMPLEMENT |
| TLS | Not evidenced | Not evidenced | Not evidenced in IRC path | Missing | REIMPLEMENT |
| Multi-server | Fixed status pool/partial, globally coupled | Real per-status architecture | Bridge pseudo-users, not general sessions | v3 strongest | PORT concept, REIMPLEMENT |
| Server abstraction | Hardcoded numeric switch | Compatibility catalogue/filter + numeric catalogue | None beyond display | v3 idea valuable, implementation incomplete | REDESIGN |
| CAP | None found | None found | None found | Missing | REIMPLEMENT |
| ISUPPORT/005 | Display/limited handling | Display only; `ProcessISUPPORT` TODO | Display text only | Weak in all | REIMPLEMENT |
| Numeric handling | Broad but fragile `Select Case` | Broadest catalogue/aggregator | Small basic switch | v3 evidence, no direct port | PORT behavior, REIMPLEMENT |
| Channels/user lists | Rich channel collections + UI arrays | Per-status collections/tree | Observable collections | v2/v3 behavior, v4 binding | REIMPLEMENT |
| Channel modes | o/v/i/r/b/l/n/t plus fixed parsing | Numeric/mode support with daemon comments | Minimal | Mixed | REDESIGN |
| User modes | Partial | Partial/configured | Minimal | Mixed | REIMPLEMENT |
| Queries/private messages | Rich secure-query workflow | Per-status PM state/prompt | Query collection/tab | Strong behavior | PORT behavior, REIMPLEMENT |
| WHOIS/WHO/LIST/NAMES | Broad UI/formatting | Broad numeric aggregation | Limited | v3 strongest protocol structure | PORT behavior |
| Invites/bans/kicks/topics | Yes | Yes | Partial handlers | v2/v3 rich | REIMPLEMENT |
| CTCP/ACTION | ACTION/DCC/VERSION/PING | ACTION/DCC and related | Action/client info/errmsg/ping/time/version | Mixed-to-strong | PORT then REIMPLEMENT |
| IRC services | Embedded server services plus client conveniences | Configurable X/NickServ model | None formal | v3 best client model | REDESIGN |
| DCC chat | Yes | Yes, more structured | None found | v2/v3 useful behavior, security risk | REDESIGN |
| DCC file transfer | Yes | Yes with prompts/progress | None found | Legacy implementation unsafe | REDESIGN |
| Scripting | VBScript/nIRC hooks and editors | Custom nIRC interpreter | None found | Very distinctive | REDESIGN |
| Aliases/custom commands | Menus, macros, auto-perform, bot commands | Data-driven outgoing command templates | Slash/matrix command parser | Strong product need | PORT behavior, REDESIGN |
| Bots | Configured bot nicknames/commands | NickServ/X service bot helpers | Hardcoded running/Strava-like handlers | Strong but scattered | REIMPLEMENT |
| Matrix | None | None | HTTP login/rooms/sync/bridge | v4 only, incomplete | PORT concepts, REDESIGN |
| IRC↔Matrix bridge | None | None | One-room/channel event wiring | Proof of concept | REDESIGN |
| Matrix E2EE | None | None | Native Olm experiment, not working in UI | High risk/incomplete | INVESTIGATE FURTHER |
| Media playback | MCI/custom MovieX, many formats | Video player control | None found | v2 distinctive | REDESIGN |
| Now playing/metadata | Filename/status plus legacy control hints; no confirmed ID3 reader | None found | None found | Behavior valuable, extraction uncertain | INVESTIGATE FURTHER then REIMPLEMENT |
| Playlist | INI, partial M3U, shuffle/repeat/continuous | Not found as v2 feature | Not found | Strong v2 behavior | PORT behavior |
| Visualizer/spectrum | Config/UI/legacy control evidence; active algorithm uncertain | Not found | Not found | Valuable personality, unclear algorithm | INVESTIGATE FURTHER; REDESIGN |
| Mixer/audio controls | WinMM speaker/input/bass/treble/balance/mute | Video audio controls | None found | Strong v2 but platform-specific | PORT behavior, REDESIGN |
| Logging | Rich text/log save/HTML utilities | Raw/unknown/unsupported/history buffers | Message collections, helper logging | Strong | REIMPLEMENT |
| Highlights/nick colors/timestamps | Extensive settings/custom rendering | String/UI settings | UI options, no full engine | Strong v2 behavior | PORT behavior |
| Notifications/sounds | Balloons, sounds, notify/secure-query | DCC/query prompts and UI | status indicator/UI | Mixed | REIMPLEMENT |
| Themes/skins | Pak themes, editable images, custom controls | Telerik themes/resources | MahApps dark styling/fonts | Strong product personality | REDESIGN |
| MDI workspace | Full first-class MDI, tile/cascade/minimize | Full MDI plus window bar | None | v2/v3 essential | REIMPLEMENT |
| Tabbed/sidebar workspace | Menu/window bar, not modern sidebar | connection tree + window bar | vertical tabs, no hierarchy | v4 visual direction; v3 tree evidence | REIMPLEMENT |
| Configuration/persistence | scattered INI/registry | broad INI model | WPF user settings/INI packages | All useful behaviors, weak security | REDESIGN |
| Automation/timers | auto-connect, auto-perform, alarms, scripts | scripts/timers/process queue | DispatcherTimer bridge delay | Strong but unsafe | REIMPLEMENT |
| External APIs | IE, WMI/stats, MDec, DNS/port probing | external IP lookup, Telerik | Matrix HTTP, commented fitness experiments | Mixed | REDESIGN per integration |
| Diagnostics | port scanner, system stats, console | raw/unknown/unsupported and status diagnostics | Exception/log helpers | Strong personality | PORT behavior |
| Reconnect/recovery | reconnect/rejoin flags but weak state isolation | status reconnect scaffolding | auto-reconnect setting, no robust state machine | All incomplete | REIMPLEMENT |

## 6. Forgotten and unexpected discoveries

1. **v2 embeds an IRC server.** It has server users/channels/modes, oper/K-line behavior, server links, notices, services, rehash/restart, and local server commands.
2. **v2 is also a media workstation.** The mixer controls physical WinMM sources, including bass/treble, microphone, line-in, CD, AUX, MIDI, and mute/balance—not just file playback.
3. **v2 has two extension systems.** There are event/numeric script files and a dynamic Win32 INI menu engine with command substitution and icons.
4. **v2 has an embedded browser, decode tool, port scanner, WMI/system statistics UI, quick image/file sharing, alarm surface, theme editor, menu editor, and setup wizard.** These are easy to forget if the product is remembered only as an IRC client.
5. **v2’s M3U support is a stub.** The UI advertises playlists, but reliable loading is the INI playlist path.
6. **v2’s visualizer evidence is mixed.** Configuration and UI clearly expect a spectrum/oscilloscope, but the active source path contains commented legacy visualization calls and no portable FFT implementation was located.
7. **v3’s server abstraction is not a class hierarchy.** It is a rich numeric/string catalogue plus compatibility filtering. The architecture should be evolved, not copied or described as already polymorphic.
8. **v3 has a TODO for ISUPPORT processing** despite having a compatibility layer. This is the clearest reason v5 must implement runtime discovery independently.
9. **v3’s `Status` object is the best direct evidence for session isolation**, even though its global arrays and UI references prevent direct reuse.
10. **v4’s “HexChat-like” behavior is only partial.** The current XAML has a vertical tab strip and nick list, but no server/channel hierarchy.
11. **v4’s multiple-nickname collection is bridge-specific.** It creates IRC pseudo-clients for Matrix relay identities; it is not the general multi-server architecture needed by v5.
12. **v4 has an actual Matrix `/sync` loop and room model**, but Matrix bridge translation is string/hardcode driven, and encrypted messages are explicitly unfinished.
13. **v4’s fitness bot behavior is embedded in view models.** There is no general bot registry, command permission model, scheduler, or external API abstraction.
14. **The v4 working tree contains a pre-existing modification** that comments out old Strava/leaderboard code. It was not changed by this audit.
15. **The v3 tree contains an untracked C# `nexIRC.Core` experiment**, but it is not in the solution and cannot be treated as a finished v3 architectural donor.

## 7. Code and asset salvage report

The recommendation in this table is about behavior/algorithm salvage. “Direct reuse” is rare because of language/runtime/UI dependencies.

| Source | Symbol/file | Value | Obstacles | Recommendation |
|---|---|---|---|---|
| v2 | `mdlIRCStuff.bas:130/171/306/777/865` | Complete historical map of IRC event flow, CTCP, numerics, channel updates, scripts, DCC, ignores, notify, and media integration | VB6, globals, UI coupling, fragile parser | PORT behavior; reimplement |
| v2 | `clsChannel.cls` | Channel user-level/mode/bans/invites/key/limit behavior | Collections and UI-era assumptions | PORT model semantics |
| v2 | `mdlMultimedia.bas:30+` | MCI lifecycle, seek/position/percentage/FPS/repeat/window hosting | WinMM/MCI and UI HWNDs | ARCHIVE as reference; reimplement adapter |
| v2 | `mdlMultimediaInterface.bas:100/169/198/282+` | Playlist, shuffle/repeat, folder scan, continuous play, audio-server/file-offer policy | fixed arrays, INI, forms, no real M3U parser | PORT behavior; redesign |
| v2 | `clsMovieX.cls`, `MovieX.ctl` | Playback control surface and event vocabulary | ActiveX/custom COM control | PORT API ideas only |
| v2 | `frmPlayer.frm`, `frmMixer.frm`, `frmMobileMixer.frm` | Supported-format UX, media controls, physical mixer UX, peak-meter concept | Win32, ActiveX, native devices | PORT UX; platform adapters |
| v2 | `clsFMenu.cls:131/283/322` | Dynamic menu inheritance, icons, variable substitution, command/script execution | Win32 popup APIs, forms, unsafe scripting | REDESIGN as declarative command/menu system |
| v2 | `mdlStatus.bas:72/159` | Status-window lifecycle and multi-window UX | fixed arrays/global active form | PORT concepts |
| v2 | `mdlIRCServer.bas`, `frmIRCServer.frm`, `modIRCUserCommands.bas` | Embedded IRC server feature breadth and server-side mode behavior | large coupled VB6 subsystem, security | ARCHIVE; do not put in v5 core |
| v2 | `ctlTBox.ctl`, `TBox.ctl`, `mdlRTF2HTML.bas` | IRC color/rich text/log rendering and HTML export | VB6 controls/Win32 | PORT behavior; reimplement renderer |
| v2 | `data/config/fixed/spectrum.ini`, `data/themes/*.pak`, `data/images/xp` | Theme/spectrum defaults and Team Nexgen visual identity | proprietary/legacy resource formats | ARCHIVE/reference; selectively recreate |
| v2 | `data/help/rfc1459.txt`, `rfc2812.txt`, scripts, menu INIs | Product documentation, command/event vocabulary, historical compatibility expectations | old assumptions | REUSE as reference/docs; validate protocol facts |
| v3 | `Classes/Communications/AsyncSocket.vb` | Async transport abstraction and controller concept | APM, fixed buffers, old events | PORT interface idea; reimplement |
| v3 | `Classes/Communications/StatusSocket.vb` | Status-indexed connection dispatch | Form invocation/global status | PORT session routing concept |
| v3 | `IRC/Status/Status.vb` | Per-connection state inventory: channels, PMs, raw/unknown, notices, MOTD, links | fixed structs and UI | PORT state categories; reimplement |
| v3 | `IRC/Numerics/clsProcessNumeric.vb` | Broad numeric/event coverage and aggregation map | positional parser, huge switch, UI side effects | PORT test cases/behavior; reimplement |
| v3 | `IRC/Numerics/clsIrcNumericHelper.vb` | WHOIS/LUSERS/ISON/DCC/nick-change aggregation and DCC policy | globals/UI/Environment.NewLine framing | PORT behavior; reimplement |
| v3 | `IRC/Numerics/clsIrcNumerics.vb` | Daemon conflict comments and numeric catalogue | enum/string hardcoding | REUSE as test/reference data, not parser |
| v3 | `IRC/Numerics/IrcStrings.vb` | Compatibility-tagged user-facing command/text model | Telerik/UI/global settings | PORT concept into profile/template data |
| v3 | `data/config/compatibility.ini` | Historical server families/profiles: Unreal, Hybrid, ircu, Charybdis, etc. | old names/defaults and no discovery | REUSE as seed profile metadata; validate |
| v3 | `data/config/commands.ini` | Data-driven outgoing command templates | INI parser and fixed types | PORT behavior |
| v3 | `IRC/DCC/clsDccChat.vb`, DCC forms | DCC UX and flow | old sockets, UI-owned transfer, security | REDESIGN |
| v3 | `IRC/Settings/clsServices.vb`, `IRC/Services/Bots.vb` | Configurable service definitions and NickServ flow | network-name hardcoding, plaintext creds | PORT behavior; secure redesign |
| v3 | `Classes/clsScript.vb` | Named scripts, variables, actions, math, subroutines | custom interpreter, arbitrary access risk | REDESIGN sandbox |
| v3 | `Forms/frmVideoPlayer.vb` | Media/video controls and user expectations | WinForms/`clsVideo` control | PORT UX only |
| v4 | `nexIRC.IrcProtocol/ParsedIRCMessage.cs` | Span-based parsing direction, raw/parsed split | grammar bugs, no tags/unknown preservation | REIMPLEMENT with tests; conceptually PORT |
| v4 | `nexIRC.IrcProtocol/Messages/MessageHandlerContainer.cs` | Attribute/reflection-based handler extensibility | reflection conventions, no protocol state reducer | PORT concept, simplify/strongly type |
| v4 | `nexIRC.IrcProtocol/Messages/Handlers.cs` | Event-to-domain-handler separation | small/incomplete set, UI dispatcher use | PORT structure; expand/reimplement |
| v4 | `nexIRC.IrcProtocol/Ctcp/CtcpCommands.cs` | CTCP command vocabulary and response behavior | formatting/quirks | PORT behavior; test |
| v4 | `nexIRC.IrcProtocol/Client.cs` | Simple client API and collections | one client, no state/reconnect/capabilities | PORT API shape only |
| v4 | `nexIRC.MatrixProtocol/MatrixClient.cs` | Matrix login/rooms/send/sync service separation | error/state/lifetime gaps | PORT service boundaries; reimplement |
| v4 | `nexIRC.MatrixProtocol/Core/Domain/Services/PollingService.cs` | `/sync` long-poll lifecycle and room refresh concept | `Timer(async ...)`, cancellation/error semantics | PORT concept; reimplement |
| v4 | `nexIRC.MatrixProtocol/Core/Domain/MatrixRoom/MatrixRoomEventFactory.cs` | DTO-to-domain event conversion | incomplete event coverage | PORT pattern |
| v4 | `nexIRC.MatrixProtocol/Wrapper/MessageHelper.cs` | Evidence of required reply/mention/loop-detection cases | hardcoded domains/string hacks | PORT requirements only; redesign |
| v4 | `nexIRC.Olm/OlmHelper.cs` | Native Olm boundary and error vocabulary | native ABI, incomplete session/key management, not working in UI | ARCHIVE/INVESTIGATE; do not expose as ready feature |
| v4 | `nexirc4/Views/*.xaml` and `ViewModels/*.cs` | Modern tabbed conversation, nick list, settings UX | WPF, static `App`, bridge in VM | PORT UX/state requirements; reimplement Avalonia |
| v4 | `nexirc4/Properties/Settings.settings` | Current user-facing configuration inventory | plaintext user settings, one-room assumptions | PORT settings categories; redesign storage |

## 8. Architectural mistakes v5 must not reproduce

| Legacy evidence | Problem | v5 rule |
|---|---|---|
| v2 `mdlIRCStuff.bas`, `mdlCommands.bas` | Protocol parsing, UI output, channel state, DCC, scripting, and policy in one path | Parse into protocol/domain events first; application services consume events; UI observes state |
| v2 global `lSettings.sActiveServerForm`, active arrays, fixed status pool | Single-server assumptions and cross-session mutation | Every server gets an independent `ServerSession`; no global active connection |
| v2/v3 `Split` + positional indexes | Malformed lines can throw or silently misroute parameters | Implement RFC-style framing/parser with tags, prefix, command, middle/trailing params, unknown command/numeric preservation |
| v2/v3 `On Error Resume Next`, v3 `Throw ex` | Errors are hidden or stack context is damaged | Typed errors, structured diagnostics, cancellation, and explicit failure events |
| v3 `ProcessDataArrival` with `Environment.NewLine` | Incorrect line framing for fragmented/host-dependent network data | Byte-level CRLF framer with maximum line length and partial-read tests |
| v3 huge numeric `Select Case` and `ProcessISUPPORT` TODO | Server-specific behavior is hardcoded and static | Capability/profile service with CAP + 005 discovery and declarative mode grammar |
| v4 `Client.DispatcherInvoker` static | Core state depends on one UI dispatcher | Core emits thread-safe events/state changes; hosts marshal to UI at the edge |
| v4 `async void`, `Thread.Sleep(2000)`, fire-and-forget receiver | Unobservable failures and lifetime races | `Task`/cancellation everywhere; owned background loops; serialized outbound writes |
| v4 `PollingService` uses `Timer(async _ => ...)` | Overlap/reentrancy and cancellation ambiguity | One owned async sync loop with backoff, cursor persistence, and lifecycle state |
| v4 `MatrixWrapper` async-void methods | Callers cannot await login/join/send failures | Return `Task<Result>` or typed exceptions; bridge owns delivery/retry policy |
| v4 `MessageHelper` hardcoded domains and `runningliberachat` case | String heuristics are not durable identity/loop prevention | Bridge envelope with endpoint/event/origin IDs and explicit formatting metadata |
| v4 bridge logic in `MainViewModel`/`ChannelViewModel` | UI owns integration, bot policy, timers, and transport routing | `nexIRC.Matrix` and `nexIRC.Bots` are application services independent of views |
| v4 Business/Protocol references to PresentationFramework | Platform/UI dependencies leak into shared logic | `nexIRC.Core` references only modern .NET/BCL abstractions and contracts |
| v2 MCI/IE/ActiveX and v3 Telerik | Legacy UI/control dependencies define application capabilities | Use adapters at frontend/platform boundaries |
| v2/v3 plaintext credentials in INI/registry and v4 settings password fields | Credential exposure and no secret lifecycle | Store only secure-credential references in config; host-provided secret store |
| v2 embedded server coupled to client | Server and client complexity become inseparable | Archive server code; consider a separate future product/library only after v5 client core |
| v4 native Olm code without complete key/session management | Encryption may be falsely advertised | Feature flag E2EE; complete interoperability/security tests before release |
| Fixed array limits across v2/v3 | Hidden capacity failures and defragmentation bugs | Use bounded/observable collections with intentional resource limits |

## 9. Recommended nexIRC 5 feature set

### Minimum first release

* Multiple independent IRC server sessions with TCP/TLS, registration, reconnect, graceful disconnect, raw log, and session diagnostics.
* Robust IRC framing/parser with tags/prefixes/parameters/trailing values and unknown preservation.
* CAP negotiation and runtime capability model; 005/ISUPPORT parser for CASEMAPPING, PREFIX, CHANTYPES, CHANMODES, NETWORK, LINELEN, MAXLIST, EXCEPTS, INVEX, STATUSMSG, TARGMAX, UTF8ONLY, and related tokens.
* Channel/query/server conversations, users, topics, joins/parts/kicks, modes, WHOIS/WHO/LIST/NAMES, notices, CTCP ACTION/PING/VERSION, away, notify, ignore, highlights, timestamps, nick colors, logging, notifications, and command history.
* Data-driven server profiles/quirks and secure configuration persistence.
* First-class workspace model with both MDI and tab/sidebar presentations.
* Avalonia desktop UI on Windows/Linux/macOS, with no Avalonia types in core.

### Follow-up parity/features

* DCC chat/file transfer with explicit security policy.
* Services profiles/actions (NickServ/ChanServ/X/custom) with secure credentials.
* v2-inspired media module: playlists, metadata, now playing, channel policies, announcement templates, audio controls, and visualizer abstraction.
* Matrix client and bridge with durable mappings and robust loop prevention.
* Bot framework, including running/fitness command modules and external API adapters.
* Sandboxed scripts/automation, custom aliases, macro/menu customization, timers, and event hooks.
* Themes, skin/resource packages, URL/file/image sharing, diagnostics, importers, and optional system integration.

### Archive/discard decisions

* **Archive:** v2 embedded IRC server implementation, VB6 ActiveX controls, v3 Telerik forms, old help/skin formats, native Olm experiment until independently validated, legacy decoder, and old project/build files.
* **Discard:** direct VB6/VB.NET translation, MCI command strings as shared logic, IE embedding, registry/INI passwords, global active-form state, fixed numeric switches as the v5 extension mechanism, hardcoded Matrix domains/room, and UI-embedded bot code.
* **Investigate further:** exact v2 spectrum/FFT algorithm, true metadata/tag support, alarm semantics, any omitted v2 updater implementation, complete v4 Matrix E2EE requirements, and whether Strava integration is an intended maintained feature or a local historical bot.

## 10. Proposed nexIRC 5 architecture

The following boundaries improve the user’s initial sketch while keeping its intent:

```text
nexIRC.App / composition root
├── nexIRC.Core
│   ├── Protocol (framing, tags, prefix, commands, numerics, raw events)
│   ├── Transport contracts (IConnection, IDnsResolver, ITlsPolicy)
│   ├── ServerSession and connection state machine
│   ├── Channels, users, queries, topics, modes, permissions
│   ├── CAP / ISUPPORT / ServerFeatureSet / ServerProfiles / quirks
│   ├── Commands, aliases, highlights, ignore, notify, logging contracts
│   ├── Application events/state projections
│   ├── Workspace/conversation model
│   └── Configuration models and migration contracts
├── nexIRC.Networking
│   └── TCP/TLS/DNS/proxy/reconnect implementations
├── nexIRC.Dcc
├── nexIRC.Media
│   ├── metadata/tag extraction
│   ├── playlist/now-playing policy
│   ├── announcement formatting
│   ├── playback/audio-device contracts
│   └── visualizer/spectrum contracts
├── nexIRC.Matrix
│   ├── Client-Server API
│   ├── sync/event model
│   ├── room/session state
│   ├── bridge mapping/delivery/loop prevention
│   └── optional E2EE provider boundary
├── nexIRC.Bots
│   ├── command registry/permissions/rate limits
│   ├── services bots
│   ├── running/fitness bot modules
│   └── external API contracts/adapters
├── nexIRC.Extensions
│   ├── sandboxed script/automation runtime
│   └── extension contracts and package loading
├── nexIRC.Persistence
│   ├── configuration/settings migration
│   ├── secure credential references
│   ├── logs/history
│   └── layout/theme storage
├── nexIRC.Desktop.Avalonia
│   ├── MDI workspace host
│   ├── tab/sidebar workspace host
│   ├── detached windows
│   └── platform adapters
├── nexIRC.guideXOS
│   └── guideXOS App Model host/platform adapters
└── tests
    ├── Core protocol/state tests
    ├── Matrix/bridge contract tests
    ├── DCC/security tests
    └── UI smoke/accessibility tests
```

### Core design rules

* `nexIRC.Core` must not reference Avalonia, WPF, guideXOS, Windows-only namespaces, platform media APIs, or UI controls.
* `ServerSession` owns its own connection, identity, capability snapshot, server profile, channel collection, query collection, pending requests, desired-state recovery plan, and outbound command queue.
* `ConnectionManager` owns many `ServerSession` instances. A UI’s selected server/conversation is a view concern, never a global protocol singleton.
* State changes should be emitted as typed events or reduced into immutable/observable projections. UI hosts subscribe and marshal to their own thread.
* Raw lines and unknown numerics must remain inspectable even when no semantic handler is installed.
* Server profiles are declarative data: known network/IRCd hints, mode grammar defaults, quirks, and optional service conventions. Runtime CAP/005/004 discovery has precedence; manual overrides have explicit precedence and auditability.
* Mode handling must use negotiated `PREFIX`/`CHANMODES` and a generic mode parser, not fixed `+o/+v/+b/+l` assumptions.
* Reconnect restores desired state through a state machine: registration → capability negotiation → identity confirmation → auto-join → channel/user resynchronization → queued actions. It must not pretend old channel state is still authoritative.

### CAP/ISUPPORT/profile flow

```text
TCP/TLS line
  → IRC framer/parser
  → CAP LS/ACK/NAK/NEW/DEL state
  → registration state machine
  → 004/005 parser
  → ServerFeatureSet snapshot
  → generic mode/command/format services
  → session state/events/UI projections
```

Preserve a `RawServerMessage` for every input. Parse CAP and ISUPPORT into a normalized dictionary with token values, source, timestamp, and confidence. Seed from a known profile only before/alongside discovery; never silently override negotiated server behavior.

## 11. guideXOS App Model capability checklist

This is a requirements checklist for the future host, not a guideXOS modification request.

| Capability | Minimum nexIRC | Feature parity | Optional/future notes |
|---|---|---|---|
| DNS resolution | Required | Required | Async cancellation and proxy/SRV support are desirable |
| TCP sockets | Required | Required | Multiple concurrent sockets for multiple servers/DCC |
| TLS | Required for modern IRC | Required | Certificate policy, diagnostics, pinning/overrides if needed |
| Async operations/tasks | Required | Required | Cancellation, timeouts, bounded queues |
| Threads/task continuations | Required for background I/O | Required | UI thread marshalling at host edge |
| HTTP/HTTPS | Not for IRC-only minimum | Required for Matrix and external APIs | Secure proxy and timeout controls |
| WebSocket | Not minimum if Matrix uses `/sync` | Required if selected Matrix transport/features need it | Keep Matrix transport abstract |
| Filesystem app data | Required | Required | Logs, settings, playlists, layouts, themes, downloads |
| Settings storage | Required | Required | Versioned migrations and atomic writes |
| Secure credential storage | Required for passwords/tokens | Required | Never put raw credentials in shared config |
| Timers/scheduling | Required | Required | Reconnect, notify, scripts, bots, media, Matrix sync |
| Clipboard | Useful for minimum UX | Required for parity | Copy/paste, URLs, channel/user actions |
| Notifications | Useful for minimum UX | Required for parity | Mentions, private messages, DCC, connection changes |
| Audio playback | Not minimum IRC | Required for v2 media parity | Must be host adapter, not core |
| System media integration | Optional | Optional/future | Now-playing/system controls |
| Multiple windows | Optional minimum | Required for full MDI/detached views | Need shared state/window activation |
| Window activation | Optional minimum | Required for notifications/pop-outs | Deep-link to conversation |
| Application lifecycle | Required | Required | Suspend/resume, shutdown, reconnect policy |
| URI handling | Useful | Required for browser/link actions | External browser or host browser surface |
| File dialogs | Useful | Required for DCC/media/import/export | Host abstraction |
| Background activity | Useful for reconnect/notify | Required for parity | guideXOS policy/limits need confirmation |
| Font/text rendering | Required | Required | IRC formatting, Unicode, accessibility |
| Image decoding | Not minimum | Required for theme/image sharing | Bounded/safe decoding |
| Accessibility | Future | Future parity | Semantics, keyboard, high contrast, screen readers |
| Native library loading | Not minimum | Required only if E2EE/media adapter uses native code | Sandboxed ABI/version policy |
| Inbound listening/NAT support | Not minimum | Required for some DCC modes | Passive DCC fallback should be supported |

The minimum host contract should be specified before the guideXOS frontend begins: async network streams, TLS, secure settings/secrets, app data storage, lifecycle, timers, text rendering, notifications, and a window/view-host model are the critical blockers.

## 12. Avalonia frontend considerations

* Keep `nexIRC.Desktop.Avalonia` as a host and projection layer. View models may use Avalonia abstractions; `nexIRC.Core` may not.
* Use a shared conversation view model/projection for server, channel, query, DCC, Matrix room, and diagnostic conversations.
* Make rich IRC formatting a platform-neutral document/span model first. Avalonia then renders spans; guideXOS gets its own renderer.
* Keep file dialogs, clipboard, notifications, URI launch, media playback, native image decoding, and secure storage behind host service interfaces.
* Build the tab/sidebar presentation with a real hierarchical tree: account/server → status/system → channels → queries/other conversations. v4’s vertical tab list is a visual reference, not a complete model.
* Avoid binding directly to mutable socket objects. Bind to observable projections with explicit unread/mention/connection status.
* Test Linux/macOS behaviors early: font metrics, window detachment, URI/file dialogs, notifications, native media, and TLS certificate UI can differ.

## 13. MDI and tabbed/sidebar workspace architecture

The intended model is sound:

```text
ServerSession/application state
            │
      WorkspaceState
  ┌─────────┴─────────┐
  MDI presentation   Tab/sidebar presentation
```

### Shared model

`WorkspaceState` should contain stable conversation IDs, conversation descriptors, server/account grouping, ordering, selected conversation, unread/mention counts, and presentation-independent commands. A conversation is not a window or tab; it is a durable state/projection that can be hosted by either layout.

### Classic MDI

* `MdiWorkspaceState` stores child bounds, z-order, minimized/maximized state, visibility, active conversation, and optional per-conversation docking/panel state.
* A child host binds to the same conversation view model used by the tabbed layout.
* Tile/cascade/arrange/minimize-all are presentation commands over the child layout, not protocol commands.
* Closing an MDI child hides/detaches the view by default; it does not disconnect or destroy the session unless the user explicitly closes the conversation/session.

### Tabbed/sidebar

* `SidebarWorkspaceState` stores server/group/channel/query order, expanded nodes, selected conversation, tab order, pinned tabs, unread/mention counts, and optional compact/vertical tab settings.
* The central content host swaps the active conversation view without changing its model.
* A server/channel hierarchy must be represented even if the initial UI also offers a flat vertical tab list.

### Runtime switching and detached windows

* Switching layout rehosts conversation view models in a new presentation tree; it never reconnects IRC/Matrix.
* Persist layout state separately from session configuration so a layout can change without mutating server credentials or desired channel state.
* Detached/pop-out windows are additional hosts of a conversation ID. On close they return to the workspace or remain a hidden host according to policy.
* A future hybrid mode can show a sidebar/tree plus floating MDI children because the model already separates state from presentation.

## 14. Suggested implementation phases

### Phase 0 — completed by this document

Archaeology, feature matrix, salvage/debt inventory, unanswered questions, risk register, and first milestone.

### Phase 1 — protocol kernel

Create the new solution and `nexIRC.Core`/`nexIRC.Networking`. Implement tested framing/parser, raw messages, typed commands/numerics, CAP negotiation, 005/ISUPPORT, profile/quirk model, session state machine, and one-server TLS vertical slice. No legacy code translation is required.

### Phase 2 — core IRC application behavior

Add multi-server session manager, channel/query/user state, modes, WHOIS/WHO/LIST/NAMES, notices, CTCP, away/notify/ignore/highlights, logging, notifications contracts, auto-join, reconnect/resync, command history, aliases, and secure configuration.

### Phase 3 — dual Avalonia workspaces

Implement shared workspace/conversation projections, then MDI and tab/sidebar hosts. Add layout persistence, window commands, detached views, theming, rich text, and cross-platform smoke testing.

### Phase 4 — DCC and services

Implement secure DCC chat/file transfer, passive/active negotiation, cancellation/progress, file policy, sandboxed downloads, and configurable services actions.

### Phase 5 — media personality

Define media interfaces and adapters; add metadata/tag extraction, playlists/import, now-playing templates, channel policies, playback controls, audio-device integration, and visualizer abstraction. Validate the exact v2 spectrum behavior before selecting an algorithm.

### Phase 6 — Matrix and bridge

Implement Matrix endpoint/session, sync, room state, message formatting, durable mapping, delivery/retry, origin-based loop prevention, permissions, and optional E2EE provider. Test bridge recovery and duplicate/replay scenarios before UI polish.

### Phase 7 — bots, scripting, extensions

Add transport-independent bot registry, permissions/rate limits, NickServ/ChanServ modules, running/fitness modules, external API adapters, scheduler, and sandboxed scripts/extensions.

### Phase 8 — guideXOS host and parity

Implement guideXOS adapters after the capability contract is confirmed. Port workspace/UI behavior, notifications, secure storage, file dialogs, media, lifecycle/background rules, and accessibility progressively.

## 15. Explicit unanswered questions

1. Which v2 media visualization algorithm/control produced the advertised spectrum/oscilloscope, and was it in the missing/compiled MP3 ActiveX control or in source no longer active?
2. Did v2’s now-playing system ever extract ID3 metadata, or did it only use filename/codec labels?
3. Is the v2 embedded IRC server still a desired future product feature, an experiment, or strictly historical archive material?
4. Which exact IRC networks/IRCd variants must be supported at v5 launch, and which v3 compatibility profiles remain relevant?
5. What minimum CAP set is required—multi-prefix, SASL, message-tags, server-time, account-tag, echo-message, batch, labeled-response, chathistory, draft features?
6. Should SASL mechanisms and credential flows be in core protocol or a separately optional authentication package?
7. Are DCC active connections acceptable on guideXOS, or must v5 prioritize passive DCC/proxy/relay alternatives?
8. Is Matrix support required for v5 minimum, or is it a post-IRC milestone? Is E2EE required for initial Matrix support?
9. Which Matrix API versions/servers and event types must be supported, including edits, reactions, replies, media, redactions, threads, and encrypted rooms?
10. Is `!strava`/fitness functionality a maintained nexIRC product requirement or a local deployment-specific bot? Which external APIs and permission model are intended?
11. Should scripting be trusted local automation, a sandboxed extension runtime, or both with separate capabilities?
12. What settings/import compatibility is desired with v2/v3/v4 INI files, and which legacy secrets must never be imported automatically?
13. What is the intended guideXOS window model: native multiple windows, one shell with child surfaces, or a hybrid?
14. Does guideXOS expose secure credentials, background sockets, inbound listeners, audio, notifications, and image/font services with the semantics DCC/media need?
15. What Team Nexgen assets are legally/technically intended for v5 reuse, and which should be recreated as new assets?

## 16. High-risk areas

* **IRC correctness:** framing, UTF-8, tags, line limits, CAP/SASL, 005 tokens, case mapping, mode grammar, numerics, and server quirks.
* **Recovery:** reconnect without duplicate messages, stale users, lost joins, incorrect topic/mode state, or replayed bridge events.
* **DCC security/networking:** inbound listeners, NAT, file path traversal, malicious filenames, partial transfers, cancellation, and executable/script handling.
* **Matrix:** long-sync lifecycle, rate limits, retries, room membership, event ordering, edits/replies/threads, token security, and E2EE/key management.
* **Bridge semantics:** identity mapping, loop prevention, duplicate suppression, formatting loss, rate/length limits, and offline delivery.
* **Cross-platform UI/platform services:** MDI/detached windows, system notifications, secure storage, font/rendering differences, media, and dialogs.
* **Media/visualizer:** codec availability, metadata correctness, device access, CPU use, audio routing, and portability.
* **Automation/extensions:** privilege boundaries, malicious scripts, secrets, network/file access, and versioned APIs.
* **Persistence:** schema migrations, encrypted secrets, crash-safe writes, import compatibility, and separation of settings from layout/history.

## 17. Recommended first implementation milestone

Create the new v5 solution with no references to v2/v3/v4 and deliver a headless testable slice:

1. `IByteTransport`/`IConnection` and a CRLF IRC line framer with maximum-line enforcement.
2. A grammar-correct IRC message model preserving raw input, tags, prefix, command, middle parameters, trailing parameter, unknown commands, and unknown numerics.
3. A registration/CAP state machine supporting CAP LS/REQ/ACK/NAK and configurable SASL later.
4. An ISUPPORT parser and immutable `ServerFeatureSet` for at least CASEMAPPING, PREFIX, CHANTYPES, CHANMODES, NETWORK, LINELEN, MAXLIST, EXCEPTS, INVEX, STATUSMSG, and UTF8ONLY.
5. A `ServerSession` that owns identity, connection state, feature set, channels, queries, users, raw events, and a serialized outbound queue.
6. A deterministic fake transport and tests for fragmented reads, multiple messages per read, empty/trailing parameters, tags, unknown numerics, mode grammar, CAP ordering, registration failures, and reconnect/resync.
7. A minimal console/headless harness that can connect to a test IRC server or replay captured lines and print state/events.

This milestone directly addresses the most damaging weaknesses found in all three donors and gives both future Avalonia and guideXOS hosts a stable contract to consume.

## 18. Exact paths examined and audit artifact

### v2 paths

* `D:\dev\nexIRC\nexIRC2\NexIRC.vbp`
* `D:\dev\nexIRC\nexIRC2\mdiMain.frm`, `mdiNexIRC.frm`, `frmStatus.frm`, `frmChannel.frm`, `frmQuery.frm`
* `D:\dev\nexIRC\nexIRC2\mdlIRCStuff.bas`, `mdlStatus.bas`, `clsChannel.cls`, `mdlModes.bas`
* `D:\dev\nexIRC\nexIRC2\mdlIRCServer.bas`, `frmIRCServer.frm`, `modIRCUserCommands.bas`, `modOperServ.bas`
* `D:\dev\nexIRC\nexIRC2\mdlMultimedia.bas`, `mdlMultimediaInterface.bas`, `clsMovieX.cls`, `clsMovieX1.cls`, `MovieX.ctl`, `frmPlayer.frm`, `frmPlaylist.frm`, `frmMixer.frm`, `frmMobileMixer.frm`
* `D:\dev\nexIRC\nexIRC2\clsFMenu.cls`, `mdlAutoConnect.bas`, `mdlAutoJoin.bas`, `mdlAutoPreform.bas`, `mdlBotCommands.bas`, `mdlIgnore.bas`, `mdlNotify.bas`, `mdlPortScan.bas`, `mdlCrypt.bas`, `mdlFunctions.bas`
* `D:\dev\nexIRC\nexIRC2\ctlTBox.ctl`, `TBox.ctl`, `ctlListView.ctl`, `ucCoolList.ctl`, `mdlRTF2HTML.bas`
* `D:\dev\nexIRC\nexIRC2\data\config\settings.ini`, `fixed\servers.ini`, `fixed\botcommands.ini`, `fixed\scripts.ini`, `fixed\spectrum.ini`, `themes\blue.pak`, `themes\pink.pak`
* `D:\dev\nexIRC\nexIRC2\data\scripts\nexirc\`, `data\help\`, `data\playlists\`, `data\sounds\`, `data\images\`

### v3 paths

* `D:\dev\nexIRC\nexIRC3\nexIRC.sln`, `nexIRC\nexIRC.vbproj`
* `D:\dev\nexIRC\nexIRC3\Classes\Communications\AsyncSocket.vb`, `StatusSocket.vb`, `Classes\clsScript.vb`
* `D:\dev\nexIRC\nexIRC3\IRC\Status\Status.vb`, `IRC\Channels\clsChannels.vb`, `clsChannelList.vb`, `clsChannelFolder.vb`
* `D:\dev\nexIRC\nexIRC3\IRC\Numerics\clsProcessNumeric.vb`, `clsIrcNumericHelper.vb`, `clsIrcNumerics.vb`, `IrcStrings.vb`
* `D:\dev\nexIRC\nexIRC3\IRC\DCC\clsDccChat.vb`, `Forms\frmDCCSend.vb`, `frmDCCGet.vb`, `frmDCCChat.vb`
* `D:\dev\nexIRC\nexIRC3\IRC\Settings\Settings.vb`, `clsDCC.vb`, `clsServices.vb`, `IRC\Services\Bots.vb`
* `D:\dev\nexIRC\nexIRC3\Forms\mdiMain.vb`, `mdiMain.Designer.vb`, `Forms\frmVideoPlayer.vb`, `IRC\MainWindow\clsMainWindowUI.vb`
* `D:\dev\nexIRC\nexIRC3\data\config\compatibility.ini`, `commands.ini`, `services.ini`, `dcc.ini`, `networks.ini`, `servers.ini`, `settings.ini`, `data\script\`
* `D:\dev\nexIRC\nexIRC3\nexIRC.Core\` — untracked C# experiment, not part of the canonical solution

### v4 paths

* `D:\dev\nexIRC\nexIRC4\nexIRC4.sln` and all `*.csproj` files listed in the inventory
* `D:\dev\nexIRC\nexIRC4\nexIRC.IrcProtocol\Client.cs`, `ParsedIRCMessage.cs`, `IRCPrefix.cs`, `IRCCommand.cs`, `IRCNumericReply.cs`
* `D:\dev\nexIRC\nexIRC4\nexIRC.IrcProtocol\Connection\TcpClientConnection.cs`, `WebSocketClientConnection.cs`, `Messages\Handlers.cs`, `MessageHandlerContainer.cs`, `Ctcp\CtcpCommands.cs`
* `D:\dev\nexIRC\nexIRC4\nexIRC.MatrixProtocol\MatrixClient.cs`, `MatrixClientFactory.cs`, `Wrapper\MatrixWrapper.cs`, `Wrapper\MessageHelper.cs`, `Core\Domain\Services\PollingService.cs`, `Core\Domain\MatrixRoom\MatrixRoomEventFactory.cs`
* `D:\dev\nexIRC\nexIRC4\nexIRC.Olm\OlmHelper.cs`, `Core\`, and native Olm library directories
* `D:\dev\nexIRC\nexIRC4\nexirc4\Views\MainWindow.xaml`, `ChannelView.xaml`, `QueryView.xaml`, `ServerView.xaml`, `SettingsWindow.xaml`
* `D:\dev\nexIRC\nexIRC4\nexirc4\ViewModels\MainViewModel.cs`, `ServerViewModel.cs`, `ChannelViewModel.cs`, `QueryViewModel.cs`, `TabItemViewModel.cs`
* `D:\dev\nexIRC\nexIRC4\nexirc4\Properties\Settings.settings`, `README.md`
* Pre-existing dirty file: `D:\dev\nexIRC\nexIRC4\nexIRC4\ViewModels\ChannelViewModel.cs`

### Durable report created

This document is the only file created during the audit:

`D:\dev\nexIRC\nexIRC5\docs\Phase0-Archaeology.md`

No v2, v3, or v4 source file was modified. 
