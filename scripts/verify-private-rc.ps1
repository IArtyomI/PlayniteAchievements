[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$PlayniteProfile,
    [string]$GameInstallPath,
    [string]$AppId = '2863680',
    [string]$ExpectedVersion = '3.0.1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedId = 'PlayniteAchievements'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $repositoryRoot 'dist\PlayniteAchievements_3_0_1.pext'
}
if ([string]::IsNullOrWhiteSpace($PlayniteProfile)) {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        throw 'Playnite profile could not be discovered automatically. Re-run with -PlayniteProfile <Playnite-profile-directory>.'
    }

    $PlayniteProfile = Join-Path $env:APPDATA 'Playnite'
}
if ([string]::IsNullOrWhiteSpace($GameInstallPath)) {
    throw 'Game install path could not be discovered automatically. Re-run with -GameInstallPath <ZERO-PARADES-install-directory>.'
}
$extensionsPath = Join-Path $PlayniteProfile 'Extensions'
$extensionsDataPath = Join-Path $PlayniteProfile 'ExtensionsData'
$addonDataPath = Join-Path $extensionsDataPath 'e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b'
$runtimePath = Join-Path $env:APPDATA "GSE Saves\$AppId\achievements.json"
$script:failed = 0

function Report {
    param([string]$Name, [bool]$Passed, [string]$Detail)

    if ($Passed) {
        Write-Host ("PASS  {0}: {1}" -f $Name, $Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("FAIL  {0}: {1}" -f $Name, $Detail) -ForegroundColor Red
        $script:failed++
    }
}

function Get-ManifestValue {
    param([string]$Manifest, [string]$Key)

    $match = [regex]::Match($Manifest, '(?m)^' + [regex]::Escape($Key) + ':\s*(?<value>[^\r\n]+)\s*$')
    if (-not $match.Success) { return $null }
    return $match.Groups['value'].Value.Trim()
}

function Get-InstalledExtension {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $null }
    foreach ($manifestFile in @(Get-ChildItem -LiteralPath $Root -Filter 'extension.yaml' -File -Recurse -ErrorAction SilentlyContinue)) {
        $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw
        if ((Get-ManifestValue $manifest 'Id') -ieq $expectedId) {
            return [pscustomobject]@{
                Root = $manifestFile.Directory.FullName
                Manifest = $manifest
            }
        }
    }
    return $null
}

function Read-ZipEntryText {
    param([string]$ArchivePath, [string]$EntryName)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -ieq $EntryName } | Select-Object -First 1
        if ($null -eq $entry) { throw "Package entry '$EntryName' was not found." }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Get-ZipEntryHash {
    param([string]$ArchivePath, [string]$EntryName)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -ieq $EntryName } | Select-Object -First 1
        if ($null -eq $entry) { throw "Package entry '$EntryName' was not found." }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $stream = $entry.Open()
        try { return ((-join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }))).ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Find-SteamSettings {
    param([string]$Root, [string]$WantedAppId)

    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @(
        (Join-Path $Root 'steam_settings'),
        (Join-Path $Root 'ZeroParades_Data\Plugins\x86_64\steam_settings')
    )) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'achievements.json') -PathType Leaf) { $candidates.Add($candidate) }
    }
    foreach ($candidate in @(Get-ChildItem -LiteralPath $Root -Directory -Recurse -Filter 'steam_settings' -ErrorAction SilentlyContinue)) {
        if (Test-Path -LiteralPath (Join-Path $candidate.FullName 'achievements.json') -PathType Leaf) { $candidates.Add($candidate.FullName) }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        $appIdFile = Join-Path $candidate 'steam_appid.txt'
        $candidateAppId = if (Test-Path -LiteralPath $appIdFile -PathType Leaf) { (Get-Content -LiteralPath $appIdFile -Raw).Trim() } else { '' }
        if ([string]::IsNullOrWhiteSpace($candidateAppId) -or $candidateAppId -eq $WantedAppId) { return $candidate }
    }
    return $null
}

function Resolve-IconPath {
    param([string]$SettingsDirectory, [string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    if ($Value -match '^(?i)https?://') { return $Value }
    if ([System.IO.Path]::IsPathRooted($Value)) { return [System.IO.Path]::GetFullPath($Value) }
    return [System.IO.Path]::GetFullPath((Join-Path $SettingsDirectory ($Value.Replace('/', '\'))))
}

Write-Host 'Private RC read-only verification' -ForegroundColor Cyan
Write-Host 'No files, profile data, package contents, or databases will be changed.'

$installed = Get-InstalledExtension $extensionsPath
$installedId = if ($null -ne $installed) { Get-ManifestValue $installed.Manifest 'Id' } else { $null }
$installedVersion = if ($null -ne $installed) { Get-ManifestValue $installed.Manifest 'Version' } else { $null }
Report 'Installed extension ID' ($installedId -eq $expectedId) "actual=$installedId expected=$expectedId"
Report 'Installed version' ($installedVersion -eq $ExpectedVersion) "actual=$installedVersion expected=$ExpectedVersion"

$packageExists = Test-Path -LiteralPath $PackagePath -PathType Leaf
$packageManifest = $null
$packageModule = 'PlayniteAchievements.dll'
if ($packageExists) {
    $packageManifest = Read-ZipEntryText $PackagePath 'extension.yaml'
    $packageModule = Get-ManifestValue $packageManifest 'Module'
}
$installedDll = if ($null -ne $installed) { Join-Path $installed.Root $packageModule } else { $null }
$packageDllHash = if ($packageExists) { Get-ZipEntryHash $PackagePath $packageModule } else { $null }
$installedDllHash = if ($null -ne $installedDll -and (Test-Path -LiteralPath $installedDll -PathType Leaf)) { (Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
Report 'Installed DLL hash' ($null -ne $installedDllHash -and $installedDllHash -eq $packageDllHash) "installed=$installedDllHash package=$packageDllHash"

Report 'ExtensionsData still present' (Test-Path -LiteralPath $extensionsDataPath -PathType Container) $extensionsDataPath

$logCandidates = @(
    (Join-Path $addonDataPath 'playniteachievements.log'),
    (Join-Path $PlayniteProfile 'playnite.log')
)
$logText = (($logCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | ForEach-Object { Get-Content -LiteralPath $_ -Raw -ErrorAction SilentlyContinue }) -join "`n")
Report 'GseLocal provider loaded' ($logText -match '(?i)(\[GseLocal\].*IsCapable|provider=GseLocal|GseLocal.*Loaded)') 'Playnite log contains GseLocal activity'
Report 'Steam schema enrichment succeeded' ($logText -match '(?i)Enriched .*matched=55/55') 'Playnite log contains matched=55/55 enrichment'
Report 'ZERO PARADES contains 55 achievements' ($logText -match "(?i)Loaded 55 achievements for 'ZERO PARADES For Dead Spies'") 'Playnite log contains the 55-row load'
Report 'Unlocked count is 1' ($logText -match "(?i)Loaded 55 achievements for 'ZERO PARADES For Dead Spies' with unlocked=1") 'Playnite log contains unlocked=1'

$settingsDirectory = Find-SteamSettings $GameInstallPath $AppId
$schema = $null
$runtime = $null
$schemaCount = 0
$runtimeUnlocked = 0
$conditioning = $null
$resolvedIconCount = 0
$iconReferenceCount = 0
if ($null -ne $settingsDirectory) {
    $schemaPath = Join-Path $settingsDirectory 'achievements.json'
    $schemaParsed = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($schemaPath))
    [object[]]$schema = $schemaParsed
    $schemaCount = $schema.Count
    if (Test-Path -LiteralPath $runtimePath -PathType Leaf) {
        $runtime = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($runtimePath))
        $runtimeProperties = @($runtime.PSObject.Properties)
        $runtimeUnlocked = @($runtimeProperties | Where-Object { [bool]$_.Value.earned }).Count
        $conditioning = $runtime.PSObject.Properties['ACH_CONDITIONING']
    }
    foreach ($definition in $schema) {
        foreach ($iconProperty in @('icon', 'icon_gray', 'icongray')) {
            $iconPropertyValue = $definition.PSObject.Properties[$iconProperty]
            $iconValue = if ($null -ne $iconPropertyValue) { [string]$iconPropertyValue.Value } else { '' }
            if ([string]::IsNullOrWhiteSpace($iconValue)) { continue }
            $iconReferenceCount++
            $resolved = Resolve-IconPath $settingsDirectory $iconValue
            if (($resolved -match '^(?i)https?://') -or (Test-Path -LiteralPath $resolved -PathType Leaf)) { $resolvedIconCount++ }
        }
    }
}
Report 'Steam schema count is 55' ($schemaCount -eq 55) "settings=$settingsDirectory count=$schemaCount"
Report 'Unlocked count is 1 (local state)' ($runtimeUnlocked -eq 1) "runtime=$runtimePath unlocked=$runtimeUnlocked"
$conditioningEarned = $null -ne $conditioning -and [bool]$conditioning.Value.earned
$conditioningTime = if ($conditioningEarned) { [long]$conditioning.Value.earned_time } else { 0 }
Report 'ACH_CONDITIONING is unlocked' $conditioningEarned "earned_time=$conditioningTime"
Report 'Icon paths resolve' ($iconReferenceCount -gt 0 -and $resolvedIconCount -eq $iconReferenceCount) "resolved=$resolvedIconCount/$iconReferenceCount"

Write-Host ''
if ($script:failed -eq 0) {
    Write-Host 'VERIFICATION PASSED' -ForegroundColor Green
    exit 0
}

Write-Host "VERIFICATION FAILED ($script:failed check(s))" -ForegroundColor Red
exit 1
