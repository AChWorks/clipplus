# ClipPlus — Project specification

## Purpose

Provide a lightweight substitute for the Windows clipboard-history popup, preserving native Copy/Paste and the familiar keyboard-first workflow. ClipPlus is a standalone Windows desktop application, not a Windows Shell patch.

## Accepted behavior

- Open with Win+V, subject to validating a stable supported interception mechanism. Do not modify system binaries or inject into Windows Shell. Consuming Win+V must not cause Start to open on modifier release; preserve standalone Win and unrelated shortcuts. If interception is unavailable, diagnose the conflict and offer a configurable alternate shortcut rather than silently breaking native behavior.
- Focus the search field on opening. Search user titles and text content immediately. For a nonblank query, real title matches rank before content-only matches, preserving pinned-first/recency within each tier. The No Title placeholder is not searchable as a title. Keyboard arrows and mouse selection navigate the filtered list; Enter pastes the original selected payload into the previously active application; Esc dismisses the popup. Direct mouse selection/paste must remain simple.
- Keep pinned entries above recent entries when not searching, independent of new copies, and preserve pins across restart. Retain up to 50 unpinned recent items in addition to pinned items. Evict only the oldest unpinned entries. Deduplicate equivalent copies without losing titles, colors or pin status.
- Display No Title for untitled entries. Clicking the title edits it in place; Enter saves, Esc cancels, and an empty title clears it. Titles never modify the actual payload.
- Allow each clip to have an optional persistent color. Place a compact color swatch next to the pin action; hovering or clicking opens a small palette of eight predefined colors plus Default (no custom color), fitting the normal popup width without horizontal scrolling. Use eight visually distinct hues and a stronger 12-DIP theme-aware colored edge, preserve readable text and provide named tooltips, a selected indicator and keyboard-accessible selection. Choosing or clearing a color must not paste, reorder, pin or modify the payload. Store the color with the clip metadata in SQLite and preserve it on restart, deduplication and pin/unpin. No custom color editor, tags or category system.
- Allow header drag and vertical resize, preserving manually selected screen position and height across hide/reopen and restart. Clamp to the available monitor work area, including changed monitor/DPI configurations. Before the first manual move, place near the input context where supported; offer Reset popup position in the tray.
- Support text, HTML and images with original relevant clipboard formats preserved. Do not expand into a general file/snippet manager. Respect Windows clipboard history exclusion markers. Never include clipboard payloads in diagnostic logs.
- Support pin/unpin, delete, Clear All with pins preserved, and system light/dark appearance.
- A single background process and a recognizable clipboard-plus icon for EXE, popup and tray; no polling loop for clipboard changes, no service, no cloud sync, accounts, telemetry, or network dependency.

## Technical direction

- C#, WPF, .NET Framework 4.7.2 or later; use the Windows-provided runtime and SQLite engine, without requiring modern .NET Desktop Runtime. Ship a direct portable executable and a conventional graphical installer. Build tooling may use modern .NET SDK, but the product must not require it. Preserve compatible SQLite schema and settings. Validate actual Windows 10/11 environments, with no arbitrary build allowlist.
- Reuse Windows clipboard APIs and AddClipboardFormatListener / WM_CLIPBOARDUPDATE. Preserve the existing system clipboard semantics. Use Win32 foreground/window and input APIs to restore focus and paste; avoid recapture loops, handle clipboard contention, and report failed operations accurately.
- Local SQLite storage for history and metadata; JSON for window and app preferences. Keep per-user state separate from the installed executable. Elevated mode uses a protected per-user directory under Program Files/ClipPlusData; normal mode uses LocalAppData/ClipPlus. These histories remain separate so a privileged process does not consume medium-integrity-writable state. Use straightforward code, transactions and parameterized queries; no extra framework or search engine.
- The graphical installer offers automatic elevated or normal startup; the default provides automatic elevated startup in the interactive user session via Task Scheduler (Run with highest privileges, user logon). Install the executable and any loadable code in a protected Program Files path. Never use SYSTEM, disable UAC or store executable/configuration that controls privileged execution in writable locations. Installation/uninstallation must register/remove only ClipPlus-owned resources. Provide a non-elevated launch option and actionable errors if elevated startup cannot be configured. Do not attempt to interact with the secure desktop.
- No Foundation dependency: this is a product-local Windows desktop capability. Reuse native platform capabilities instead of introducing shared abstractions.

## Completion and validation

First prove the Win+V interaction and focus restoration/Paste in both ordinary and elevated Windows applications. A Windows build and Linux cross-build alone do not prove interactive runtime behavior.

Validate keyboard/mouse navigation, title edit, search, color selection/reset and persistence, keyboard-accessible palette without accidental paste, theme contrast, pinned ordering, retention/duplicates, restart persistence, drag, persisted position, resizing and screen bounds, supported clipboard formats, clipboard exclusion markers, contention and self-paste handling, and installation/startup/update/uninstall. Update must preserve history/settings. Use real Windows interactive evidence for hotkeys (including Start-menu regression), foreground focus, paste, DPI and elevation; use targeted automated tests for deterministic state and storage behavior.

Delivery: reproducible Windows build, tested installable package and documentation, exact-candidate independent review and owner-authorized merge/release. No initial release is claimed.

## References

- [Koinon new-project intake](https://github.com/AChWorks/koinon/blob/main/docs/workflows/new-project-intake.md)
- [Koinon validation evidence](https://github.com/AChWorks/koinon/blob/main/docs/contracts/validation-evidence.md)
- [Koinon lifecycle and release](https://github.com/AChWorks/koinon/blob/main/docs/contracts/lifecycle-release.md)
