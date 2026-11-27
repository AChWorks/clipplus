#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Repository,
      [Parameter(Mandatory)][string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Select stable published predecessors of the candidate, not the latest
# arbitrary tags or a permanently growing list. Excluding the candidate
# itself also keeps post-publication validation on the same upgrade window.
[xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/ClipPlus/ClipPlus.csproj') -Raw
$candidate = [version]([string]$project.Project.PropertyGroup.Version)
$json = @(gh api --paginate "repos/$Repository/releases?per_page=100" --jq '.[] | select(.draft == false and .prerelease == false)')
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate published stable releases.' }
$versions = @(foreach ($line in $json) {
    $release = $line | ConvertFrom-Json
    # The repository uses vMAJOR.MINOR.PATCH stable version tags.
    if ($release.tag_name -notmatch '^v(\d+\.\d+\.\d+)$') {
        throw "Unexpected stable release tag: $($release.tag_name)"
    }
    $version = [version]$Matches[1]
    if ($version -lt $candidate) {
        [pscustomobject]@{ Version = $version; Release = $release }
    }
})
if (@($versions | Group-Object Version | Where-Object Count -ne 1).Count -ne 0) {
    throw 'Ambiguous stable release versions.'
}
$selected = @($versions | Sort-Object Version -Descending | Select-Object -First 3)
if ($selected.Count -eq 0) { throw "No published stable predecessor of $candidate was found." }
New-Item -ItemType Directory -Path $FixtureDirectory -Force | Out-Null
function PublishedHash($Release, [string]$Name) {
    $asset = @($Release.assets | Where-Object name -eq $Name)
    if ($asset.Count -ne 1 -or $asset[0].digest -notmatch '^sha256:([0-9a-f]{64})$') {
        throw "Missing unambiguous SHA-256 release asset: $($Release.tag_name)/$Name"
    }
    return $Matches[1].ToLowerInvariant()
}
$fixtures = @(foreach ($entry in $selected) {
    $release = $entry.Release
    $tag = $release.tag_name
    $setupHash = PublishedHash $release 'ClipPlus-Setup.exe'
    $exeHash = PublishedHash $release 'ClipPlus.exe'
    $dir = Join-Path $FixtureDirectory $tag
    gh release download $tag --repo $Repository --pattern ClipPlus-Setup.exe --pattern ClipPlus-Setup.exe.sha256 --pattern ClipPlus.exe.sha256 --dir $dir | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not retrieve exact release fixtures: $tag" }
    foreach ($name in @('ClipPlus-Setup.exe','ClipPlus.exe')) {
        $expected = if ($name -eq 'ClipPlus-Setup.exe') { $setupHash } else { $exeHash }
        $checksumFile = Join-Path $dir "$name.sha256"
        $checksum = ((Get-Content -LiteralPath $checksumFile -Raw).Trim() -split '\s+')[0]
        if ($checksum -ne $expected -or
            (Get-FileHash -LiteralPath $checksumFile -Algorithm SHA256).Hash -ne (PublishedHash $release "$name.sha256")) {
            throw "Published checksum evidence mismatch: $tag/$name"
        }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $dir 'ClipPlus-Setup.exe') -Algorithm SHA256).Hash -ne $setupHash) {
        throw "Released installer checksum mismatch: $tag"
    }
    Write-Host "Selected verified stable predecessor $tag (release $($release.id))."
    [pscustomobject]@{
        Version = $tag
        ReleaseId = $release.id
        SetupHash = $setupHash
        ExeHash = $exeHash
    }
})
ConvertTo-Json -InputObject @($fixtures) -Depth 4 | Set-Content -LiteralPath (Join-Path $FixtureDirectory 'fixtures.json') -Encoding utf8NoBOM
