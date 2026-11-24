#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage = Join-Path $OutputDirectory ("package-" + [Guid]::NewGuid().ToString('N'))
$build = Join-Path $stage 'build'
$payload = Join-Path $stage 'payload'
$app = Join-Path $payload 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null
try {
    $project = Join-Path $root 'src/ClipPlus/ClipPlus.csproj'
    [xml]$xml = Get-Content -LiteralPath $project -Raw
    $version = [string]$xml.Project.PropertyGroup.Version
    dotnet build $project -c Release -p:EmbedDependencies=true -p:DebugType=None -p:DebugSymbols=false -o $build --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)." }
    # The test deliberately launches the exact executable alone to prove that
    # neither a .config file nor the original managed dependency files are required.
    Copy-Item -LiteralPath (Join-Path $build 'ClipPlus.exe') -Destination $app
    $exe = Join-Path $app 'ClipPlus.exe'
    @{ product='AChWorks.ClipPlus'; version=$version; rid='windows-anycpu'; files=@(
        @{ path='ClipPlus.exe'; sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash }
    ) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8NoBOM
    foreach ($name in @('Install.ps1','Uninstall.ps1','Setup.Common.ps1','Invoke-Setup.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $payload
    }
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $iscc = if ($compiler) { $compiler.Source } else {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) 'Inno Setup 6\ISCC.exe'
    }
    if (-not (Test-Path -LiteralPath $iscc)) { throw 'Inno Setup 6 compiler is required on the build machine.' }
    & $iscc "/DPayloadDir=$payload" "/DAppVersion=$version" "/DOutputDir=$OutputDirectory" (Join-Path $root 'installer/ClipPlus.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $exe -Destination (Join-Path $OutputDirectory 'ClipPlus.exe') -Force
    foreach ($name in @('ClipPlus.exe','ClipPlus-Setup.exe')) {
        $file = Join-Path $OutputDirectory $name
        $digest = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $name" | Set-Content -LiteralPath "$file.sha256" -Encoding ascii
        Write-Host "$name ($((Get-Item -LiteralPath $file).Length) bytes)"
    }
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
