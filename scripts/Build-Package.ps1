#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64','win-x86')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = ''
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
    dotnet publish $project -c Release -r $Runtime --self-contained true -o $app --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    [xml]$xml = Get-Content -LiteralPath $project -Raw
    $version = [string]$xml.Project.PropertyGroup.Version
    $files = @(Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{
            path = $_.FullName.Substring($app.Length + 1).Replace('\','/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    @{ product='AChWorks.ClipPlus'; version=$version; rid=$Runtime; files=$files } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    foreach ($name in @('Install.ps1','Uninstall.ps1','Setup.Common.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $stage
    }
    $archive = Join-Path $OutputDirectory "ClipPlus-$Runtime.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  ClipPlus-$Runtime.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host "Package: $archive"
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
