# ClipPlus

A lightweight Windows clipboard history manager that keeps the familiar Win+V workflow and adds search, editable titles, pins, colors, categories, compact view and persistent popup geometry.

[Download installer](https://github.com/AChWorks/clipplus/releases/latest/download/ClipPlus-Setup.exe) · [Download portable](https://github.com/AChWorks/clipplus/releases/latest/download/ClipPlus.exe) · [Releases and checksums](https://github.com/AChWorks/clipplus/releases/latest)

## Download and install

ClipPlus targets Windows 10 and 11. It uses the Windows-provided .NET Framework 4.7.2 or later and SQLite engine (winsqlite3.dll). Both are normally available on modern Windows installations; older systems may need the corresponding Windows components updated. No separately installed modern .NET Desktop Runtime is needed. See [Microsoft's Framework version table](https://learn.microsoft.com/dotnet/framework/install/versions-and-dependencies) and the release's documented validation scope.

| Download | How to use |
| --- | --- |
| [ClipPlus-Setup.exe](https://github.com/AChWorks/clipplus/releases/latest/download/ClipPlus-Setup.exe) | Recommended. Run the graphical installer as the intended logged-in administrator. Choose automatic startup with administrator privileges (default) or ordinary account privileges, then complete the wizard. |
| [ClipPlus.exe](https://github.com/AChWorks/clipplus/releases/latest/download/ClipPlus.exe) | Portable single executable, no installation or sidecar files. Exit any running ClipPlus instance first, then launch it. Use Run as administrator when you need to paste into elevated applications. |

The installer registers ClipPlus in Windows Installed Apps and creates an account-specific Start menu shortcut. It configures an interactive per-user startup task, not a Windows service. Normal startup uses the account's ordinary Windows token, which may already be administrative on an account without a filtered token.

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
| Clear history | Clear recent removes all unpinned items (including categorized items) and keeps pins |
| Move or resize | Drag the header or top/bottom edge; position and height persist |
| Restore contextual position | Tray > Reset popup position |

ClipPlus keeps 100 recent unpinned clips plus all pinned clips, deduplicates equivalent copies without losing their metadata, and retains Unicode text, HTML and relevant native image formats. Pinned and Recent are global views; custom categories contain both pinned and unpinned clips, with pins first. A clip can belong to one category. Deleting a category detaches its clips without deleting them. Categorization does not prevent normal history eviction: pin a clip to keep it. Category order, the selected view and compact mode persist. In compact mode, use the Paste clip button or Enter to paste a titled clip; clicking its title still edits it. The popup follows the system light/dark appearance, with a slim Windows-style scrollbar for overflowing history.

## Update, uninstall and recovery

- **Update:** run the new trusted Setup executable under the same account. History and preferences are preserved. When switching from an early pre-release test installer with a shared Start menu shortcut, uninstall that test build first, then install the stable version; this preserves history and settings.
- **Uninstall:** use Windows Installed Apps (Apps & features on Windows 10). ClipPlus removes its owned installation, task, shortcut and registration; retained history and preferences are not purged.
- **Interrupted installation:** run the trusted Setup again as the same administrator account. It recovers interrupted installation or rollback before continuing. If recovery reports inconsistent protected state, preserve the indicated transaction directory and correct the reported error before retrying. Do not delete the backup or history manually.

## Local data and privacy

| Execution mode | Data directory |
| --- | --- |
| Normal | `%LOCALAPPDATA%\ClipPlus` |
| Administrator | Protected per-user directory under `%ProgramFiles%\ClipPlusData\<user SID>` |

Clipboard history is stored in local SQLite; preferences use JSON. Normal and elevated histories are deliberately separate. History is not application-encrypted and should not be treated as a password vault or encrypted backup. ClipPlus honors clipboard exclusion markers and keeps clipboard payloads out of diagnostics. No account, network service, telemetry or cloud synchronization is required. Exit from the tray to stop capture.

For troubleshooting, inspect `diagnostics.log` in the active mode's data directory. When reporting an issue, include the version, Windows build, execution mode, reproduction steps and relevant diagnostics without sharing actual clipboard content or credentials.

## Build and test

Development requires the .NET 10 SDK, PowerShell 7 and Inno Setup 6 on Windows. These are build tools, not user runtime requirements.

```powershell
dotnet test tests/ClipPlus.Tests/ClipPlus.Tests.csproj -c Release
dotnet build tests/ClipPlus.WindowsSmoke/ClipPlus.WindowsSmoke.csproj -c Release
pwsh -File scripts/Build-Package.ps1
```

The package script produces both executables, SHA-256 files, the project license and third-party notices in `artifacts/`. GitHub Actions exercises the portable executable, native clipboard and UI interactions, installation, updates, rollback/recovery and uninstall on an isolated Windows runner. CI artifacts are test candidates; official downloads come from [Releases](https://github.com/AChWorks/clipplus/releases).

Validation covers the available Windows Server CI environment and representative hands-on testing. It does not establish complete compatibility with every Windows 10/11 client edition, older Framework version, privilege configuration or mixed-DPI desktop. See the [release notes](https://github.com/AChWorks/clipplus/releases/latest) and [Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md) for the precise evidence boundary.

## License and development

ClipPlus is developed by AChWorks and licensed under [MPL-2.0](LICENSE). Corresponding first-party source is available at each release tag and in GitHub's source archives. The portable and installed application provide the complete license and third-party notices in the tray's License and notices menu; the same notices accompany the release assets.

- [Project specification](docs/PROJECT-SPEC.md)
- [Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md)
- [Third-party licenses and dependency inventory](docs/licenses/)
- [Build and test workflow](.github/workflows/ci.yml)
- [Report an issue](https://github.com/AChWorks/clipplus/issues)
