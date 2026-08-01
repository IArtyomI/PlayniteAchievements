# Native GSE / Goldberg provider

The addon reads the local GSE/Goldberg schema and runtime state through the normal Playnite Achievements pipeline. Steam is used only for official metadata, rarity, hidden flags, and icons; GSE remains authoritative for earned state and `earned_time`.

## Installation

1. Close Playnite.
2. Drag the private `.pext` package onto the Playnite window, or install it from `Add-ons > Install from file`.
3. Restart Playnite and refresh ZERO PARADES. The existing `PlayniteAchievements` extension ID and `ExtensionsData` location are preserved for compatibility.

The provider looks for the local schema under the game's `steam_settings` directory and runtime state at `%APPDATA%\GSE Saves\<AppID>\achievements.json` or the equivalent Goldberg path. A complete runtime file is required before GSE can replace the normal Steam source.

## Update

1. Close Playnite.
2. Install the newer private `.pext` over the existing Playnite Achievements installation.
3. Restart Playnite and run a normal refresh.

Do not remove the existing extension data or change the normal Playnite profile. No watcher, cache deletion, provider juggling, or game-file modification is required.
