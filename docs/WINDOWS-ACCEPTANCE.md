# Windows desktop acceptance

Use a disposable or explicitly authorized interactive Windows desktop. Use synthetic data only; do not record actual clipboard contents or credentials in evidence. Record exact commit/package SHA-256, OS build, display scaling, account elevation and results. A build or automated store test does not prove these interactions.

## Runtime

1. Start the portable app normally. Confirm tray icon, a single instance, no unwanted window on startup, and normal copy/paste.
2. Copy distinct plain text in Notepad. Physically press Win+V. Verify the native popup does not also open, Start does not open or steal focus when Win is released, Search receives focus, standalone Win and unrelated shortcuts remain normal, normal Windows shortcuts and typing still work, Esc closes, repeated hotkey works, and native Win+V resumes after Exit. If the chord cannot be intercepted on this OS, test the alternate shortcuts and record the exact behavior; do not mark Win+V as passed.
3. With text focus in Notepad, open ClipPlus, type a search, navigate using Up/Down and press Enter. Verify the original target receives the original content exactly once. Test mouse hover and preview click. Switch focus away while the popup is open: it should dismiss, and paste must never be sent to an unrelated app.
4. Edit a title inline, save/cancel/clear, search Persian and English title/content, verify title matches precede even pinned content-only matches and No Title does not count as an actual title, and confirm only original content is pasted.
5. Pin clips, copy 55+ distinct items, recopy a pinned and a recent clip, verify stable pinned-first order, 50 recent items, and preserved metadata. Unpin, delete, clear recent and restart.
6. Select each of eight colors and Default using mouse hover/click and keyboard. Verify no accidental paste or reorder, persistence on restart/recopy/pin-unpin, visible focus, no horizontal overflow, readable light/dark/high-contrast appearance, and selection feedback.
7. Drag the header and resize vertically, hide and reopen, restart, move to another monitor/DPI, and test near screen edges. Verify saved position and height, work-area clamp and recovery of preferred height on the larger monitor; reset placement from the tray. Confirm the header controls remain clickable.
8. Copy and paste plain Unicode, formatted HTML (e.g. browser to editor), PNG/bitmap image into Paint, and compare actual content. Verify image preview, duplicate recapture behavior, clipboard contention recovery, no self-capture loop, and privacy markers (ExcludeClipboardContentFromMonitorProcessing and CanIncludeInClipboardHistory=0 ignored; CanUploadToCloudClipboard=0 preserved on paste).

## Installation / privilege / lifecycle

1. Verify the archive hash and choose the lightweight package with machine-wide .NET 10 Desktop Runtime or the optional self-contained package. Verify a missing required runtime is reported before replacing an installed version. Extract and run Install.ps1 as the intended logged-in administrator. Check Program Files installation ACL/owner, Task Scheduler principal (interactive, highest), executable path and verified ready.json PID. No UAC disablement or SYSTEM task.
2. Repeat tests 2–3 from an ordinary and elevated editor/terminal. Verify normal mode fails safely for a high-integrity target and elevated mode pastes correctly. Do not use the UAC secure desktop.
3. Verify elevated history and settings use the protected per-user directory, normal history is separate, medium-integrity processes cannot modify privileged app/data files, and diagnostic logs contain no clipboard content.
4. Log out/in: automatic elevated startup with correct per-user history and settings. Update with a trusted test package: preserve metadata and preferences. Corrupt a copied package payload: installer should reject before stopping the current version. Test failed activation and rollback in the disposable environment.
5. Uninstall: only ClipPlus-owned files/task are removed; history/settings remain; native Win+V works again.

## Evidence boundary

CI builds, package hash, and core tests are recorded on the PR. The interactive checklist remains pending until a real interactive Windows environment is available. Do not merge or publish a stable release without the project review and acceptance gates.
