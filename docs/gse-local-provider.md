# Native GSE / Goldberg local provider

## Purpose

The GSE local provider imports local Steam-emulator achievement state through the normal Playnite Achievements provider, refresh, cache, icon, and notification pipeline.

It is a read-only consumer. It does not install, configure, or redistribute GSE/Goldberg binaries and does not modify game executables or game data.

## Supported source layout

The provider discovers a game-local `steam_settings` directory beneath the Playnite game's expanded install directory. Common layouts include:

```text
<Game>\steam_settings\achievements.json
<Game>\Plugins\x86_64\steam_settings\achievements.json
<Game>\<Game>_Data\Plugins\x86_64\steam_settings\achievements.json
```

The matching AppID is read from:

```text
steam_settings\steam_appid.txt
```

Runtime achievement state is read from the newest matching file in:

```text
%APPDATA%\GSE Saves\<AppID>\achievements.json
%APPDATA%\Goldberg SteamEmu Saves\<AppID>\achievements.json
```

## State safety

A local source is selected only when both the schema and an actual runtime `achievements.json` exist.

The runtime file must contain one state row for every unique achievement API name in the schema. Missing or duplicate rows make the snapshot non-authoritative. A non-authoritative snapshot is skipped; it cannot manufacture locked achievements or replace prior cached data.

This also prevents an empty AppID directory or a `playtime.txt`-only setup from shadowing the normal Steam web provider.

## Metadata and icons

The provider supports:

- plain or multilingual `displayName` and `description` values, using English first;
- `icon` for the unlocked image;
- `icon_gray` and legacy `icongray` for the locked image;
- `hidden` as a boolean or numeric value;
- `earned` and Unix `earned_time` runtime state.

Icon paths are resolved relative to `steam_settings` and accepted only when the resolved file exists inside the game's expanded install directory. Remote URLs and traversal outside the install directory are ignored.

## Refresh behavior

When a complete local source exists, `GseLocal` is evaluated before the normal Steam web provider. Games without a valid local source continue through the existing provider order unchanged.

The existing Playnite Achievements systems own refresh timing:

- the existing in-game poller can detect runtime-file changes while a game is running;
- the existing game-stop handler performs a reliable final single-game refresh;
- normal cache, icon ingestion, unlock diffing, notifications, recordings, and theme bindings remain unchanged.

No standalone achievement watcher needs to remain open.

## Read-only source validation

From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\validate-gse-local-source.ps1 `
  -InstallDirectory "C:\Games\Zero Parades" `
  -AppId 2863680
```

A valid ZERO PARADES source should report 55 schema rows, 55 runtime rows, one or more unlocked rows, zero missing runtime rows, and zero missing icons.

## Focused build and contract validation

Close Playnite, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\validate-external-snapshot.ps1 `
  -Configuration Release
```

This rebuilds the production plugin and runs the focused External Snapshot and GSE local contract suite.

## Isolated Playnite development loop

Close every Playnite process, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\start-gse-local-dev.ps1
```

The script:

1. builds the plugin;
2. refuses to deploy into the real installed `%APPDATA%\Playnite` profile;
3. deploys the complete build output into an isolated profile;
4. rejects duplicate `PlayniteAchievements` extension IDs in that profile;
5. launches Playnite Desktop with `--userdatadir`, `--nolibupdate`, and `--hidesplashscreen`.

Playnite plugins cannot be hot-reloaded. Exit the isolated Playnite instance before rebuilding and run the launcher again for each new DLL.

The development profile's extension log is:

```text
<isolated profile>\extensions.log
```

## ZERO PARADES reference result

The proven reference source uses AppID `2863680`. Its runtime file contained all 55 achievement rows and a real unlocked `ACH_CONDITIONING` entry with Unix timestamp `1785473923`. The manual importer displayed `1 / 55`; this provider consumes that same source directly and resolves its local schema icons.
