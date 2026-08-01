[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,

    [string]$AppId,

    [string]$ApplicationDataDirectory = $env:APPDATA
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$maximumDirectoriesScanned = 512
$maximumDepth = 6
$maximumAchievements = 10000

function Get-NormalizedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label is empty."
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "$Label does not exist: $fullPath"
    }

    return $fullPath
}

function Normalize-AppId {
    param([string]$Value)

    $trimmed = ([string]$Value).Trim()
    if ($trimmed -match '^\d+$') {
        return $trimmed
    }

    return ""
}

function Add-SettingsCandidate {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.HashSet[string]]$Set,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    try {
        $candidate = [System.IO.Path]::GetFullPath($Path)
        if ((Test-Path -LiteralPath $candidate -PathType Container) -and
            (Test-Path -LiteralPath (Join-Path $candidate "achievements.json") -PathType Leaf)) {
            [void]$Set.Add($candidate)
        }
    }
    catch {
    }
}

function Find-SteamSettingsDirectories {
    param([Parameter(Mandatory = $true)][string]$Root)

    $results = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    Add-SettingsCandidate $results (Join-Path $Root "steam_settings")
    Add-SettingsCandidate $results (Join-Path $Root "Plugins\x86_64\steam_settings")

    try {
        foreach ($dataDirectory in Get-ChildItem -LiteralPath $Root -Directory -Filter "*_Data" -ErrorAction Stop) {
            Add-SettingsCandidate $results (Join-Path $dataDirectory.FullName "Plugins\x86_64\steam_settings")
        }
    }
    catch {
    }

    $queue = New-Object 'System.Collections.Generic.Queue[object]'
    $queue.Enqueue([pscustomobject]@{ Path = $Root; Depth = 0 })
    $scanned = 0

    while ($queue.Count -gt 0 -and $scanned -lt $maximumDirectoriesScanned) {
        $item = $queue.Dequeue()
        $scanned++

        try {
            $children = @(Get-ChildItem -LiteralPath $item.Path -Directory -Force -ErrorAction Stop)
        }
        catch {
            continue
        }

        foreach ($child in $children | Sort-Object FullName) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                continue
            }

            if ($child.Name -ieq "steam_settings") {
                Add-SettingsCandidate $results $child.FullName
                continue
            }

            if ([int]$item.Depth -lt $maximumDepth) {
                $queue.Enqueue([pscustomobject]@{
                    Path = $child.FullName
                    Depth = ([int]$item.Depth + 1)
                })
            }
        }
    }

    return @($results | Sort-Object)
}

function Read-AppIdFromSettings {
    param([Parameter(Mandatory = $true)][string]$SettingsDirectory)

    $path = Join-Path $SettingsDirectory "steam_appid.txt"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return ""
    }

    return Normalize-AppId ([System.IO.File]::ReadAllText($path))
}

function Get-LocalizedText {
    param($Value)

    if ($null -eq $Value) {
        return ""
    }

    if ($Value -is [string]) {
        return [string]$Value
    }

    $english = $Value.PSObject.Properties["english"]
    if ($null -ne $english -and -not [string]::IsNullOrWhiteSpace([string]$english.Value)) {
        return [string]$english.Value
    }

    foreach ($property in $Value.PSObject.Properties) {
        if (-not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return [string]$property.Value
        }
    }

    return ""
}

$installRoot = Get-NormalizedDirectory $InstallDirectory "Install directory"
$appDataRoot = Get-NormalizedDirectory $ApplicationDataDirectory "Application-data directory"
$preferredAppId = Normalize-AppId $AppId

Write-Host ""
Write-Host "GSE local-source validation" -ForegroundColor Cyan
Write-Host "Install directory: $installRoot"
Write-Host "Application data:  $appDataRoot"
Write-Host "Requested AppID:   $(if ($preferredAppId) { $preferredAppId } else { '(infer)' })"

$settingsCandidates = @(Find-SteamSettingsDirectories $installRoot)
if ($settingsCandidates.Count -eq 0) {
    throw "No steam_settings directory containing achievements.json was found under the install directory."
}

$selectedSettings = $null
$selectedAppId = $null
foreach ($candidate in $settingsCandidates) {
    $candidateAppId = Read-AppIdFromSettings $candidate
    if ($preferredAppId -and $candidateAppId -and $candidateAppId -ne $preferredAppId) {
        continue
    }

    $effectiveAppId = if ($candidateAppId) { $candidateAppId } else { $preferredAppId }
    if (-not $effectiveAppId) {
        continue
    }

    $selectedSettings = $candidate
    $selectedAppId = $effectiveAppId
    break
}

if (-not $selectedSettings) {
    throw "No steam_settings directory matched the requested or inferred AppID."
}

$schemaPath = Join-Path $selectedSettings "achievements.json"
$runtimeCandidates = @(
    (Join-Path $appDataRoot "GSE Saves\$selectedAppId"),
    (Join-Path $appDataRoot "Goldberg SteamEmu Saves\$selectedAppId")
)

$runtimeDirectory = $runtimeCandidates |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ "achievements.json") -PathType Leaf } |
    Sort-Object { (Get-Item -LiteralPath (Join-Path $_ "achievements.json")).LastWriteTimeUtc } -Descending |
    Select-Object -First 1

if (-not $runtimeDirectory) {
    $runtimeDirectory = $runtimeCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
        Select-Object -First 1
}

if (-not $runtimeDirectory) {
    $runtimeDirectory = $runtimeCandidates[0]
}

$runtimePath = Join-Path $runtimeDirectory "achievements.json"

Write-Host ""
Write-Host "Resolved source" -ForegroundColor Green
Write-Host "AppID:             $selectedAppId"
Write-Host "steam_settings:    $selectedSettings"
Write-Host "Schema:            $schemaPath"
Write-Host "Runtime directory: $runtimeDirectory"
Write-Host "Runtime state:     $runtimePath"

if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
    throw "The runtime achievements.json does not exist yet. GSE must create a complete runtime state before the provider treats it as authoritative."
}

$schemaParsed = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($schemaPath))
[object[]]$schema = $schemaParsed
$runtime = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($runtimePath))

if ($schema.Count -eq 0 -or $schema.Count -gt $maximumAchievements) {
    throw "The schema achievement count $($schema.Count) is unsupported."
}

$runtimeProperties = @($runtime.PSObject.Properties)
if ($runtimeProperties.Count -gt $maximumAchievements) {
    throw "The runtime achievement count $($runtimeProperties.Count) is unsupported."
}

$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$missingRuntime = New-Object 'System.Collections.Generic.List[string]'
$missingIcons = New-Object 'System.Collections.Generic.List[string]'
$unlocked = New-Object 'System.Collections.Generic.List[object]'

foreach ($definition in $schema) {
    $apiName = [string]$definition.name
    if ([string]::IsNullOrWhiteSpace($apiName) -or -not $seen.Add($apiName)) {
        throw "The schema contains an empty or duplicate achievement API name."
    }

    $stateProperty = $runtime.PSObject.Properties[$apiName]
    if ($null -eq $stateProperty) {
        $missingRuntime.Add($apiName)
        continue
    }

    $state = $stateProperty.Value
    if ([bool]$state.earned) {
        $unlocked.Add([pscustomobject]@{
            ApiName = $apiName
            DisplayName = Get-LocalizedText $definition.displayName
            EarnedTime = [long]$state.earned_time
        })
    }

    foreach ($iconProperty in @("icon", "icon_gray", "icongray")) {
        $relative = [string]$definition.$iconProperty
        if ([string]::IsNullOrWhiteSpace($relative)) {
            continue
        }

        $iconPath = if ([System.IO.Path]::IsPathRooted($relative)) {
            [System.IO.Path]::GetFullPath($relative)
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path $selectedSettings ($relative.Replace('/', '\'))))
        }

        if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
            $missingIcons.Add("$apiName [$iconProperty] -> $iconPath")
        }
    }
}

Write-Host ""
Write-Host "Validation summary" -ForegroundColor Cyan
Write-Host "Schema achievements:  $($schema.Count)"
Write-Host "Runtime achievements: $($runtimeProperties.Count)"
Write-Host "Unlocked:             $($unlocked.Count)"
Write-Host "Missing runtime rows: $($missingRuntime.Count)"
Write-Host "Missing icon files:   $($missingIcons.Count)"

if ($unlocked.Count -gt 0) {
    Write-Host ""
    Write-Host "Unlocked achievements" -ForegroundColor Green
    $unlocked | Format-Table -AutoSize
}

if ($missingRuntime.Count -gt 0) {
    Write-Host ""
    Write-Host "Missing runtime entries" -ForegroundColor Red
    $missingRuntime | Select-Object -First 25
}

if ($missingIcons.Count -gt 0) {
    Write-Host ""
    Write-Host "Missing icons" -ForegroundColor Yellow
    $missingIcons | Select-Object -First 25
}

if ($missingRuntime.Count -gt 0) {
    throw "The runtime state is incomplete. The addon will preserve prior cached data instead of manufacturing locked achievements."
}

Write-Host ""
Write-Host "GSE local source is authoritative and ready for Playnite import." -ForegroundColor Green
