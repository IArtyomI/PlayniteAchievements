[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$PlaynitePath,

    [string]$ProfileDirectory = "$env:USERPROFILE\Documents\PlayniteDev\TestProfiles\GseLocalProvider",

    [switch]$SkipBuild,

    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repositoryRoot "source"
$projectPath = Join-Path $sourceDirectory "PlayniteAchievements.csproj"
$solutionDirectory = $sourceDirectory.TrimEnd('\') + '\'
$outputDirectory = Join-Path $sourceDirectory "bin\$Configuration"
$extensionId = "PlayniteAchievements"
$developmentFolderName = "PlayniteAchievements-GseLocalDev"

function Get-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Resolve-MSBuild {
    $fromPath = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $installationPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if (-not [string]::IsNullOrWhiteSpace($installationPath)) {
            $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    throw "MSBuild was not found. Install Visual Studio 2022 Build Tools with managed desktop and .NET Framework 4.6.2 targeting support."
}

function Resolve-PlayniteDesktopPath {
    param([string]$ConfiguredPath)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        $resolved = [System.IO.Path]::GetFullPath($ConfiguredPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Playnite.DesktopApp.exe was not found at: $resolved"
        }

        return $resolved
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Playnite\Playnite.DesktopApp.exe"),
        (Join-Path $env:ProgramFiles "Playnite\Playnite.DesktopApp.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Playnite\Playnite.DesktopApp.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "Playnite.DesktopApp.exe was not found automatically. Pass -PlaynitePath with its full path."
}

function Get-ManifestId {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    foreach ($line in [System.IO.File]::ReadAllLines($ManifestPath)) {
        if ($line -match '^\s*Id\s*:\s*(.+?)\s*$') {
            return $Matches[1].Trim().Trim('"', "'")
        }
    }

    return ""
}

$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "Playnite*" })
if ($running.Count -gt 0) {
    $names = ($running | Select-Object -ExpandProperty ProcessName -Unique) -join ", "
    throw "Playnite is running ($names). Exit Playnite completely before rebuilding or replacing a plugin DLL."
}

$profile = Get-NormalizedPath $ProfileDirectory
$realInstalledProfile = Get-NormalizedPath (Join-Path $env:APPDATA "Playnite")
if ([string]::Equals($profile, $realInstalledProfile, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to deploy the development build into the real installed Playnite profile. Use an isolated profile directory."
}

if (-not $SkipBuild) {
    $msbuild = Resolve-MSBuild
    Write-Host "Building Playnite Achievements $Configuration..." -ForegroundColor Cyan
    & $msbuild $projectPath /restore /t:Rebuild /m /p:Configuration=$Configuration /p:RestorePackagesConfig=true "/p:SolutionDir=$solutionDirectory" /nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Plugin build failed with exit code $LASTEXITCODE."
    }
}

$manifestSource = Join-Path $outputDirectory "extension.yaml"
$assemblySource = Join-Path $outputDirectory "PlayniteAchievements.dll"
if (-not (Test-Path -LiteralPath $manifestSource -PathType Leaf) -or
    -not (Test-Path -LiteralPath $assemblySource -PathType Leaf)) {
    throw "The plugin build output is incomplete: $outputDirectory"
}

New-Item -ItemType Directory -Path $profile -Force | Out-Null
$extensionsDirectory = Join-Path $profile "Extensions"
New-Item -ItemType Directory -Path $extensionsDirectory -Force | Out-Null
$deploymentDirectory = Join-Path $extensionsDirectory $developmentFolderName

$conflictingManifests = @(
    Get-ChildItem -LiteralPath $extensionsDirectory -Recurse -File -Filter "extension.yaml" -ErrorAction SilentlyContinue |
        Where-Object {
            -not $_.FullName.StartsWith(
                $deploymentDirectory + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase) -and
            (Get-ManifestId $_.FullName) -ieq $extensionId
        }
)

if ($conflictingManifests.Count -gt 0) {
    Write-Host ""
    Write-Host "Conflicting Playnite Achievements development installations:" -ForegroundColor Red
    $conflictingManifests | Select-Object FullName | Format-Table -AutoSize
    throw "The isolated profile already contains another extension with Id '$extensionId'. Remove or disable that copy before deploying this build."
}

if (Test-Path -LiteralPath $deploymentDirectory) {
    Remove-Item -LiteralPath $deploymentDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $deploymentDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $outputDirectory "*") -Destination $deploymentDirectory -Recurse -Force

$deployedAssembly = Join-Path $deploymentDirectory "PlayniteAchievements.dll"
if (-not (Test-Path -LiteralPath $deployedAssembly -PathType Leaf)) {
    throw "Development deployment did not produce the plugin assembly."
}

$recordPath = Join-Path (Split-Path -Parent $profile) "LastGseLocalTestProfile.txt"
[System.IO.File]::WriteAllText(
    $recordPath,
    $profile,
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "GSE local provider development build deployed." -ForegroundColor Green
Write-Host "Profile:   $profile"
Write-Host "Extension: $deploymentDirectory"
Write-Host "Assembly:  $deployedAssembly"
Write-Host "Log:       $(Join-Path $profile 'extensions.log')"

if ($NoLaunch) {
    Write-Host ""
    Write-Host "Launch skipped because -NoLaunch was supplied." -ForegroundColor Yellow
    return
}

$playnite = Resolve-PlayniteDesktopPath $PlaynitePath
$arguments = @(
    "--userdatadir",
    ('"' + $profile + '"'),
    "--startdesktop",
    "--nolibupdate",
    "--hidesplashscreen"
)

Write-Host ""
Write-Host "Starting isolated Playnite development profile..." -ForegroundColor Cyan
Write-Host "Executable: $playnite"
Start-Process -FilePath $playnite -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $playnite)
