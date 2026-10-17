# nexIRC Windows UI Acceptance

- Mode: `diagnostic`
- Target PID: `14268`; executable: `nexIRC.Desktop.exe`; window title verified: `nexIRC 5`
- Session: `1`; input desktop: `Default`; integrity RID: `8192`
- Target ownership: `runner launched`

| Scenario | Result | Evidence | State assertion | UI assertion |
|---|---|---|---|---|
| `UI54-NATIVE-ABI` | **PASS** | Level 1 — Native OS input | structure sizes and union alignment | x86/x64 sizes recorded |
| `UI54-KEY-QUALIFICATION` | **PASS** | Level 1 — Native OS input | no command issued | WPF preview key event pair observed=True |
| `UI54-MOUSE-QUALIFICATION` | **PASS** | Level 1 — Native OS input | conversation selection only | WPF preview mouse down/up observed=True |
| `UI54-UIA-TREE` | **PASS** | Level 2 — Runtime UI Automation | eligible fixture row found; nonempty visible bounds=True | hit class=TreeItem:class; ancestor classes=TreeItem:class > Tree:class > Window:class |

## Diagnostics

- Input ABI: architecture=64-bit, INPUT=40/40, MOUSEINPUT=32/32, KEYBDINPUT=24/24; INPUT union offset=8.
- Opt-in WPF probe file present=True; initial bytes=65; records=3.
- UIA target row: class=TreeItem:class focusable=True enabled=True offscreen=False bounds=226x20 nameFingerprint=ce3607029a25.
- WindowFromPoint belongs to target PID: True; UIA FromPoint class=TreeItem:class; UIA ancestry (nearest first): TreeItem:class > Tree:class > Window:class.
- WPF route telemetry: WPF_PREPROCESS kind=QueryCursorEventArgs; WPF_PREPROCESS kind=MouseEventArgs; WPF_PREPROCESS kind=MouseEventArgs; WIN32_MESSAGE id=0x0201; WPF_PREPROCESS kind=MouseButtonEventArgs; WPF_PREVIEW_MOUSE_DOWN; WPF_PREPROCESS kind=MouseButtonEventArgs; WPF_MOUSE_DOWN; WIN32_MESSAGE id=0x0202; WPF_PREPROCESS kind=MouseButtonEventArgs; WPF_PREVIEW_MOUSE_UP; WPF_PREPROCESS kind=MouseButtonEventArgs; WPF_MOUSE_UP; WPF_PREPROCESS kind=QueryCursorEventArgs
- Focused element before inspection fingerprint: 9adc665b3108; target DPI=96.

Names and input values are omitted from the report; fixture correlation uses short SHA-256 fingerprints.