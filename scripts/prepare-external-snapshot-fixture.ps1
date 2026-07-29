[CmdletBinding()]
param(
    [ValidateSet("Prepare", "Restore")]
    [string]$Mode = "Prepare",

    [string]$TestProfilesRoot = "$env:USERPROFILE\Documents\PlayniteDev\TestProfiles",

    [Guid]$PlayniteGameId = "81e05f49-ea4c-438e-b37e-71c95418c7ab",

    [int]$ExpectedAchievementCount = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$producerId = "0391911c-bf98-4a2d-8200-8641af0973e9"
$snapshotFormat = "playnite-achievement-sources.snapshot"
$indexFormat = "playnite-achievement-sources.index"
$schemaVersion = 1
$fixtureDiagnostic = "Synthetic isolated all-locked fixture for External Snapshot consumer validation."

function Get-FullDirectoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Assert-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate)
    $rootFull = Get-FullDirectoryPath $AllowedRoot
    $rootWithSeparator = $rootFull + [System.IO.Path]::DirectorySeparatorChar

    if (-not $candidateFull.StartsWith(
        $rootWithSeparator,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is outside the isolated test-profile root: $candidateFull"
    }

    return $candidateFull
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null

    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $json = $Value | ConvertTo-Json -Depth 100
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    try {
        [System.IO.File]::WriteAllText($temporaryPath, $json, $utf8NoBom)

        if (Test-Path -LiteralPath $Path) {
            [System.IO.File]::Replace($temporaryPath, $Path, $null)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "Playnite*" })
if ($running.Count -gt 0) {
    $processNames = ($running | Select-Object -ExpandProperty ProcessName -Unique) -join ", "
    throw "Playnite is running ($processNames). Exit Playnite completely before changing the isolated fixture."
}

$testProfilesRootFull = Get-FullDirectoryPath $TestProfilesRoot
$profileRecord = Join-Path $testProfilesRootFull "LastExternalSnapshotTestProfile.txt"
if (-not (Test-Path -LiteralPath $profileRecord)) {
    throw "The isolated-profile pointer was not found: $profileRecord"
}

$testProfile = (Get-Content -LiteralPath $profileRecord -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($testProfile)) {
    throw "The isolated-profile pointer is empty."
}

$testProfile = Assert-PathWithin $testProfile $testProfilesRootFull "The selected test profile"
if (-not (Test-Path -LiteralPath $testProfile -PathType Container)) {
    throw "The isolated test profile does not exist: $testProfile"
}

$realProfile = Get-FullDirectoryPath (Join-Path $env:APPDATA "Playnite")
if ([string]::Equals(
    (Get-FullDirectoryPath $testProfile),
    $realProfile,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to operate on the real Playnite profile."
}

$producerRoot = Join-Path $testProfile "ExtensionsData\$producerId"
$producerRoot = Assert-PathWithin $producerRoot $testProfile "The Achievement Sources data directory"
$indexPath = Join-Path $producerRoot "bridge\v1\index.json"
$backupRoot = Join-Path $testProfile "ExternalSnapshotFixtureBackups"
$backupPointer = Join-Path $testProfile "LastExternalSnapshotFixtureBackup.txt"

if ($Mode -eq "Restore") {
    if (-not (Test-Path -LiteralPath $backupPointer)) {
        throw "No fixture-backup pointer was found: $backupPointer"
    }

    $backupDirectory = (Get-Content -LiteralPath $backupPointer -Raw).Trim()
    $backupDirectory = Assert-PathWithin $backupDirectory $backupRoot "The fixture backup"
    $manifestPath = Join-Path $backupDirectory "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "The fixture backup manifest was not found: $manifestPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $originalIndex = Join-Path $backupDirectory "index.original.json"
    $originalSnapshot = Join-Path $backupDirectory "snapshot.original.json"
    $restoreIndexPath = Assert-PathWithin ([string]$manifest.IndexPath) $testProfile "The restored index"
    $restoreSnapshotPath = Assert-PathWithin ([string]$manifest.SnapshotPath) $testProfile "The restored snapshot"

    if (-not (Test-Path -LiteralPath $originalIndex) -or
        -not (Test-Path -LiteralPath $originalSnapshot)) {
        throw "The fixture backup is incomplete: $backupDirectory"
    }

    Copy-Item -LiteralPath $originalIndex -Destination $restoreIndexPath -Force
    Copy-Item -LiteralPath $originalSnapshot -Destination $restoreSnapshotPath -Force

    Write-Host ""
    Write-Host "Original isolated snapshot restored."
    Write-Host "Backup: $backupDirectory"
    Write-Host "Index: $restoreIndexPath"
    Write-Host "Snapshot: $restoreSnapshotPath"
    return
}

if (-not (Test-Path -LiteralPath $indexPath)) {
    throw "The Achievement Sources bridge index was not found: $indexPath"
}

$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
if ([string]$index.Format -ne $indexFormat -or [int]$index.SchemaVersion -ne $schemaVersion) {
    throw "The bridge index format or schema version is unsupported."
}

$entry = @($index.Entries) |
    Where-Object {
        try {
            [Guid]$_.PlayniteGameId -eq $PlayniteGameId
        }
        catch {
            $false
        }
    } |
    Select-Object -First 1

if ($null -eq $entry) {
    throw "The Disco Elysium bridge entry was not found for $($PlayniteGameId.ToString('D'))."
}

$relativeSnapshotPath = [string]$entry.SnapshotRelativePath
if ([string]::IsNullOrWhiteSpace($relativeSnapshotPath) -or
    [System.IO.Path]::IsPathRooted($relativeSnapshotPath) -or
    -not [string]::Equals(
        [System.IO.Path]::GetExtension($relativeSnapshotPath),
        ".json",
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The bridge entry contains an unsafe snapshot path."
}

$snapshotPath = Join-Path $producerRoot ($relativeSnapshotPath.Replace('/', '\'))
$snapshotPath = Assert-PathWithin $snapshotPath $producerRoot "The indexed snapshot"
if (-not (Test-Path -LiteralPath $snapshotPath)) {
    throw "The indexed snapshot was not found: $snapshotPath"
}

$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
if ([string]$snapshot.Format -ne $snapshotFormat -or [int]$snapshot.SchemaVersion -ne $schemaVersion) {
    throw "The snapshot format or schema version is unsupported."
}

if ([Guid]$snapshot.PlayniteGameId -ne $PlayniteGameId) {
    throw "The snapshot Playnite game ID does not match the requested fixture game."
}

$achievements = @($snapshot.Achievements)
if ($achievements.Count -ne $ExpectedAchievementCount) {
    throw "Expected $ExpectedAchievementCount achievements, but found $($achievements.Count)."
}

$duplicateIds = $achievements |
    Group-Object -Property AchievementId |
    Where-Object { [string]::IsNullOrWhiteSpace($_.Name) -or $_.Count -ne 1 }
if (@($duplicateIds).Count -gt 0) {
    throw "The snapshot contains empty or duplicate achievement IDs."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDirectory = Join-Path $backupRoot "DiscoElysium-$stamp"
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

$originalIndex = Join-Path $backupDirectory "index.original.json"
$originalSnapshot = Join-Path $backupDirectory "snapshot.original.json"
Copy-Item -LiteralPath $indexPath -Destination $originalIndex -Force
Copy-Item -LiteralPath $snapshotPath -Destination $originalSnapshot -Force

$manifest = [pscustomobject]@{
    CreatedAtUtc = [DateTime]::UtcNow.ToString("o")
    TestProfile = $testProfile
    IndexPath = $indexPath
    SnapshotPath = $snapshotPath
    PlayniteGameId = $PlayniteGameId.ToString("D")
    OriginalIndexSha256 = (Get-FileHash -LiteralPath $originalIndex -Algorithm SHA256).Hash
    OriginalSnapshotSha256 = (Get-FileHash -LiteralPath $originalSnapshot -Algorithm SHA256).Hash
}
Write-JsonAtomically (Join-Path $backupDirectory "manifest.json") $manifest
$backupDirectory | Set-Content -LiteralPath $backupPointer -Encoding UTF8

$generatedAtUtc = [DateTime]::UtcNow.ToString("o")
$snapshot.GeneratedAtUtc = $generatedAtUtc
$snapshot.StateKnown = $true
$snapshot.IsCompleteSnapshot = $true

foreach ($achievement in $achievements) {
    $achievement.IsUnlocked = $false
    $achievement.UnlockTimeUtc = $null
    $achievement.CurrentProgress = if ($null -ne $achievement.MaximumProgress) { 0 } else { $null }
}

$diagnostics = @($snapshot.Diagnostics) |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
if ($diagnostics -notcontains $fixtureDiagnostic) {
    $diagnostics += $fixtureDiagnostic
}
$snapshot.Diagnostics = @($diagnostics)

$entry.SnapshotGeneratedAtUtc = $generatedAtUtc
$entry.StateKnown = $true
$entry.IsCompleteSnapshot = $true
$entry.AchievementCount = $achievements.Count
$index.GeneratedAtUtc = $generatedAtUtc

Write-JsonAtomically $snapshotPath $snapshot
Write-JsonAtomically $indexPath $index

$verifiedSnapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
$verifiedIndex = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$verifiedEntry = @($verifiedIndex.Entries) |
    Where-Object { [Guid]$_.PlayniteGameId -eq $PlayniteGameId } |
    Select-Object -First 1
$verifiedAchievements = @($verifiedSnapshot.Achievements)

if (-not [bool]$verifiedSnapshot.StateKnown -or
    -not [bool]$verifiedSnapshot.IsCompleteSnapshot -or
    -not [bool]$verifiedEntry.StateKnown -or
    -not [bool]$verifiedEntry.IsCompleteSnapshot -or
    $verifiedAchievements.Count -ne $ExpectedAchievementCount -or
    @($verifiedAchievements | Where-Object { [bool]$_.IsUnlocked }).Count -ne 0) {
    throw "The prepared fixture failed post-write verification. Restore it with -Mode Restore."
}

Write-Host ""
Write-Host "Synthetic complete snapshot fixture prepared."
Write-Host "Test profile: $testProfile"
Write-Host "Snapshot: $snapshotPath"
Write-Host "Backup: $backupDirectory"
Write-Host ""
Write-Host "StateKnown: True"
Write-Host "IsCompleteSnapshot: True"
Write-Host "AchievementCount: $($verifiedAchievements.Count)"
Write-Host "UnlockedCount: 0"
Write-Host "Snapshot SHA256: $((Get-FileHash -LiteralPath $snapshotPath -Algorithm SHA256).Hash)"
Write-Host ""
Write-Host "Restore command:"
Write-Host ".\scripts\prepare-external-snapshot-fixture.ps1 -Mode Restore"
