# Windows desktop acceptance

Use a disposable or explicitly authorized interactive Windows desktop. Use synthetic data only; do not record actual clipboard contents or credentials in evidence. Record exact commit/package SHA-256, OS build, display scaling, account elevation and results. A build or automated store test does not prove these interactions.

## Runtime

1. Start the portable app normally. Confirm tray icon, a single instance, no unwanted window on startup, and normal copy/paste.
2. Copy distinct plain text in Notepad. Physically press Win+V. Verify the native popup does not also open, Start does not open or steal focus when Win is released, Search receives focus, standalone Win and unrelated shortcuts remain normal, normal Windows shortcuts and typing still work, Esc closes, repeated hotkey works, and native Win+V resumes after Exit. If the chord cannot be intercepted on this OS, test the alternate shortcuts and record the exact behavior; do not mark Win+V as passed.
3. With text focus in Notepad, open ClipPlus, type a search, navigate using Up/Down and press Enter. Verify the original target receives the original content exactly once. Test mouse hover and preview click. Switch focus away while the popup is open: it should dismiss, and paste must never be sent to an unrelated app.
4. Edit a title inline, save/cancel/clear, search Persian and English title/content, verify title matches precede even pinned content-only matches and No Title does not count as an actual title, and confirm only original content is pasted.
5. Pin clips, copy 55+ distinct items, recopy a pinned and a recent clip, verify stable pinned-first order, 50 recent items, and preserved metadata. Unpin, delete, clear recent and restart.
6. Select each of eight colors and Default using mouse hover/click and keyboard. Verify no accidental paste or reorder, persistence on restart/recopy/pin-unpin, visible focus, no horizontal overflow, readable light/dark/high-contrast appearance, and selection feedback.
   With an overflowing history, verify the thin rounded scrollbar expands on hover,
   retains a usable pointer target and supports thumb dragging, page clicks,
   wheel scrolling and keyboard navigation. Check both themes and high contrast.
7. Drag the header and resize vertically, hide and reopen, restart, move to another monitor/DPI, and test near screen edges. Verify saved position and height, work-area clamp and recovery of preferred height on the larger monitor; reset placement from the tray. Confirm the header controls remain clickable.
8. Copy and paste plain Unicode, formatted HTML (e.g. browser to editor), PNG/bitmap image into Paint, and compare actual content. Verify image preview, duplicate recapture behavior, clipboard contention recovery, no self-capture loop, and privacy markers (ExcludeClipboardContentFromMonitorProcessing and CanIncludeInClipboardHistory=0 ignored; CanUploadToCloudClipboard=0 preserved on paste). Test partial SetClipboardData failures: publish the privacy marker first, never send paste after failure, clear partial restoration where possible.

## Installation / privilege / lifecycle

1. Verify the executable hashes. Launch the portable ClipPlus.exe directly without sidecar files. Run ClipPlus-Setup.exe, proceed through the wizard, choose administrator or normal automatic startup, and install as the intended logged-in administrator. Verify a missing Windows .NET Framework prerequisite is reported before replacing an installed version. Verify Installed Apps registration and the standard uninstaller. Check Program Files installation ACL/owner, Task Scheduler principal (interactive, highest), actual process token via native Windows API, executable path and verified health marker PID. Normal-state marker data is never used as proof of elevated readiness. No UAC disablement or SYSTEM task.
2. Repeat tests 2–3 from an ordinary and elevated editor/terminal. Verify normal mode fails safely for a high-integrity target and elevated mode pastes correctly. Do not use the UAC secure desktop.
3. Verify elevated history and settings use the protected per-user directory, normal history is separate, medium-integrity processes cannot modify privileged app/data files, and diagnostic logs contain no clipboard content.
4. Log out/in: verify automatic startup with the selected privileges and the correct per-user history and settings.
5. Update using a trusted test package. Verify the previous app remains unchanged if prerequisite or payload validation fails. Compare real pinned history metadata, original clipboard format payloads and settings after the update. Check restored popup position and height.
6. Test failed task activation, wizard failure after activation, and cancellation before final commit for both initial install and upgrade. Verify restoration of the previous executable, ownership marker, task and startup mode, Installed Apps registration and uninstaller, own shortcut, and pinned history/settings. For an initial installation failure, verify no owned installation resources remain.
7. Test interrupted setup recovery using a fresh trusted wizard:
   - Interrupt before journal creation, after moving the previous package, and after wizard activation but before commit.
   - Interrupt rollback after atomically moving the failed package aside, after restoring the previous package (when present), and during terminal cleanup after removing the failed-package ownership marker.
   - Repeat recovery and verify the previous package and marker hashes, task, registration, shortcut and pinned history/settings. Check that a foreign account shortcut fixture remains unchanged and the transaction is cleaned up.
   - Use only the separately compiled fault-test installer or disposable script fixtures in CI, never a release binary. Injected termination tests do not establish physical power-loss or filesystem-corruption behavior.
8. Verify distinct account shortcut ownership. A foreign-SID fixture alone is not a full two-account interactive desktop test.
9. Uninstall from Installed Apps: only ClipPlus-owned files, task, shortcut and registration are removed; history/settings remain; native Win+V works again.

## Evidence boundary

For each release, refer to its [release notes](https://github.com/AChWorks/clipplus/releases) and linked exact-candidate CI/PR for build, package hashes, embedded dependency/notice inventory, core tests, native UI/clipboard checks and installer lifecycle evidence. Inspect License and notices from the tray and the accompanying LICENSE.txt and THIRD-PARTY-NOTICES.txt. Verify AChWorks file metadata/Installed Apps publisher separately from Authenticode status.

For v0.1.0, the Owner accepted the representative Windows CI and prior hands-on test boundary in [the project specification](PROJECT-SPEC.md#completion-and-validation) on 2026-11-24. The full checklist above remains useful for future compatibility verification, but untested client environments do not independently block the initial release. In particular, a genuine filtered ordinary token, cross-integrity desktop cases, two actual accounts, Windows 10/11 client and older Framework versions, mixed-DPI monitors, physical Win+V/native shell restoration, theme interactions and logoff/logon have not all been exercised. The missing-Framework path was simulated; no OS component was removed. Do not mark these cases passed without evidence. Release notes must disclose material gaps; exact-candidate independent review and Owner authorization remain required.
