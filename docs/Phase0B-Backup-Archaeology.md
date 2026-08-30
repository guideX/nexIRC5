# Phase 0B — Backup Archaeology and Lost Feature Recovery

Date: 2026-08-30  
Scope: read-only archaeology of `D:\dev\nexIRC\bkup`, compared with the canonical `nexIRC2`, `nexIRC3`, and `nexIRC4` trees.  
Status: final bounded archaeology pass; no nexIRC 5 implementation was started.

## Executive result

This pass found 15 candidate source roots, which reduce to 12 distinct meaningful source snapshots after exact duplicate groups are collapsed. The set contains nine meaningful VB6-era snapshots, one older VB.NET v3 snapshot, and two distinct v4 Git-backed snapshots. The directory named `nexIRC4_Corrupted` is not empty evidence: its working tree has deleted files, but its Git `HEAD` still contains a distinct 2025 source tree that was inspected through Git objects without checking it out.

The major conclusions are:

- The v2 visualizer was a real historical feature. It was driven by compiled ActiveX/COM controls (`Mp3OCX.ocx` and `VFmp3player.ocx`) that raised playback-frame and peak events and rendered the spectrum/waveform. The backup contains the integration contract and event handlers, but no FFT, PCM/sample callback, bitmap renderer, OCX, or usable typelib implementation. The algorithm is therefore not recoverable from these backups.
- v2 exposed artist/title and codec statistics through `Mp3OCX` properties, but no source-level ID3/tag parser was found. Album, track, and genre extraction were not evidenced. IRC announcements still appear primarily filename/file-offer based; the `GetMP3Info` path clearly updates the UI, but does not prove that artist/title were announced into IRC.
- M3U loading remains a stub in the early and canonical v2 trees. It was not a lost complete implementation.
- Alarm, updater, media search, generic DCC/file offering, scripting hooks, dynamic menus, the embedded IRC server, themes, and the audio-server/file-offer surface are present in the canonical v2 tree. They are not missing features that need to be recovered from a backup.
- One genuinely unusual lost v2 subsystem is “Telos”: a separate Telnet server/administration shell with login, multiple users, filesystem commands, and shutdown controls. It is historically interesting but not a safe v5 donor.
- The older v3 tree does not contain a deeper daemon compatibility implementation. `ProcessISUPPORT` remains a TODO, and there are no runtime UnrealIRCd/Hybrid/Bahamut/ircu handler classes, CAP parser, or PREFIX/CHANTYPES/CHANMODES model.
- v4’s Matrix bridge evolved through several revisions. The backups show real loop suppression, reply formatting, single-room/channel mapping, multiple-nickname support, Matrix polling with `next_batch`, and encrypted-event plumbing. They do not show a complete multi-room/multi-server bridge or working Megolm decryption.
- v4 running/fitness behavior is more concrete than Phase 0 could establish: pace calculation and hard-coded `##running` commands survived, while leaderboard/Strava call sites are active in one historical Git snapshot but commented out in the current canonical working tree. The referenced `nexIRC.Strava` provider source is absent from the recovered snapshot, so the API implementation is not recoverable here.

Nothing in the backup pass changes the architectural decision that nexIRC 5 is a clean new C#/.NET application. Legacy material is behavior, UX, protocol, and asset evidence only.

## 1. Backup tree inventory

Dates below use archive timestamps and source/Git history where available. Extracted filesystem timestamps were treated as weaker evidence because several archives were re-extracted later.

| ID | Exact path | Likely generation/date | Language/project | Completeness and relationship | Meaningful unique source |
| --- | --- | --- | --- | --- | --- |
| B01 | `D:\dev\nexIRC\bkup\src(nexirc_20b6_functioning_dcc)\Source` | 2004-05-26 | VB6; nexIRC 2.0 beta lineage | Small early source tree; DCC-labelled variant | Early functioning-DCC experiment |
| B02 | `D:\dev\nexIRC\bkup\src(nexirc_20b6_functioning_dcc2)\Source` | 2004-05-26 | VB6; nexIRC 2.0 beta lineage | Distinct early variant, with a much larger packaged payload | Variant of the early DCC/media tree |
| B03 | `D:\dev\nexIRC\bkup\src(nexirc20b6c)` | 2004-05-28 archive | VB6; 20b6c | Small distinct early source tree | Early UI/media/config experiments |
| B04 | `D:\dev\nexIRC\bkup\src(NexIRC)\NexIRC\Backup\failed src\Source` | Internal source dates 2004-07-25 to 2004-08-01 | VB6; full-ish nexIRC 2.x | Substantially complete but explicitly labelled failed source | External visualizer integration, spectrum theme module, decoder wrapper, Telos/Telnet subsystem |
| B05 | `D:\dev\nexIRC\bkup\src(NexIRC)\NexIRC\Backup\NexIRC 2.0 Beta 5 (Non Functioning DCC)` | 2004-02-02 | VB6; beta 5 | Older partial tree | Early skin editor, background web page, playlist model, contact surface |
| B06 | `D:\dev\nexIRC\bkup\src(NexIRC)\NexIRC\Backup\NexIRC 2.0 Beta 6 (Non Functioning DCC)` | 2004-06-04 | VB6; beta 6 | Older partial tree | Transitional DCC/UI variant |
| B07 | `D:\dev\nexIRC\bkup\src(NexIRC)\NexIRC\Source` | Internal source dates 2004-08-31 to 2005-05-03 | VB6; mature nexIRC 2.x | Largest full old source tree | Mature media, DCC, scripting, themes, embedded server, file offering |
| B08 | `D:\dev\nexIRC\bkup\src(nexirc225_indev)\NexIRC\Backup\failed src\Source` | Same source lineage as B04 | VB6 | Exact content duplicate of B04 after build/editor noise is excluded | No unique source; redundant copy |
| B09 | `D:\dev\nexIRC\bkup\src(nexirc225_indev)\NexIRC\Backup\NexIRC 2.0 Beta 5 (Non Functioning DCC)` | Same source lineage as B05 | VB6 | Exact content duplicate of B05 | No unique source; redundant copy |
| B10 | `D:\dev\nexIRC\bkup\src(nexirc225_indev)\NexIRC\Backup\NexIRC 2.0 Beta 6 (Non Functioning DCC)` | Same source lineage as B06 | VB6 | Exact content duplicate of B06 | No unique source; redundant copy |
| B11 | `D:\dev\nexIRC\bkup\src(nexirc225_indev)\NexIRC\Backup\Cabral IRC262939112001` | Approximately late 2004 | VB6/older Cabral-derived tree | Smaller distinct ancestor/parallel tree | Raw window, color editor, contact and DCC forms |
| B12 | `D:\dev\nexIRC\bkup\src(nexirc225_indev)\NexIRC\Source` | Internal source dates 2005-03-30 to 2005-04-23 | VB6; nexIRC 2.25 in development | Full later tree; not byte-identical to B07 | Later external-player integration and mature v2 behavior |
| B13 | `D:\dev\nexIRC\bkup\nexIRC3_` | Source/Git material around 2017 | VB.NET; nexIRC 3 | Older source/Git snapshot; roughly 203 meaningful source files after excluding build/editor artifacts | Confirms the same compatibility-catalogue architecture and the same `ProcessISUPPORT` TODO |
| B14 | `D:\dev\nexIRC\bkup\nexIRC4_` | Git `HEAD` 2024-10-26 | C#/.NET WPF; nexIRC 4 | Working source snapshot with older Git history and build/editor material | Matrix bridge revisions, Olm wrapper, event aggregator/socket utility, running-channel behavior |
| B15 | `D:\dev\nexIRC\bkup\nexIRC4_Corrupted\nexIRC4_Corrupted` | Git `HEAD` 2025-11-11 | C#/.NET WPF; nexIRC 4 | Working tree files are deleted, but Git `HEAD` contains 227 meaningful tracked paths; inspected without checkout | Later bridge/protocol state and active leaderboard/Strava call sites; provider source absent |

Other backup material:

- `D:\dev\nexIRC\bkup\archives` is an archive container, not a separate source generation. It contains the archive forms corresponding to the extracted v2/v4 trees above; the seven archive files were inventoried but not repeatedly deep-scanned after their extracted trees were identified.
- `D:\dev\nexIRC\bkup\ControlzEx.dll` is a standalone compiled dependency, not a nexIRC source tree.

## 2. Duplicate and redundant snapshot identification

The old source roots were fingerprinted using a sorted manifest of relative path, file length, and SHA-256 content hash. `.git`, `.vs`, `bin`, `obj`, `packages`, and other build/editor output were excluded. This was used to avoid treating extraction noise as a historical feature.

The exact duplicate groups are:

| Duplicate group | Fingerprint prefix | Meaning |
| --- | --- | --- |
| B04 = B08 | `23b07d...` | Same failed-source v2 tree |
| B05 = B09 | `433cad...` | Same beta 5 tree |
| B06 = B10 | `fe69e9...` | Same beta 6 tree |

The result is 12 distinct meaningful snapshots from 15 candidate roots. B15 is counted as meaningful because its Git object database preserves source even though its working tree is deleted. It was not checked out, restored, or otherwise modified.

## 3. Important unique snapshots

The most valuable historical sources were B04, B07, B12, B13, B14, and B15.

- B04 is the key v2 archaeology source. It contains the clearest active external-player event handlers, the standalone `mdlSpectrum.bas` theme manager, and the Telos/Telnet subsystem.
- B07 is the broad mature v2 donor. It preserves the integrated media, DCC, scripting, dynamic-menu, theme, embedded-server, and file-offering surfaces.
- B12 is the later 2.25 source and gives the clearest line-level evidence for `GetFileInfo`, artist/title, bitrate/sample-rate display, and MP3 peak/frame events.
- B13 is the only meaningful older v3 tree. It confirms that the canonical v3 compatibility abstraction did not evolve into daemon-specific runtime handlers.
- B14 preserves the readable v4 development history, including bridge-loop and reply-to fixes, automatic connection work, and the incomplete encryption series.
- B15 provides a later v4 Git snapshot through objects only. It retains the Matrix/Olm code and the fitness/leaderboard call sites, but not a recoverable `nexIRC.Strava` project.

## 4. v2 visualizer findings

### What actually worked

B04 and B12 show a working integration contract around a compiled `Mp3OCX` control:

- `D:\dev\nexIRC\bkup\src(NexIRC)\NexIRC\Backup\failed src\Source\mdiMain.frm:2-7` declares `msscript.ocx`, `MSWINSCK.OCX`, `xplook.ocx`, `VFmp3player.ocx`, and `Mp3OCX.ocx`.
- `D:\dev\nexIRC\bkup\src(NexIRC)\NexIRC\Backup\failed src\Source\mdlMultimediaInterface.bas:376-412` sets the MP3 control band count and selects `otSpectrum` or `otWave`, then starts/stops/pauses playback through the control.
- `D:\dev\nexIRC\bkup\src(NexIRC)\NexIRC\Backup\failed src\Source\mdiMain.frm:2037` toggles peak display; `:2130` handles frame notifications; `:2140` handles peak events; `:2171` handles playback start; `:2178` handles the player thread ending; and `:3398` changes the visualizer mode.
- `D:\dev\nexIRC\bkup\src(nexirc225_indev)\NexIRC\Source\mdiMain.frm:2094-2168` contains the corresponding later event handlers. `FrameNotify` updates playback progress, `PeakFound` drives the peak response, and `Started` displays codec information.
- The peak handler conditionally moves the nexIRC logo by small offsets, pauses briefly, and moves it back. This is the historical “logo twitch” behavior, not a generic animation system.
- A second external player path uses `VFmp3player1`, `VFmp3scope`, and two `VFmp3level` controls. `PlayVisFX` assigns a song name and starts the player; `frmPlaybackProgress.frm` uses the player’s duration tag and `Seek` method.

### What was not recovered

`D:\dev\nexIRC\bkup\src(NexIRC)\NexIRC\Backup\failed src\Source\mdlSpectrum.bas` is a theme/configuration module, not a DSP implementation. It stores and applies band count, background, divider, left/right channel, peak, top/bottom band colors, and a toolbar graphic. It contains no FFT, PCM reader, sample callback, windowing function, peak detector, bitmap drawing loop, or spectrum coordinate calculation.

The relevant project files refer to external controls, but no matching `Mp3OCX.ocx`, `VFmp3player.ocx`, `MDec.ocx`, source, usable typelib, or equivalent implementation was found in `bkup`. The canonical v2 tree retains commented-out `ctlMP3OCX` calls and spectrum-theme state (`D:\dev\nexIRC\nexIRC2\mdlMultimediaInterface.bas:408-417,472-494` and `D:\dev\nexIRC\nexIRC2\mdiNexIRC.frm:1825-1858,2128-2197`), but not the underlying control.

### Classification

| Question | Finding |
| --- | --- |
| Historical behavior | Recoverable: wave/spectrum modes, bands, colors, peaks, progress, codec display, logo twitch |
| Source algorithm | Not recoverable |
| Dependency type | Windows ActiveX/COM integration; compiled custom/third-party player controls, vendor unknown |
| Rendering location | Inside the missing control, not in the VB6 source |
| Peak detection location | Inside the missing control; VB6 only receives `PeakFound` |
| v5 disposition | SALVAGE BEHAVIOR; reimplement behind `nexIRC.Media` later, do not port the OCX contract or guess the FFT |

This resolves the Phase 0 uncertainty at the architecture level: the visualizer was not imaginary, but it was never a portable in-tree algorithm in the surviving source.

## 5. v2 media metadata and now-playing

The strongest evidence is in the later B12 source:

- `D:\dev\nexIRC\bkup\src(nexirc225_indev)\NexIRC\Source\mdlMultimediaInterface.bas:452-465` calls `mdiMain.ctlMP3OCX.GetFileInfo`, then reads `.Artist` and `.Title` to populate the secondary filename label, with filename fallback.
- `D:\dev\nexIRC\bkup\src(nexirc225_indev)\NexIRC\Source\mdiMain.frm:2149-2168` handles player start/end, and line `2161` reads `.BitRate` and `.SamplesPerSecond` for the codec/status label.
- The canonical implementation retains the same behavior only as commented code at `D:\dev\nexIRC\nexIRC2\mdlMultimediaInterface.bas:472-474` and `D:\dev\nexIRC\nexIRC2\mdiNexIRC.frm:2180-2197`.
- Duration and position are also available as playback-control/frame/MCI values. That is playback metadata, not evidence of ID3 extraction.

No source in the distinct v2 snapshots or canonical v2 tree contains an ID3 parser, TagLib-style reader, album/genre/track parser, MPEG tag-frame reader, or metadata cache. A search for `ID3`, album, genre, track, tag parsing, and MP3 metadata implementations did not produce a relevant source hit.

| Field | Historical evidence | Confidence |
| --- | --- | --- |
| Artist | External `Mp3OCX.Artist` property | High; control-dependent |
| Title | External `Mp3OCX.Title` property | High; control-dependent |
| Album | No evidence | High that no in-tree implementation was found |
| Track number | No evidence | High that no in-tree implementation was found |
| Genre | No evidence | High that no in-tree implementation was found |
| Duration | MCI/player frame/tag/playback values | High as playback duration; not ID3 |
| Bitrate | External `Mp3OCX.BitRate` property | High; control-dependent |
| Sample rate | External `Mp3OCX.SamplesPerSecond` property | High; control-dependent |

The `ActivatePlayback`/`PlayMP3` paths use `GetFileTitle` and file-offer/announcement settings. `GetMP3Info` demonstrably updates the player UI, but no surviving code proves that artist/title replaced the filename in IRC announcements. The historical now-playing behavior should therefore be described as filename-first, with optional external-control artist/title and codec display.

### Classification

The metadata question is resolved enough for v5 planning but not for binary reproduction: artist/title and codec stats came from an unavailable external control; no ID3 implementation survives; the exact control parser and exact IRC announcement formatting remain unknown.

## 6. Lost v2 features and earlier experiments

| Feature or experiment | Evidence | Present in canonical v2? | Relevance | Disposition |
| --- | --- | --- | --- | --- |
| M3U loading | B05 `mdlPlaylist.bas:79-89` has `LoadM3u` but no parser; canonical `mdlMultimediaInterface.bas` still leaves `LoadM3U` at a read/stub stage | No complete implementation in either | MEDIUM | INVESTIGATE LATER / REIMPLEMENT only if v5 playlists require it |
| Alarm | `frmAlarm.frm`, `mdlAlarm.bas`, timer, date/time matching, and `PlayFile` behavior are present in canonical and mature backups | Yes | MEDIUM | SALVAGE BEHAVIOR as a documented optional utility |
| Updater/update check | `frmSplash.frm:862-869` and related splash/config code implement a legacy HTTP version check | Yes, legacy form | MEDIUM | REIMPLEMENT securely if retained; never copy the old transport/update trust model |
| Media search | `frmSearchThroughPlaylist.frm`, `frmSearchWithinPlaylist.frm`, and settings/config paths exist in canonical and full v2 backups | Yes | MEDIUM | SALVAGE BEHAVIOR |
| Generic image/file sharing | `frmQuickImage.frm` is a preview surface; actual transfer is generic DCC/file offering (`frmSendFile`, `frmFileOffer`, audio-server settings). No dedicated image-sharing protocol was found | Generic DCC/file offer yes; dedicated image sharing no | LOW | ARCHIVE the distinction; do not invent a special image subsystem |
| Decoder | B04 `frmDecode.frm` wraps external `MDec.ocx`; the v2 project references `MDec.ocx` and calls `Decode`/progress events | Wrapper survives in part; binary/algorithm absent | MEDIUM | REIMPLEMENT behind `nexIRC.Media` only if a real use case remains |
| Scripting/event hooks | `mdiNexIRC.frm` declares `msscript.ocx`; `mdlIRCStuff.bas`/`mdlCommands.bas` load event and numeric scripts; `frmScriptManager.frm` manages scripts | Yes | HIGH | SALVAGE BEHAVIOR, with a modern sandboxed extension model |
| Dynamic menus | `clsFMenu.cls`, `frmMenuEditor.frm`, menu scripting, and bitmap menu icons exist | Yes | HIGH | SALVAGE BEHAVIOR |
| Early skin editor | B05 `mdlSkin.bas` and `frmSkinEditor.frm` define up to 100 skins and toolbar image slots, but most image-loading calls are commented or incomplete | No as this early implementation | MEDIUM | ARCHIVE behavior/schema; do not port unfinished VB6 code |
| Background web page | B05 `frmBackgroundWebpage.frm` embeds a WebBrowser and navigates to the old Team Nexgen site | No | LOW | ARCHIVE |
| Contact surface | B05/B06/B11 `frmContacts.frm` provide an Online/contacts tree and mouse actions | No exact equivalent in canonical v2 | LOW | ARCHIVE or reimagine later as a presence/notification utility |
| Telos/Telnet server | B04 `frmTelnet.frm`, `frmTelnetStatus.frm`, and `mdlTelos.bas` implement a Winsock Telnet client/server/admin shell with login, users, filesystem commands, and shutdown | No | LOW | DISCARD as a network feature; ARCHIVE as personality/history |
| Playback progress form | B04 `frmPlaybackProgress.frm:34-47` uses `VFmp3player1.Tag` and `.Seek` | No exact equivalent | MEDIUM | SALVAGE BEHAVIOR |
| Embedded IRC server | B04/B07/B12 and canonical `frmIRCServer.frm` contain an actual server surface with users, channels, modes, K-lines/flood handling, and server windows | Yes | MEDIUM | ARCHIVE as a historical capability; reimplement only as an explicit v5 scope decision |
| DCC | Early snapshots distinguish functioning and non-functioning DCC variants; mature B07/B12 and canonical contain DCC chat/file forms | Yes | MEDIUM | SALVAGE BEHAVIOR; implement anew in `nexIRC.Dcc` |
| Audio server/file offering | Canonical `frmFileOfferSettings.frm` exposes audio-server, file-offer-in-channel, offer-when-played, and download logging settings | Yes | HIGH | SALVAGE BEHAVIOR for nexIRC identity; implement safely later |
| Raw/message utilities | B11 `frmRAW.frm` and B04 `frmMessageServer.frm` are utility/debug surfaces; related raw/status views remain in later trees | Partial | LOW | ARCHIVE or reimagine as diagnostics |

The backup pass found no complete historical implementation for M3U parsing, a new decoder algorithm, dedicated image sharing, or a v2 feature that was silently deleted from the canonical tree and is ready to copy.

## 7. Intermediate v3 architecture and server compatibility

The older v3 source is structurally close to canonical v3:

- `D:\dev\nexIRC\bkup\nexIRC3_\IRC\Numerics\clsProcessNumeric.vb:149-153` dispatches numeric `005` to `ProcessISUPPORT`.
- `D:\dev\nexIRC\bkup\nexIRC3_\IRC\Numerics\clsIrcNumericHelper.vb:196-202` contains the same `ProcessISUPPORT` method with a `TODO` body as canonical v3.
- `clsIrcNumerics.vb` is a broad numeric catalogue and comment/reference table, not a runtime server family implementation.
- `ServerInfo.vb` and `Status.vb` contain useful state/value concepts, including prefix/channel-type fields, supported modes, per-status state, and server links. They do not parse or model the complete ISUPPORT grammar.
- The compatibility form/config is declarative. The known network list includes RFC1459/RFC2812, NEXIRC, KineIRCd, ircu, QuakeNet, Hybrid, IRCnet, Bahamut, AustHex, Ultimate/DreamForge, GameSurge, Ratbox, PTlink, aircd, Unreal, and Charybdis, but there are no corresponding daemon-specific handler classes.
- No completed CAP/SASL runtime, PREFIX/CHANTYPES/CHANMODES token parser, alternate socket/session layer, or deeper UnrealIRCd/Hybrid/Bahamut/ircu implementation appears in B13.

Conclusion: the daemon/server compatibility idea was implemented as a catalogue plus formatting/status hooks, not as a polymorphic server capability architecture. Phase 0 is confirmed. The useful v5 donor is the requirement vocabulary—raw/unknown numerics, server links, mode state, and compatibility labels—not the VB.NET structure.

## 8. Intermediate v4, Matrix, bridge, and bot work

### Matrix bridge revisions

B14 Git history records several concrete bridge iterations:

- `d786242` — fixed Matrix messages appearing in an IRC channel.
- `36abe44` — fixed a `reply_to` glitch across protocol layers.
- `a491789` — GUI/reply-to changes.
- `ee95b32` — encryption/reply-to handling, including a network-specific exception.
- `c6fc7f2` and `f6ee973` — encryption changes explicitly marked as not working yet.
- `ffd5012` — automatic IRC connect-on-startup and error handling.
- `688c883` — additional hard-coded `##running` actions.

The source shows a real bridge, but the bridge is application-wired rather than a general mapping engine:

- `D:\dev\nexIRC\bkup\nexIRC4_\nexIRC.MatrixProtocol\Wrapper\MessageHelper.cs:57-103` normalizes Matrix user IDs, blocks messages from the configured Matrix nickname (`DoubleRelayDetected`), permits relay only from the configured default Matrix room, handles mention/reply text, and contains a special reply formatting exception.
- `D:\dev\nexIRC\bkup\nexIRC4_\nexIRC4\ViewModels\MainViewModel.cs:157-216` filters Matrix events, relays to IRC, supports the multiple-nickname client collection, and handles encrypted events separately.
- `MainViewModel` constructs one Matrix wrapper from settings, joins one configured Matrix channel, and maps it to one default IRC channel. `UseMultipleNicknames` is not the same thing as multiple IRC servers.
- The Matrix library itself is more general than the UI: `PollingService.cs:34,46,72-80,98-127` keeps a concurrent room dictionary, joined/invited/left room state, and a `next_batch` token. `RoomService` exposes joined-room and join/leave operations. The desktop bridge does not expose a complete multi-room mapping configuration.
- Polling has start/stop/cancellation and token state, but no evidence of a complete reconnect/backoff/re-authentication/resync supervisor around Matrix login. IRC auto-reconnect exists as a setting; the bridge-level reconnect behavior remains incomplete.

### Encryption and Olm

The recovered v4 source contains real native bindings, not a completed encryption feature:

- `D:\dev\nexIRC\bkup\nexIRC4_\nexIRC.Olm\OlmHelper.cs:1-211` P/Invokes native `Olm.dll`, including outbound/inbound group session, group decrypt, session decrypt, pickle, and error functions; helper implementations continue at `:218-356`.
- `D:\dev\nexIRC\bkup\nexIRC4_\Libraries\Olm\olm.dll` plus `.lib`, `.exp`, and `.pdb` are compiled native dependencies.
- Matrix domain models parse encryption, room-key, and encrypted events, but `MainViewModel` explicitly marks the decryption path as not working and leaves the actual decrypt call commented out.

Classification: native third-party dependency plus incomplete experiment. It is useful evidence for a future `nexIRC.Matrix` crypto boundary, not reusable secure encryption code.

### Running, leaderboard, Strava, and bot behavior

The historical v4 identity work is more specific than the canonical source alone suggests:

- B15 `HEAD:nexIRC4/ViewModels/ChannelViewModel.cs:127-139` contains an active `ClubleaderboardScraper.Scrape(true)` path for `##runningtesting`, sends the result to Matrix when enabled, and includes an alternate auto-login leaderboard experiment that is commented out.
- B15 `HEAD:nexIRC4/ViewModels/MainViewModel.cs:445-463` contains routing for `!strava speed`, `!strava elev`, `!strava slope`, `!strava`, and `!help` in `##running`.
- The current canonical `D:\dev\nexIRC\nexIRC4\nexirc4\ViewModels\ChannelViewModel.cs:8,127-139` comments out the Strava namespace and leaderboard execution while retaining related call-site comments and the pace calculation. `MainViewModel.cs:445-515` retains the running-channel command routing and pace calculation.
- `D:\dev\nexIRC\nexIRC4\nexirc4\nexIRC4.csproj:81` still references `..\nexIRC.Strava\nexIRC.Strava.csproj`, but no recoverable `nexIRC.Strava` source project is present in B14 or B15 `HEAD`. The working-tree directory in the “corrupted” container is empty and was not restored.
- No general `LinkBot` class, bot framework, Strava API client, leaderboard scraper implementation, or external fitness API client was found. The behavior is hard-coded in view models and depends on missing code.
- `team-nexgen.core` contributes an event aggregator and socket/server helpers. These are useful infrastructure ideas, not evidence of a completed bot framework.

Classification: behavior and command vocabulary are recoverable; the Strava/leaderboard implementation is not. The appropriate v5 home, if wanted, is `nexIRC.Bots` or `nexIRC.Extensions`, with explicit credentials, rate limits, and testable adapters.

### Credential/secret handling note

A path-only scan found credential-like historical material, including literal-looking client-secret/access-token comments in the current canonical v4 `D:\dev\nexIRC\nexIRC4\nexirc4\ViewModels\ChannelViewModel.cs:133-134`. The values are intentionally not reproduced here. Other hits in v2/v3/v4 are primarily password/token input fields and transport parameters, not proof of live secrets. Treat all matching source and backup copies as potentially contaminated: revoke/rotate any still-valid credentials, remove secret material from repository history and backup retention where appropriate, and do not copy values into v5.

## 9. Lost-feature delta matrix

| Backup source | Related canonical version | Unique feature/code | Evidence | Present in canonical tree? | Relevance | Disposition |
| --- | --- | --- | --- | --- | --- | --- |
| B04/B12 | v2 | External MP3/VF visualizer event contract | `mdiMain.frm` MP3OCX events; `mdlMultimediaInterface.bas` visualizer selection/playback | No underlying control; commented vestiges only | HIGH | SALVAGE BEHAVIOR |
| B04 | v2 | Spectrum theme manager | `mdlSpectrum.bas` color/band/theme persistence and apply routines | Partial theme state, no old module/control | MEDIUM | SALVAGE BEHAVIOR |
| B12 | v2 | Artist/title/bitrate/sample-rate properties | `GetMP3Info`, `GetFileInfo`, `Artist`, `Title`, `BitRate`, `SamplesPerSecond` | Partial/commented, external provider absent | MEDIUM | SALVAGE BEHAVIOR |
| B05 + canonical | v2 | M3U loader | `mdlPlaylist.bas:79-89`; canonical M3U stub | No complete implementation | MEDIUM | INVESTIGATE LATER / REIMPLEMENT |
| B04 | v2 | Telos Telnet server/admin shell | `frmTelnet.frm`, `frmTelnetStatus.frm`, `mdlTelos.bas` | No | LOW | ARCHIVE; DISCARD as a network feature |
| B04 | v2 | VF player playback-progress/seek window | `frmPlaybackProgress.frm:34-47` | No exact equivalent | MEDIUM | SALVAGE BEHAVIOR |
| B04 | v2 | MDec decoder wrapper | `frmDecode.frm`; `NexIRC.vbp` references `MDec.ocx` | Wrapper/format support not portable; binary absent | MEDIUM | REIMPLEMENT if needed |
| B05 | v2 | Early skin editor and 100-skin image model | `mdlSkin.bas`, `frmSkinEditor.frm` | No exact early editor | MEDIUM | ARCHIVE behavior/schema |
| B05 | v2 | Embedded Team Nexgen web background | `frmBackgroundWebpage.frm` | No | LOW | ARCHIVE |
| B05/B06/B11 | v2 | Contact tree surface | `frmContacts.frm` | No exact equivalent | LOW | ARCHIVE |
| B11 | v2 | Standalone raw/color/DCC utility forms | `frmRAW.frm`, `frmColors.frm`, DCC forms | Partial equivalents | LOW | ARCHIVE |
| B13 | v3 | Completed `ProcessISUPPORT` or daemon handlers | Same TODO in `clsIrcNumericHelper.vb`; no handler classes | No; confirms Phase 0 | HIGH | SALVAGE REQUIREMENTS, REIMPLEMENT |
| B14 | v4 | Bridge loop/reply suppression revisions | `MessageHelper.cs`; commits `d786242`, `36abe44`, `ee95b32` | Partial and still hard-coded | HIGH | SALVAGE BEHAVIOR / REIMPLEMENT |
| B14/B15 | v4 | Multi-room-capable Matrix polling library | `PollingService.cs` room dictionary and `next_batch` | Library partial; UI maps one room | HIGH | SALVAGE ALGORITHM/CONTRACT |
| B14/B15 | v4 | Olm/Megolm native binding experiment | `nexIRC.Olm\OlmHelper.cs`; `Libraries\Olm` binaries | Present but explicitly incomplete | HIGH | ARCHIVE; REIMPLEMENT with security review |
| B15 | v4 | Leaderboard/Strava call sites | `ChannelViewModel.cs`, `MainViewModel.cs`, missing `nexIRC.Strava` project | Partial/commented; provider absent | MEDIUM | SALVAGE BEHAVIOR; REIMPLEMENT as optional bot extension |
| B14/B15 | v4 | Event aggregator/socket utility | `team-nexgen.core` | Present/parallel utility | LOW | ARCHIVE or reimplement as needed |

## 10. Salvage candidates

The following are the strongest donors for v5 design, expressed as behavior/contracts rather than porting targets:

1. Media identity: preserve a media service boundary that can provide filename, duration, artist/title, bitrate, sample rate, and optional album/genre/track. Treat tag metadata and playback metadata as separate providers.
2. Visualizer identity: preserve the three-mode contract—off, waveform, spectrum—plus band count, colors/theme, peak toggle, playback progress, and optional peak-responsive logo animation. The missing OCX is not a dependency candidate.
3. Media/file identity: preserve “offer when played,” in-channel file offer, playlist search, continuous play, and audio-server concepts as explicit optional services.
4. Script/menu identity: preserve event hooks, numeric hooks, dynamic user menus, and script manager behavior behind a sandboxed, permissioned extension model.
5. v3 protocol identity: preserve an explicit server capability model with raw/unknown token retention, numeric fallback, PREFIX/CHANTYPES/CHANMODES parsing, and server-specific policy data. Do not port the declarative compatibility form as the runtime architecture.
6. Matrix bridge identity: preserve direction/origin metadata, stable IRC-channel↔Matrix-room mapping, sender identity, reply/mention conversion, loop suppression, deduplication, and reconnect/resync state. The `DoubleRelayDetected` idea is useful; the nickname and network-specific hacks are not.
7. Matrix polling identity: the `next_batch` cursor and room-state model are useful concepts for `nexIRC.Matrix`, subject to proper cancellation, backoff, persistence, and multi-room tests.
8. Running/bot identity: preserve pace calculation and the idea of channel-scoped utility commands as optional bot/extension behavior. Do not recover missing credentials or hard-code provider assumptions.
9. Visual identity: preserve theme packs, color customization, image-backed menus, and previews. The early `mdlSkin` implementation is incomplete, but it shows that customization was part of the product identity.

## 11. Newly discovered Team Nexgen personality features

The backups reinforce that nexIRC was more than an IRC text client. Features that contributed to its personality include:

- a music player with spectrum/waveform visualizer modes, peak detection, codec display, and a logo that twitched with peaks;
- now-playing/file-offer behavior, audio-server settings, continuous play, media search, and playlist-oriented channel tools;
- user scripts for IRC events and numerics, dynamic menus with bitmap icons, menu editing, and rich channel nick actions;
- DCC chat/file workflows, an embedded IRC server, server/raw/status windows, and diagnostics/utilities;
- theme/spectrum customization, screenshot/theme previews, color editing, and the abandoned skin editor direction;
- setup wizard, update checking, alarm playback, quick image/preview utilities, and a WebBrowser-based Team Nexgen surface;
- Matrix/IRC bridging with reply/mention handling, multiple nicknames, and dedicated `##running` pace/Strava/leaderboard conveniences;
- the unusual Telos Telnet administration experiment, which is historically distinctive even though it should not be revived as insecure Telnet.

These should form a v5 “personality ledger”: features to consciously preserve, modernize, or explicitly retire. The clean architecture should not accidentally erase them through a narrow protocol-only product definition.

## 12. Corrections and additions to Phase 0

| Phase 0 conclusion | Phase 0B result |
| --- | --- |
| v2 had spectrum/oscilloscope/peak behavior but no complete portable FFT in active source | Confirmed and clarified. Earlier active source used external `Mp3OCX`/`VFmp3player` controls. The missing algorithm is inside the compiled dependency, not merely hidden in an unsearched VB module. |
| v2 metadata path was uncertain | Corrected for precision: historical control integration definitely exposed artist/title and bitrate/sample-rate properties. The tag format/parser and IRC announcement use remain unknown; no ID3 source was found. |
| v2 M3U support was uncertain | Clarified: the old playlist module and canonical loader are stubs/partial scaffolding. No complete parser was recovered. |
| v2 alarm, updater, search, scripting, themes, DCC, and server features were uncertain or potentially lost | Confirmed present in the canonical v2 tree, with backup variants providing historical context. They are not lost-feature recoveries. |
| v3 compatibility may have evolved beyond the canonical tree | Confirmed unchanged: the older v3 copy has the same `ProcessISUPPORT` TODO and no daemon-specific runtime architecture. |
| v4 Matrix/bridge work was incomplete | Confirmed, with more detail: loop suppression, reply formatting, single-room mapping, polling cursor, encrypted-event models, and native Olm bindings existed; robust multi-room mapping, reconnect supervision, and working E2EE did not. |
| v4 Strava/fitness work was uncertain | Clarified: pace and command routing are present; leaderboard/Strava call sites exist in B15 and are disabled/commented in canonical v4; the provider project is missing. |

New v5 requirements or design constraints to add:

- define media metadata and visualization as optional service contracts, independent of UI and independent of any legacy binary;
- define bridge origin/direction/mapping and loop prevention before adding Matrix UX;
- retain raw capability tokens and unknown numerics in the protocol core;
- make scripting, bot, media-sharing, and theme behavior explicit extension points rather than accidental view-model code;
- provide a secure secrets/import policy and refuse historical credential reuse;
- keep the personality ledger in product requirements so modernization does not remove the unusual utilities that made nexIRC recognizable.

No Phase 0 architectural conclusion needs to be reversed. The only correction is the more precise description of v2 metadata and the external nature of its visualizer.

## 13. Remaining unanswered questions

These are now absence/external-dependency questions rather than reasons to continue broad archaeology:

- Which vendor/build produced `Mp3OCX.ocx` and `VFmp3player.ocx`, and did either control parse ID3 tags internally?
- What exact spectrum windowing, band scaling, and peak-threshold algorithm lived inside the missing control?
- Did any release announce artist/title, or only filename and file-offer text, in channel messages?
- What exact M3U behavior was intended? No implementation was found.
- What ISUPPORT/CAP model should v5 support beyond the Phase 0 vertical slice?
- What Matrix room/channel mapping model and E2EE scope are desired for v5?
- Is the running/leaderboard behavior still desired, and can a provider be implemented with current API terms and securely managed credentials?
- What historical theme assets are legally reusable, and which should be recreated or replaced?

None of these requires more general backup excavation. A new targeted pass would be justified only if an additional archive containing the missing OCX binaries/type libraries or the missing `nexIRC.Strava` project becomes available.

## 14. Archaeology completion recommendation

The bounded backup archaeology is complete. Meaningful source roots were deduplicated, the high-value v2/v3/v4 snapshots were inspected, and the remaining uncertainties are either missing external binaries, missing provider source, or deliberately incomplete experiments. Continuing to read redundant copies is unlikely to improve the v5 design decision.

Recommendation: close Phase 0B and begin Phase 1 implementation planning. Do not restore or modernize the old trees as a prerequisite.

## 15. Exact next nexIRC 5 implementation milestone

The next milestone should be the headless IRC protocol vertical slice already identified by Phase 0:

1. `nexIRC.Core`: immutable IRC line framing/parsing, prefixes, parameters, raw/unknown message retention, numeric dispatch, and protocol state.
2. `nexIRC.Networking`: TCP/TLS session, registration state machine, PING/PONG, CAP negotiation, `005` ISUPPORT token capture, disconnect classification, and bounded reconnect/backoff state.
3. Deterministic tests: fragmented/coalesced frames, malformed-but-tolerated input, CAP/005 parsing, unknown numerics, registration order, reconnect/resync, and cancellation.

The milestone should not include the Avalonia UI, Matrix, media, bots, old ActiveX behavior, or legacy source ports. Those can consume the stable contracts after the protocol core is proven.

## Read-only and integrity statement

Backup directories examined:

- `D:\dev\nexIRC\bkup\archives`
- `D:\dev\nexIRC\bkup\src(nexirc_20b6_functioning_dcc)`
- `D:\dev\nexIRC\bkup\src(nexirc_20b6_functioning_dcc2)`
- `D:\dev\nexIRC\bkup\src(nexirc20b6c)`
- `D:\dev\nexIRC\bkup\src(NexIRC)`
- `D:\dev\nexIRC\bkup\src(nexirc225_indev)`
- `D:\dev\nexIRC\bkup\nexIRC3_`
- `D:\dev\nexIRC\bkup\nexIRC4_`
- `D:\dev\nexIRC\bkup\nexIRC4_Corrupted`

No legacy source tree, canonical v2/v3/v4 tree, or `bkup` file was modified. No legacy project was built, restored, checked out, or modernized. The only intended new artifact is this Phase 0B report under `nexIRC5\docs`.

Final readiness recommendation: nexIRC 5 is ready to begin Phase 1 implementation, starting with the headless IRC protocol/networking vertical slice above.
