# ClipPlus

A minimal, native Windows clipboard history manager: the familiar Copy/Paste workflow with search, editable titles, colors, a persistent popup height, and pinned-first history.

**Status:** Initial implementation in development. Windows desktop acceptance and release review are required before a stable release.

## Features

- Win+V, with alternate shortcuts available from the tray if Windows reserves the chord.
- Search titles and copied text; arrow keys to navigate, Enter or a click on the preview to paste, Esc to close.
- Keep 50 recent unpinned clips plus all pinned clips, ordered pinned-first.
- Click a title (including No Title) to edit it; Enter saves, Esc cancels.
- Hover or click the color swatch beside Pin to pick one of eight preset colors or Default.
- Drag the top or bottom edge to resize. The preferred height survives restart.
- Retain text, HTML and native image payloads; no network service, account or sync.

## Build and test

Use .NET 10 SDK and PowerShell 7 on Windows. The application targets WPF/Win32 and needs an interactive Windows desktop to run. Check current [.NET Windows support](https://learn.microsoft.com/dotnet/core/install/windows).

    dotnet test tests/ClipPlus.Tests/ClipPlus.Tests.csproj -c Release
    dotnet build src/ClipPlus/ClipPlus.csproj -c Release
    pwsh -File scripts/Build-Package.ps1 -Runtime win-x64

The CI workflow produces a self-contained x64 test package and its SHA-256 digest. A green build is not proof of interactive behavior. Do not treat CI artifacts as stable releases.

## Install, update and remove

Extract a trusted package to a directory, verify the archive digest, and close any separately running ClipPlus instance. In an elevated Windows PowerShell opened by the intended logged-in administrator account, run:

    powershell.exe -NoProfile -File .\Install.ps1

The installer checks the package file manifest, stages the application in Program Files, and creates a per-user interactive logon task with highest privileges. It verifies a startup readiness marker from the new process and attempts rollback on activation failure. Install a newer package using the same command. The uninstaller removes the installed app and its task, not history or preferences:

    powershell.exe -NoProfile -File .\Uninstall.ps1

To try without installation or elevation, run app\ClipPlus.exe from the extracted package. Normal mode cannot reliably paste into elevated applications. Exit from the tray before switching modes.

## Local data and privacy

The installed elevated mode uses a protected, per-user directory under Program Files\ClipPlusData\<user SID>. Normal mode uses %LOCALAPPDATA%\ClipPlus. These histories are deliberately separate across privilege levels, and updates/uninstall preserve them. History is local SQLite data, not a password vault or encrypted backup. ClipPlus honors clipboard exclusion markers and keeps clipboard content out of diagnostic logs. Use the tray's Exit option to stop capture.

## Development

- [Project specification](docs/PROJECT-SPEC.md)
- [Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md)
- [Work tracking](https://github.com/AChWorks/clipplus/issues)
