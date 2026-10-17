# nexIRC Windows UI Acceptance

- Mode: `automated`
- Target PID: `18620`; executable: `nexIRC.Desktop.exe`; window title verified: `nexIRC 5`
- Session: `1`; input desktop: `Default`; integrity RID: `8192`
- Target ownership: `runner launched`

| Scenario | Result | Evidence | State assertion | UI assertion |
|---|---|---|---|---|
| `UI54-DEMO-FIXTURE-READY` | **PASS** | Level 2 — Runtime UI Automation | fixture ready=True | both demo networks and the history row are available and stable |
| `UI54-KEY-QUALIFICATION` | **PASS** | Level 1 — Native OS input | no command or text entry | WPF preview key pair=True |
| `UI54-MOUSE-QUALIFICATION` | **PASS** | Level 1 — Native OS input | conversation selection only | WPF preview mouse pair=True |
| `UI54-FIXTURE-UNPIN-ALPHA` | **PASS** | Level 1 — Native OS input | pre-existing pin removed=True; projection contains target=False | Unpin action target correlated=True |
| `UI54-MOUSE-PIN` | **PASS** | Level 1 — Native OS input | pinned projection contains target=True; count=4 | menu action target fingerprint correlated=True; UIA pinned row present=True; transcript/unread/important/composer preserved=True; active selection preserved=True |
| `UI54-FIXTURE-UNPIN-BETA` | **PASS** | Level 1 — Native OS input | Pin action available; Unpin action absent | fixture is not pinned |
| `UI54-MOUSE-PIN-BETA` | **PASS** | Level 1 — Native OS input | pinned projection contains target=True; count=5 | menu action target fingerprint correlated=True; UIA pinned row present=True; transcript/unread/important/composer preserved=True; active selection preserved=True |
| `UI54-MOUSE-MOVE-TOP` | **PASS** | Level 1 — Native OS input | target projection index moved from 2 to first=True | Move to top WPF action observed=True; UIA first row matches=True; target remains present=True; transcript/unread/important/composer preserved=True; active selection preserved=True |
| `UI54-PINNED-DESTINATION` | **PASS** | Level 1 — Native OS input | pinned activation event recorded=True | target open=True; active selection matches destination=True; open=True, viewFingerprint=20fa207a2cd7, activeFingerprint=2fdde701bf0c, entries=16, unread=0, important=False, composerBytes=0, composerFingerprint=none |
| `UI54-PIN-CLOSE-REOPEN` | **PASS** | Level 1 — Native OS input | view closed=True; pin retained=True; reopen event=True; transcript/unread/important/composer restored=True; duplicate TreeItems absent=True | UIA TreeItem count=0; unique pinned row=True; close pair=True; before close=open=True, viewFingerprint=6c2c24b52b84, activeFingerprint=2fdde701bf0c, entries=16, unread=0, important=False, composerBytes=50, composerFingerprint=35eadf0da5c5; after close=open=False, viewFingerprint=6c2c24b52b84, activeFingerprint=2fdde701bf0c, entries=16, unread=0, important=False, composerBytes=50, composerFingerprint=35eadf0da5c5; reopened=open=True, viewFingerprint=6c2c24b52b84, activeFingerprint=735a0da33494, entries=16, unread=0, important=False, composerBytes=50, composerFingerprint=35eadf0da5c5; reopen activation telemetry=APP_PINNED_ACTIVATION target=735a0da33494 view=6c2c24b52b84 active=735a0da33494 wasOpen=false open=true entries=16 unread=0 important=false composerBytes=50 composer=35eadf0da5c5 |
| `UI54-KEYBOARD-NAVIGATION` | **PASS** | Level 1 — Native OS input | focus changed from 18a436aaa96f to 46a2368bc4fd=True; focus remains in the pinned list=True; row count=5 | focused element fingerprint=46a2368bc4fd |
| `UI54-MOUSE-UNPIN` | **PASS** | Level 1 — Native OS input | target removed; remaining pins=4 | WPF Unpin action target correlated=True; UIA target row removed=True; transcript/unread/important/composer preserved=True; active selection preserved=True |
| `UI54-KEYBOARD-NAVIGATION-TO-PIN` | **PASS** | Level 1 — Native OS input | native keyboard focus reached the eligible AlphaNet #alpha row=True | sibling index=11->7; focused element fingerprint=ce3607029a25 |
| `UI54-KEYBOARD-PIN-CONTEXT` | **PASS** | Level 1 — Native OS input | no action invoked | ChannelContextMenu UIA items=2 |
| `UI54-KEYBOARD-MENU-INITIAL-FOCUS` | **PASS** | Level 2 — Runtime UI Automation | focused element is one of the visible menu items=True | menu item count=2; focused element fingerprint=b263eab65b16 |
| `UI54-KEYBOARD-PIN` | **PASS** | Level 1 — Native OS input | pinned UIA row=True; projection contains target=True; transcript/unread/important/composer preserved=True; active selection preserved=True | state pair=True; before=open=True, viewFingerprint=6c2c24b52b84, activeFingerprint=735a0da33494, entries=16, unread=0, important=False, composerBytes=50, composerFingerprint=35eadf0da5c5; after=open=True, viewFingerprint=6c2c24b52b84, activeFingerprint=735a0da33494, entries=16, unread=0, important=False, composerBytes=50, composerFingerprint=35eadf0da5c5 |
| `UI54-KEYBOARD-DESTINATION` | **PASS** | Level 1 — Native OS input | target open after activation=True; transcript/unread/important/draft and view identity preserved=True; active selection matches destination=True | composer bytes=50; composer fingerprint=35eadf0da5c5 |
| `UI54-KEYBOARD-CONTEXT` | **PASS** | Level 1 — Native OS input | no action invoked | PinnedConversationContextMenu UIA items=3 |
| `UI54-KEYBOARD-MOVE-TOP` | **PASS** | Level 1 — Native OS input | target row moved from projection index 2 to first=True; UIA first row matches=True; transcript/unread/important/composer preserved=True; active selection preserved=True | target=735a0da33494; order before=2fdde701bf0c,1c235d746e38,735a0da33494,cbea0f846e03,c84695efe01f; after=735a0da33494,1c235d746e38,2fdde701bf0c,cbea0f846e03,c84695efe01f; state pair=True; before=open=True, viewFingerprint=6c2c24b52b84, activeFingerprint=735a0da33494, entries=16, unread=0, important=False, composerBytes=50, composerFingerprint=35eadf0da5c5; after=open=True, viewFingerprint=6c2c24b52b84, activeFingerprint=735a0da33494, entries=16, unread=0, important=False, composerBytes=50, composerFingerprint=35eadf0da5c5 |
| `UI54-KEYBOARD-UNPIN` | **PASS** | Level 1 — Native OS input | AlphaNet #general row removed and #alpha retained=True; transcript/unread/important/composer preserved=True; active selection preserved=True | state pair=True; before=open=True, viewFingerprint=20fa207a2cd7, activeFingerprint=735a0da33494, entries=16, unread=0, important=False, composerBytes=0, composerFingerprint=none; after=open=True, viewFingerprint=20fa207a2cd7, activeFingerprint=735a0da33494, entries=16, unread=0, important=False, composerBytes=0, composerFingerprint=none |
| `UI54-KEYBOARD-ACTIONS` | **PASS** | Level 1 — Native OS input | pinned #general row removed=True; #alpha row remains=True | both menu action telemetry records are correlated to their target fingerprints |
| `UI54-UIA-ACCESSIBILITY` | **PASS** | Level 2 — Runtime UI Automation | pinned UI rows=4; duplicate fixture rows absent=True | named list and enabled/focusable visible rows=True |
| `UI54-FOCUS-REMOTE-REORDER` | **NOT RUN** | Level 2 — Runtime UI Automation | not evaluated | not evaluated |
| `UI54-FOCUS-REMOTE-REMOVE` | **NOT RUN** | Level 2 — Runtime UI Automation | not evaluated | not evaluated |

## Diagnostics

No additional diagnostics.

Names and input values are omitted from the report; fixture correlation uses short SHA-256 fingerprints.