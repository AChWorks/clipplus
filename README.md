# ClipPlus

A lightweight Windows clipboard history manager that keeps the familiar Win+V workflow and adds search, editable titles, pins, colors, categories, compact view, a pinned quick-paste panel and persistent popup geometry.

[Download installer](https://github.com/AChWorks/clipplus/releases/latest/download/ClipPlus-Setup.exe) · [Download portable](https://github.com/AChWorks/clipplus/releases/latest/download/ClipPlus.exe) · [Releases and checksums](https://github.com/AChWorks/clipplus/releases/latest)

## Download and install

ClipPlus targets Windows 10 and 11. It uses the Windows-provided .NET Framework 4.7.2 or later and SQLite engine (winsqlite3.dll). Both are normally available on modern Windows installations; older systems may need the corresponding Windows components updated. No separately installed modern .NET Desktop Runtime is needed. See [Microsoft's Framework version table](https://learn.microsoft.com/dotnet/framework/install/versions-and-dependencies) and the release's documented validation scope.

| Download | How to use |
| --- | --- |
| [ClipPlus-Setup.exe](https://github.com/AChWorks/clipplus/releases/latest/download/ClipPlus-Setup.exe) | Recommended. Run the graphical installer as the intended logged-in administrator. Choose administrator privileges (fresh-install default) or ordinary account privileges, then complete the wizard. Updates initially use the existing task settings. |
| [ClipPlus.exe](https://github.com/AChWorks/clipplus/releases/latest/download/ClipPlus.exe) | Portable single executable, no installation or sidecar files. Exit any running ClipPlus instance first, then launch it. Use Run as administrator when you need to paste into elevated applications. |

The installer registers ClipPlus in Windows Installed Apps and creates a per-user **ClipPlus** Start menu shortcut, without a SID in its display name. It migrates the previous owned common shortcut. It configures an interactive per-user startup task, not a Windows service. Fresh installs start with Windows; updates preserve the actual logon-trigger state and privileges unless explicitly changed. Tray > Start with Windows toggles only the logon trigger; the Start menu shortcut still works and the running app is not stopped. Tray > Startup privileges changes future launches after confirmation. Normal startup uses the account's ordinary Windows token, which may already be administrative on an account without a filtered token.

Publisher metadata identifies AChWorks. The initial release is unsigned, so Windows may display Unknown publisher or SmartScreen warnings. Verify the executable against its accompanying SHA-256 file from the [official release](https://github.com/AChWorks/clipplus/releases/latest); metadata alone does not authenticate the publisher. Do not disable UAC or SmartScreen.

## Using ClipPlus

| Action | Control |
| --- | --- |
| Open or dismiss history | Win+V; Esc closes the popup |
| Alternate shortcut | Tray > Shortcut: Ctrl+Shift+V or Ctrl+Alt+V |
| Search | Type a title or copied text; searches all views, real title matches rank first |
| Navigate and paste | Up/Down and Enter, or select and click the clip preview |
| Edit a title | Click the title or No Title; Enter saves, Esc cancels |
| Organize clips | Pin, unpin, delete or choose one of eight colors or Default from the color swatch |
| Categories | Select Pinned, Recent or a custom category; + adds a category (up to ten), double-click or F2 edits it, drag or Ctrl+Left/Right reorders it |
| Assign a category | Use + or the category name on the clip; select No category to detach it |
| Compact view | Use the centered footer toggle to collapse titled clips; untitled clips retain their previews |
| Pinned quick-paste | Use the pin icon beside Close to enter or leave the persistent title-only panel |
| Clear history | Clear removes all unpinned items (including categorized items) and keeps pins |
| Move or resize | Drag the header to move or any edge to resize; each mode remembers its own position, width and height |
| Restore contextual position | Tray > Reset popup position |

ClipPlus keeps 100 recent unpinned clips plus all pinned clips, deduplicates equivalent copies without losing their metadata, and retains Unicode text, HTML and relevant native image formats. Pinned is the complete global view, all pins first and then all unpinned recent items; Recent shows only global unpinned items. Custom categories contain their own pins first and then their unpinned clips. A clip can belong to one category. Deleting a category detaches its clips without deleting them. Categorization does not prevent normal history eviction: pin a clip to keep it. Category order, the selected view and compact mode persist. In compact mode, use the Paste clip button or Enter to paste a titled clip; clicking its title still edits it. The popup follows the system light/dark appearance, with a slim Windows-style scrollbar for overflowing history. Inactive category tabs have subtle theme-aware fills. Hover over a color swatch for a short moment (350 ms) to open its palette, or click/activate it directly without waiting.

The quick-paste panel lists only actual global pins, including untitled pins as No Title. Click a title to paste its original clipboard formats into the foreground application. The panel stays visible, including after outside clicks and successful pastes. Search matches pinned titles and content, ranks title matches first, and resets to all pins after a successful paste without moving focus back to ClipPlus. Click Search to type; use Up/Down and Enter if preferred. A canceled or copy-only operation retains the query. Drag the header and resize any edge; quick geometry is saved separately from ordinary popup geometry. The initial widths are 360 and 260 device-independent pixels (DIP), respectively, for ordinary and quick mode. Both can shrink by 100 DIP (to 260 and 160 DIP) or expand within the monitor work area. Long titles display an ellipsis when necessary; the full title remains available in its tooltip. The pin toggle returns to ordinary history; Close hides the panel. Win+V does not dismiss quick mode. No quick panel opens automatically at sign-in. Windows foreground and integrity restrictions still apply.

Updating from v0.1.0 uses an additive, transactional history migration. An arbitrary downgrade is not a full reversal of the data model: v0.1.0 still enforces its 50-item recent limit during capture and pin/unpin and does not understand the new compact/selected-view settings. Do not downgrade a history with more than 50 recent clips without first preserving a separate copy of its database and settings.

## Update, uninstall and recovery

- **Update:** run the new trusted Setup executable under the same account. History and preferences are preserved. When switching from an early pre-release test installer with a shared Start menu shortcut, uninstall that test build first, then install the stable version; this preserves history and settings.
- **Uninstall:** use Windows Installed Apps (Apps & features on Windows 10). ClipPlus removes its owned installation, task, shortcut and registration; retained history and preferences are not purged.
- **Interrupted installation:** run the trusted Setup again as the same administrator account. It recovers interrupted installation or rollback before continuing. If recovery reports inconsistent protected state, preserve the indicated transaction directory and correct the reported error before retrying. Do not delete the backup or history manually.

## Local data and privacy

| Execution mode | Data directory |
| --- | --- |
| Normal | `%LOCALAPPDATA%\ClipPlus` |
| Administrator | Protected per-user directory under `%ProgramFiles%\ClipPlusData\<user SID>` |

Clipboard history is stored in local SQLite; preferences use JSON. Normal and elevated histories are deliberately separate. If pins appear missing after an update, check the running privilege and configured startup privilege in the tray. Exit the running copy and launch with the intended mode. Setup warns about a detected profile mismatch before updating; it never automatically merges, copies, overwrites or deletes either history. History is not application-encrypted and should not be treated as a password vault or encrypted backup. ClipPlus honors clipboard exclusion markers and keeps clipboard payloads out of diagnostics. No account, network service, telemetry or cloud synchronization is required. Exit from the tray to stop capture.

For troubleshooting, inspect `diagnostics.log` in the active mode's data directory. When reporting an issue, include the version, Windows build, execution mode, reproduction steps and relevant diagnostics without sharing actual clipboard content or credentials.

## Build and test

Development requires the .NET 10 SDK, PowerShell 7 and Inno Setup 6 on Windows. These are build tools, not user runtime requirements.

```powershell
dotnet test tests/ClipPlus.Tests/ClipPlus.Tests.csproj -c Release
dotnet build tests/ClipPlus.WindowsSmoke/ClipPlus.WindowsSmoke.csproj -c Release
pwsh -File scripts/Build-Package.ps1
```

The package script produces both executables, SHA-256 files, the project license and third-party notices in `artifacts/`. GitHub Actions exercises the portable executable, native clipboard and UI interactions, installation, updates, rollback/recovery and uninstall on an isolated Windows runner. The performance harness compares identical synthetic WPF workloads against the released v0.2.0 source (opening, reopening, category switching, search and capture) and records realized card counts and elapsed layout/dispatcher time. CI artifacts are test candidates; official downloads come from [Releases](https://github.com/AChWorks/clipplus/releases).

Validation covers the available Windows Server CI environment and representative hands-on testing. It does not establish complete compatibility with every Windows 10/11 client edition, older Framework version, privilege configuration or mixed-DPI desktop. See the [release notes](https://github.com/AChWorks/clipplus/releases/latest) and [Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md) for the precise evidence boundary.

## License and development

ClipPlus is developed by AChWorks and licensed under [MPL-2.0](LICENSE). Corresponding first-party source is available at each release tag and in GitHub's source archives. The portable and installed application provide the complete license and third-party notices in the tray's License and notices menu; the same notices accompany the release assets.

- [Project specification](docs/PROJECT-SPEC.md)
- [Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md)
- [Third-party licenses and dependency inventory](docs/licenses/)
- [Build and test workflow](.github/workflows/ci.yml)
- [Report an issue](https://github.com/AChWorks/clipplus/issues)
