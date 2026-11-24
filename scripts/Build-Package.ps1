#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64','win-x86')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = '',
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage = Join-Path $OutputDirectory ("package-" + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $stage 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null
try {
    $project = Join-Path $root 'src/ClipPlus/ClipPlus.csproj'
    [xml]$xml = Get-Content -LiteralPath $project -Raw
    $version = [string]$xml.Project.PropertyGroup.Version
    $target = [string]$xml.Project.PropertyGroup.TargetFramework
    if ($target -notmatch '^net(\d+\.\d+)-windows') { throw "Unexpected target framework: $target" }
    $desktopRuntime = $Matches[1]
    $publish = @('publish', $project, '-c', 'Release', '-r', $Runtime, '-o', $app,
        '--nologo', '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($SelfContained) {
        $publish += @('--self-contained', 'true')
    } else {
        # All native dependencies come from Windows. Keep one executable and
        # use the machine-wide Desktop Runtime; no user-temp DLL extraction.
        $publish += @('--self-contained', 'false', '-p:PublishSingleFile=true')
    }
    dotnet @publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    $files = @(Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{
            path = $_.FullName.Substring($app.Length + 1).Replace('\','/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    @{ product='AChWorks.ClipPlus'; version=$version; rid=$Runtime; files=$files;
       selfContained=[bool]$SelfContained; desktopRuntime=$desktopRuntime } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    foreach ($name in @('Install.ps1','Uninstall.ps1','Setup.Common.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $stage
    }
    $name = if ($SelfContained) { "ClipPlus-$Runtime-self-contained.zip" } else { "ClipPlus-$Runtime.zip" }
    $archive = Join-Path $OutputDirectory $name
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  $name" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host "Package: $archive ($((Get-Item -LiteralPath $archive).Length) bytes)"
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
