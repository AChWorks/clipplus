#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory = '',[switch]$BuildFaultHarness)
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
    # Audit the actual Costura assembly resources, not just direct references.
    $noticeDir = Join-Path $root 'docs/licenses'
    $inventory = Get-Content -LiteralPath (Join-Path $noticeDir 'dependencies.json') -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath (Join-Path $root 'src/ClipPlus/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $inventory) {
        if (-not $assets.libraries.Contains("$($entry.package)/$($entry.version)")) {
            throw "Notice inventory is out of date: $($entry.package) $($entry.version)"
        }
    }
    $null = [Reflection.Assembly]::Load('System.Reflection.Metadata')
    $stream = [IO.File]::OpenRead($exe)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $names = @($metadata.ManifestResources | ForEach-Object {
                $metadata.GetString($metadata.GetManifestResource($_).Name)
            })
        } finally { $pe.Dispose() }
    } finally { $stream.Dispose() }
    $expected = @($inventory | ForEach-Object { "costura.$($_.assembly.ToLowerInvariant()).compressed" })
    $expected += 'costura.clipplus.core.dll.compressed'
    $actual = @($names | Where-Object { $_ -match '^costura\..*\.dll(?:\.compressed)?$' })
    if (@(Compare-Object $expected $actual).Count -ne 0) {
        throw "Embedded dependency inventory mismatch. Actual: $($actual -join ', ')"
    }
    if ($names -notcontains 'ClipPlus.License.MPL-2.0.txt') { throw 'Missing embedded ClipPlus MPL-2.0 license.' }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.txt') -Force
    $noticeFiles = @((Get-Item -LiteralPath (Join-Path $noticeDir 'index.txt'))) +
        @(Get-ChildItem -LiteralPath $noticeDir -Filter '*.txt' | Where-Object Name -ne 'index.txt' | Sort-Object Name)
    foreach ($notice in $noticeFiles) {
        if ($names -notcontains "ClipPlus.Notices.$($notice.Name)") { throw "Missing embedded notice $($notice.Name)" }
    }
    ($noticeFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join ([Environment]::NewLine * 2) |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt') -Encoding utf8NoBOM
    Write-Host "Verified embedded dependencies: $($actual.Count - 1) third-party assemblies and all license/notice resources."

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
    Write-Host "Inno Setup compiler: $([Diagnostics.FileVersionInfo]::GetVersionInfo($iscc).ProductVersion)"
    if ($BuildFaultHarness) {
        & $iscc "/DPayloadDir=$payload" "/DAppVersion=$version" "/DOutputDir=$OutputDirectory" "/DFaultHarness=1" (Join-Path $root 'installer/ClipPlus.iss')
        if ($LASTEXITCODE -ne 0) { throw "Fault-test installer compilation failed ($LASTEXITCODE)." }
    }
    Copy-Item -LiteralPath $exe -Destination (Join-Path $OutputDirectory 'ClipPlus.exe') -Force
    foreach ($name in @('ClipPlus.exe','ClipPlus-Setup.exe')) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $OutputDirectory $name))
        if ($info.CompanyName -ne 'AChWorks' -or $info.ProductName -ne 'ClipPlus') {
            throw "Unexpected publisher/product metadata for $name."
        }
        $signature = Get-AuthenticodeSignature -FilePath (Join-Path $OutputDirectory $name)
        Write-Host "$name publisher metadata: $($info.CompanyName), Authenticode: $($signature.Status)"
    }
    foreach ($name in @('ClipPlus.exe','ClipPlus-Setup.exe')) {
        $file = Join-Path $OutputDirectory $name
        $digest = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $name" | Set-Content -LiteralPath "$file.sha256" -Encoding ascii
        Write-Host "$name ($((Get-Item -LiteralPath $file).Length) bytes)"
    }
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
