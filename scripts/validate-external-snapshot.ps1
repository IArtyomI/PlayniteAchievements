[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipClean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $repositoryRoot "source\PlayniteAchievements.csproj"
$contractTestProject = Join-Path $repositoryRoot "tests\ExternalSnapshot.ContractTests\ExternalSnapshot.ContractTests.csproj"

$playniteProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "Playnite*" })
if ($playniteProcesses.Count -gt 0) {
    $processNames = ($playniteProcesses | Select-Object -ExpandProperty ProcessName -Unique) -join ", "
    throw "Playnite is running ($processNames). Exit Playnite completely before rebuilding the extension."
}

function Resolve-MSBuild {
    $fromPath = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installationPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if (-not [string]::IsNullOrWhiteSpace($installationPath)) {
            $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    throw "MSBuild was not found. Install Visual Studio 2022 Build Tools with managed desktop and .NET Framework 4.6.2 targeting support."
}

$msbuild = Resolve-MSBuild
$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw "dotnet.exe was not found. Install a current .NET SDK to run the contract tests."
}

if (-not $SkipClean) {
    $pathsToClean = @(
        (Join-Path $repositoryRoot "source\bin"),
        (Join-Path $repositoryRoot "source\obj"),
        (Join-Path $repositoryRoot "tests\ExternalSnapshot.ContractTests\bin"),
        (Join-Path $repositoryRoot "tests\ExternalSnapshot.ContractTests\obj")
    )

    foreach ($path in $pathsToClean) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
        }
    }
}

Write-Host "Building plugin $Configuration with $msbuild"
& $msbuild $pluginProject /restore /t:Rebuild /m /p:Configuration=$Configuration /p:RestorePackagesConfig=true /nologo
if ($LASTEXITCODE -ne 0) {
    throw "Plugin build failed with exit code $LASTEXITCODE."
}

Write-Host "Running external snapshot contract tests"
& $dotnet.Source test $contractTestProject --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "External snapshot contract tests failed with exit code $LASTEXITCODE."
}

$pluginOutput = Join-Path $repositoryRoot "source\bin\$Configuration\PlayniteAchievements.dll"
if (-not (Test-Path $pluginOutput)) {
    throw "The expected PlayniteAchievements.dll output was not produced."
}

Write-Host ""
Write-Host "Local validation passed."
Write-Host "Plugin output: $pluginOutput"
Write-Host "Next: load the fork build as a Playnite external development extension and test a single game with a complete known-state snapshot."
