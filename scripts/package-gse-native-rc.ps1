[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$ToolboxPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'source'
$solutionPath = Join-Path $sourceRoot 'PlayniteAchievements.sln'
$productionProjectPath = Join-Path $sourceRoot 'PlayniteAchievements.csproj'
$manifestPath = Join-Path $sourceRoot 'extension.yaml'
$outputPath = Join-Path $sourceRoot "bin\$Configuration"
$distPath = Join-Path $repositoryRoot 'dist'

function Resolve-Executable([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    return $null
}

function Assert-ExitCode([string]$operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$operation failed with exit code $LASTEXITCODE."
    }
}

function Resolve-Toolbox([string]$requestedPath) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($requestedPath)) {
        $candidates += $requestedPath
    }

    $candidates += @(
        (Join-Path ${env:ProgramFiles} 'Playnite\toolbox.exe'),
        (Join-Path ${env:LOCALAPPDATA} 'Playnite\toolbox.exe'),
        'C:\Playnite_dev\toolbox.exe',
        'C:\Projects\Playnite_dev\toolbox.exe',
        'D:\Playnite_dev\toolbox.exe',
        'D:\Projects\Playnite_dev\toolbox.exe',
        'F:\Playnite_dev\toolbox.exe',
        'G:\Playnite_dev\toolbox.exe'
    )

    $candidates += Get-ChildItem -Path @(
        (Join-Path ${env:ProgramFiles} 'Playnite'),
        (Join-Path ${env:LOCALAPPDATA} 'Playnite')
    ) -Filter 'toolbox.exe' -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName

    foreach ($candidate in $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Playnite Toolbox (toolbox.exe) was not found. Pass -ToolboxPath or install Playnite.'
}

function Get-ManifestValue([string]$manifest, [string]$key) {
    $pattern = '(?m)^' + [regex]::Escape($key) + ':\s*(?<value>[^\r\n]+)\s*$'
    $match = [regex]::Match($manifest, $pattern)
    if (-not $match.Success) {
        throw "extension.yaml is missing '$key'."
    }

    return $match.Groups['value'].Value.Trim()
}

New-Item -ItemType Directory -Path $distPath -Force | Out-Null

$nuget = Resolve-Executable 'nuget.exe'

$msbuild = Resolve-Executable 'msbuild.exe'
if ($null -eq $msbuild) {
    $msbuild = Get-ChildItem -Path @(
        'C:\Program Files\Microsoft Visual Studio',
        'C:\Program Files (x86)\Microsoft Visual Studio'
    ) -Filter 'MSBuild.exe' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\MSBuild\\Current\\Bin\\MSBuild\.exe$' } |
        Select-Object -ExpandProperty FullName -First 1
}
if ($null -eq $msbuild) {
    throw 'MSBuild.exe was not found. Install Visual Studio Build Tools or add MSBuild to PATH.'
}

$dotnet = Resolve-Executable 'dotnet.exe'
if ($null -eq $dotnet) {
    throw 'dotnet.exe was not found on PATH.'
}

$toolbox = Resolve-Toolbox $ToolboxPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifestId = Get-ManifestValue $manifest 'Id'
$manifestName = Get-ManifestValue $manifest 'Name'
$manifestVersion = Get-ManifestValue $manifest 'Version'
$manifestModule = Get-ManifestValue $manifest 'Module'
$manifestType = Get-ManifestValue $manifest 'Type'
$manifestIcon = Get-ManifestValue $manifest 'Icon'

if ($manifestId -ne 'PlayniteAchievements') { throw "Unexpected extension ID '$manifestId'." }
if ($manifestName -ne 'Playnite Achievements') { throw "Unexpected extension name '$manifestName'." }
if ($manifestVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid extension version '$manifestVersion'." }
if ($manifestModule -ne 'PlayniteAchievements.dll') { throw "Unexpected extension module '$manifestModule'." }
if ($manifestType -ne 'GenericPlugin') { throw "Unexpected extension type '$manifestType'." }
if ($manifestIcon -ne 'icon.png') { throw "Unexpected extension icon '$manifestIcon'." }

Write-Host "Restoring production packages..." -ForegroundColor Cyan
if ($null -ne $nuget) {
    & $nuget restore $solutionPath -NonInteractive
    Assert-ExitCode 'NuGet restore'
} else {
    Write-Warning 'NuGet.exe is not installed; using dotnet restore and verifying the existing packages.config package root.'
    & $dotnet restore $solutionPath /p:RestorePackagesConfig=true --ignore-failed-sources
    Assert-ExitCode 'dotnet production restore'
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'packages') -PathType Container)) {
        throw 'The production packages.config restore root is missing. Install NuGet.exe for a clean restore.'
    }
}

Write-Host "Restoring contract-test packages..." -ForegroundColor Cyan
& $dotnet restore (Join-Path $repositoryRoot 'tests\GseLocal.ContractTests\GseLocal.ContractTests.csproj') --ignore-failed-sources
Assert-ExitCode 'GSE contract-test restore'

Write-Host "Building production Release..." -ForegroundColor Cyan
& $msbuild $productionProjectPath /t:Build "/p:Configuration=$Configuration" /p:RestorePackagesConfig=true /m
Assert-ExitCode 'Production build'

Write-Host "Running GSE contract tests..." -ForegroundColor Cyan
& $dotnet test (Join-Path $repositoryRoot 'tests\GseLocal.ContractTests\GseLocal.ContractTests.csproj') -c $Configuration --no-restore
Assert-ExitCode 'GSE contract tests'

if (-not (Test-Path -LiteralPath (Join-Path $outputPath $manifestModule) -PathType Leaf)) {
    throw "Build output is missing $manifestModule."
}

Write-Host "Packaging with Playnite Toolbox: $toolbox" -ForegroundColor Cyan
& $toolbox pack $outputPath $distPath
Assert-ExitCode 'Playnite Toolbox pack'

$packageName = "PlayniteAchievements_$($manifestVersion.Replace('.', '_')).pext"
$package = Get-Item -LiteralPath (Join-Path $distPath $packageName) -ErrorAction SilentlyContinue
if ($null -eq $package) {
    throw "Playnite Toolbox did not create the expected $packageName in $distPath."
}

$hash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$($package.FullName).sha256"
Set-Content -LiteralPath $hashPath -Value "$hash  $($package.Name)" -Encoding ASCII

$metadata = [ordered]@{
    package = $package.Name
    sha256 = $hash
    extensionId = $manifestId
    version = $manifestVersion
    configuration = $Configuration
    branch = (& git -C $repositoryRoot branch --show-current).Trim()
    commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
}
Set-Content -LiteralPath (Join-Path $distPath 'gse-native-rc.json') -Value ($metadata | ConvertTo-Json) -Encoding UTF8

Write-Host "Private RC package: $($package.FullName)" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor Green
