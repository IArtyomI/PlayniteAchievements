[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipClean,
    [switch]$SkipExistingTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot "source\PlayniteAchievements.sln"
$existingTestProject = Join-Path $repositoryRoot "tests\PlayniteAchievements.Tests\PlayniteAchievements.Tests.csproj"
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

function Resolve-VSTest {
    $fromPath = Get-Command vstest.console.exe -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        return $null
    }

    $installationPaths = @(
        & $vswhere -latest -products * -requires Microsoft.VisualStudio.PackageGroup.TestTools.Core -property installationPath
        & $vswhere -latest -products * -property installationPath
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($installationPath in $installationPaths) {
        $candidates = @(
            (Join-Path $installationPath "Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe"),
            (Join-Path $installationPath "Common7\IDE\Extensions\TestPlatform\vstest.console.exe")
        )

        foreach ($candidate in $candidates) {
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    return $null
}

$msbuild = Resolve-MSBuild
$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw "dotnet.exe was not found. Install a current .NET SDK to run the tests."
}

if (-not $SkipClean) {
    $pathsToClean = @(
        (Join-Path $repositoryRoot "source\bin"),
        (Join-Path $repositoryRoot "source\obj"),
        (Join-Path $repositoryRoot "tests\PlayniteAchievements.Tests\bin"),
        (Join-Path $repositoryRoot "tests\PlayniteAchievements.Tests\obj"),
        (Join-Path $repositoryRoot "tests\ExternalSnapshot.ContractTests\bin"),
        (Join-Path $repositoryRoot "tests\ExternalSnapshot.ContractTests\obj")
    )

    foreach ($path in $pathsToClean) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
        }
    }
}

Write-Host "Building $Configuration with $msbuild"
& $msbuild $solutionPath /restore /t:Rebuild /m /p:Configuration=$Configuration /p:RestorePackagesConfig=true /nologo
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE."
}

Write-Host "Running external snapshot contract tests"
& $dotnet.Source test $contractTestProject --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "External snapshot contract tests failed with exit code $LASTEXITCODE."
}

if (-not $SkipExistingTests) {
    $existingTestAssembly = Join-Path $repositoryRoot "tests\PlayniteAchievements.Tests\bin\$Configuration\net462\PlayniteAchievements.Tests.dll"
    if (-not (Test-Path $existingTestAssembly)) {
        throw "The existing Playnite Achievements test assembly was not produced."
    }

    $vstest = Resolve-VSTest
    if (-not [string]::IsNullOrWhiteSpace($vstest)) {
        Write-Host "Running existing tests with Visual Studio Test Platform: $existingTestAssembly"
        & $vstest $existingTestAssembly /Platform:x64
    }
    else {
        Write-Host "Visual Studio Test Platform was not found; running existing tests with dotnet test."
        & $dotnet.Source test $existingTestProject --configuration $Configuration --no-build --no-restore --nologo
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Existing tests failed with exit code $LASTEXITCODE."
    }
}

$pluginOutput = Join-Path $repositoryRoot "source\bin\$Configuration\PlayniteAchievements.dll"
if (-not (Test-Path $pluginOutput)) {
    throw "The expected PlayniteAchievements.dll output was not produced."
}

Write-Host ""
Write-Host "Local validation passed."
Write-Host "Plugin output: $pluginOutput"
Write-Host "Next: load the fork build as a Playnite external development extension and test a single game with a complete known-state snapshot."
