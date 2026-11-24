# ClipPlus

A minimal, native Windows clipboard history manager: the familiar Copy/Paste workflow with search, editable titles, colors, a persistent popup position and height, and pinned-first history.

**Status:** Initial implementation in development. Exact-candidate validation and independent review are required before a stable release.

## Features

- Win+V, with alternate shortcuts available from the tray. Opening ClipPlus does not open Start.
- Search titles and copied text; real title matches rank first, then content matches. Within each tier, pinned items precede recent items. Arrow keys to navigate, Enter or a click on the preview to paste, Esc to close.
- Keep 50 recent unpinned clips plus all pinned clips, ordered pinned-first when not searching.
- Click a title (including No Title) to edit it; Enter saves, Esc cancels.
- Hover or click the color swatch beside Pin to pick one of eight preset colors or Default.
- Drag the header to move the popup; drag the top or bottom edge to resize. Position and height survive restart. Use Reset popup position in the tray to return to contextual placement.
- Retain text, HTML and native image payloads; no network service, account or sync.

## Download and install

- **ClipPlus.exe**: portable single executable. Exit any existing ClipPlus instance from the tray, then launch directly. Run as administrator when you need to paste into elevated applications.
- **ClipPlus-Setup.exe**: graphical Windows installation. Next, choose whether to run as administrator, Install, Finish. It starts at logon in the chosen mode (normal uses the account's ordinary Windows token; accounts without a filtered token may already be administrators), provides an account-specific Start menu shortcut (ClipPlus followed by the account SID), and registers in Settings > Apps > Installed apps. Run Setup again to update; history and settings are preserved. Uninstall from Installed apps.

If setup was interrupted by a crash or shutdown, run the trusted ClipPlus-Setup.exe again as the same administrator account. Setup recovers the interrupted transaction before offering installation; you can then continue updating or close the wizard and uninstall normally. If the journal or backup is inconsistent, preserve the reported transaction directory and resolve the specific error before retrying. Do not delete the backup or history manually. When switching from an earlier pre-release test installer with a shared shortcut, uninstall that test build first (history and settings remain), then install the new candidate.

No separately installed modern .NET Desktop Runtime is required. ClipPlus uses the Windows .NET Framework 4.7.2 or later and Windows-provided SQLite, both normally present on modern Windows 10 and Windows 11. Older Windows installations may need the OS component enabled/updated; see [Microsoft's version table](https://learn.microsoft.com/dotnet/framework/install/versions-and-dependencies). Actual runtime acceptance on every Windows edition and architecture is not claimed.

## Build and test

Use .NET 10 SDK (build/test tooling only), PowerShell 7 and Inno Setup 6 on Windows.

    dotnet test tests/ClipPlus.Tests/ClipPlus.Tests.csproj -c Release
    dotnet build tests/ClipPlus.WindowsSmoke/ClipPlus.WindowsSmoke.csproj -c Release
    pwsh -File scripts/Build-Package.ps1

The CI workflow creates portable and graphical installer executables with SHA-256 digests. The isolated Windows lifecycle smoke runs the exact bare portable executable, installs/updates/uninstalls the graphical package, verifies startup and clipboard behavior. CI artifacts are test candidates, not stable releases.

## Local data and privacy

The installed elevated mode uses a protected, per-user directory under Program Files\ClipPlusData\<user SID>. Normal mode uses %LOCALAPPDATA%\ClipPlus. These histories are deliberately separate across privilege levels, and updates/uninstall preserve them. History is local SQLite data, not a password vault or encrypted backup. ClipPlus honors clipboard exclusion markers and keeps clipboard content out of diagnostic logs. Use the tray's Exit option to stop capture.

## License and publisher

ClipPlus first-party code is licensed under the [Mozilla Public License 2.0](LICENSE). Source code for each release is available from its corresponding Git tag and GitHub release source archive. The portable executable and installed app expose **License and notices** in the tray menu. The build verifies the actual embedded assembly inventory and includes the full first-party license and third-party notices in the application and release artifacts. Upstream notices retain their original terms; see [docs/licenses](docs/licenses).

Publisher metadata identifies AChWorks. Until publicly trusted Authenticode signing is available, the executables are unsigned and Windows may display Unknown publisher or SmartScreen warnings. Verify official GitHub release SHA-256 checksums; metadata alone is not publisher authentication. Do not disable UAC or SmartScreen. Compatibility evidence and untested environments are documented in the [Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md).

## Development

- [Project specification](docs/PROJECT-SPEC.md)
- [Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md)
- [Work tracking](https://github.com/AChWorks/clipplus/issues)
