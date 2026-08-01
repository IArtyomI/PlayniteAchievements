[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$ExpectedSha256,
    [string]$PlayniteProfile,
    [string]$DevelopmentRoot,
    [string]$ExpectedVersion = '3.0.1',
    [switch]$Apply
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
$extensionsPath = Join-Path $PlayniteProfile 'Extensions'
$extensionsDataPath = Join-Path $PlayniteProfile 'ExtensionsData'
$backupRoot = Join-Path $PlayniteProfile 'ExtensionBackups'
$script:failed = 0

function Write-Check {
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
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups['value'].Value.Trim()
}

function Read-ZipEntryText {
    param([string]$ArchivePath, [string]$EntryName)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -ieq $EntryName } | Select-Object -First 1
        if ($null -eq $entry) {
            throw "Package entry '$EntryName' was not found."
        }

        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-InstalledExtension {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return $null
    }

    foreach ($manifestFile in @(Get-ChildItem -LiteralPath $Root -Filter 'extension.yaml' -File -Recurse -ErrorAction SilentlyContinue)) {
        $manifestText = Get-Content -LiteralPath $manifestFile.FullName -Raw
        if ((Get-ManifestValue $manifestText 'Id') -ieq $expectedId) {
            return [pscustomobject]@{
                Root = $manifestFile.Directory.FullName
                Manifest = $manifestText
                ManifestPath = $manifestFile.FullName
            }
        }
    }

    return $null
}

function Get-DevelopmentConflicts {
    param([string]$Root)

    $conflicts = New-Object System.Collections.Generic.List[string]
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'Playnite.DesktopApp.exe'" -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        $commandLine = [string]$process.CommandLine
        if ($commandLine -match '(?i)(bin[\\/]Debug|PlayniteDev|TestProfiles|--userdatadir|PlayniteAchievements\.dll|-PluginPath)') {
            $conflicts.Add("running development Playnite process PID $($process.ProcessId): $commandLine")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Root) -and (Test-Path -LiteralPath $Root -PathType Container)) {
        $launchFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @('.cmd', '.bat', '.ps1', '.json', '.xml', '.ini') })
        foreach ($file in $launchFiles) {
            $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
            if ($text -match '(?i)(bin[\\/]Debug[\\/].*PlayniteAchievements\.dll|[-/]PluginPath\s*[=:]?[^\r\n]*PlayniteAchievements|Playnite\.DesktopApp\.exe[^\r\n]*(Debug|PlayniteAchievements\.dll))') {
                $conflicts.Add("development launch/configuration: $($file.FullName)")
            }
        }
    }

    return @($conflicts | Select-Object -Unique)
}

function Backup-ProgramFiles {
    param([string]$InstalledRoot)

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $destinationRoot = Join-Path $backupRoot "PlayniteAchievements-$stamp"
    $programExtensions = '\.(dll|pdb|yaml|yml|png|jpg|jpeg|ico|xml|config)$'
    $files = @(Get-ChildItem -LiteralPath $InstalledRoot -File -Recurse -ErrorAction Stop |
        Where-Object { $_.Name -match $programExtensions -or $_.Name -match '\.(deps|runtimeconfig)\.json$' })

    if ($files.Count -eq 0) {
        Write-Host 'No replaceable addon program files were found to back up.' -ForegroundColor Yellow
        return $null
    }

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($InstalledRoot.Length).TrimStart('\', '/')
        $destination = Join-Path $destinationRoot $relative
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    return $destinationRoot
}

Write-Host 'Private RC installation preparation (no Playnite profile changes by default)' -ForegroundColor Cyan
Write-Host "Package:          $([System.IO.Path]::GetFullPath($PackagePath))"
Write-Host "Playnite profile:  $([System.IO.Path]::GetFullPath($PlayniteProfile))"
Write-Host "ExtensionsData:   $([System.IO.Path]::GetFullPath($extensionsDataPath)) (never moved, cleared, or modified)"

$resolvedPackage = $null
try {
    $resolvedPackage = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
}
catch {
    Write-Check 'Package exists' $false $_.Exception.Message
}

if ($null -ne $resolvedPackage) {
    Write-Check 'Package exists' (Test-Path -LiteralPath $resolvedPackage -PathType Leaf) $resolvedPackage
    $actualHash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecarPath = "$resolvedPackage.sha256"
    $expectedHash = $ExpectedSha256
    if ([string]::IsNullOrWhiteSpace($expectedHash) -and (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) {
        $sidecar = Get-Content -LiteralPath $sidecarPath -Raw
        $sidecarMatch = [regex]::Match($sidecar, '(?i)\b([0-9a-f]{64})\b')
        if ($sidecarMatch.Success) { $expectedHash = $sidecarMatch.Groups[1].Value.ToLowerInvariant() }
    }
    $hashValid = -not [string]::IsNullOrWhiteSpace($expectedHash) -and $actualHash -eq $expectedHash.ToLowerInvariant()
    Write-Check 'Package SHA-256' $hashValid ("actual=$actualHash expected=$expectedHash")

    $manifestText = Read-ZipEntryText $resolvedPackage 'extension.yaml'
    $packageId = Get-ManifestValue $manifestText 'Id'
    $packageVersion = Get-ManifestValue $manifestText 'Version'
    Write-Check 'Manifest ID' ($packageId -eq $expectedId) "actual=$packageId expected=$expectedId"
    Write-Check 'Manifest version' ($packageVersion -eq $ExpectedVersion) "actual=$packageVersion expected=$ExpectedVersion"
}

Write-Check 'ExtensionsData preserved' (Test-Path -LiteralPath $extensionsDataPath -PathType Container) $extensionsDataPath

$playniteProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'Playnite.DesktopApp.exe'" -ErrorAction SilentlyContinue)
if ($playniteProcesses.Count -gt 0) {
    foreach ($process in $playniteProcesses) {
        Write-Host "Playnite is running (PID $($process.ProcessId)): $([string]$process.CommandLine)" -ForegroundColor Yellow
    }
    Write-Check 'Playnite closed' $false 'Close Playnite yourself; this script will not stop it.'
}
else {
    Write-Check 'Playnite closed' $true 'No Playnite.DesktopApp.exe process detected.'
}

$developmentRoots = @()
if (-not [string]::IsNullOrWhiteSpace($DevelopmentRoot)) {
    $developmentRoots += $DevelopmentRoot
}
if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
    $developmentRoots += Join-Path $env:USERPROFILE 'Documents\PlayniteDev'
}
$developmentConflicts = @($developmentRoots | Select-Object -Unique | ForEach-Object { Get-DevelopmentConflicts $_ })
if ($developmentConflicts.Count -gt 0) {
    foreach ($conflict in $developmentConflicts) { Write-Host "Development conflict: $conflict" -ForegroundColor Yellow }
    Write-Check 'Development build stopped' $false 'Stop using the listed Debug/development launcher or configuration, then rerun.'
}
else {
    Write-Check 'Development build stopped' $true 'No active Debug/development launcher detected.'
}

$installed = Get-InstalledExtension $extensionsPath
if ($null -ne $installed) {
    Write-Host "Existing addon program directory: $($installed.Root)" -ForegroundColor Yellow
    Write-Host 'Only replaceable addon program files may be backed up; ExtensionsData is outside this operation.' -ForegroundColor Yellow
}
else {
    Write-Host 'No installed Playnite Achievements program directory was found; no backup is needed.' -ForegroundColor Yellow
}

if ($script:failed -gt 0) {
    Write-Host 'Preparation stopped. No files were changed.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'PROPOSED FINAL INSTALLATION ACTION:' -ForegroundColor Cyan
Write-Host "1. Close Playnite completely (already verified at this point)."
Write-Host "2. Do not launch any external Debug/development Playnite profile."
Write-Host "3. Install this package manually: $([System.IO.Path]::GetFullPath($PackagePath))"
Write-Host '4. Launch normal Playnite from the normal Start-menu shortcut.'
Write-Host '5. Run scripts\verify-private-rc.ps1 and confirm every required check is PASS.'
Write-Host 'ExtensionsData and the achievement database are not installation targets.' -ForegroundColor Green

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN: no files were changed. Re-run with -Apply only after reviewing the action above.' -ForegroundColor Yellow
    exit 0
}

if ($null -ne $installed) {
    $backupPath = Backup-ProgramFiles $installed.Root
    if ($null -ne $backupPath) {
        Write-Host "Backed up replaceable addon program files to: $backupPath" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'Preparation complete. The package is ready for the exact manual installation action printed above.' -ForegroundColor Green
