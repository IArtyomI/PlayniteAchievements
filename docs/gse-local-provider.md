# Native GSE / Goldberg provider

The addon reads local GSE/Goldberg state and uses Steam only for official
metadata, rarity, hidden flags, and icons. GSE remains authoritative for earned
state and `earned_time`.

## Installation

1. Close Playnite completely.
2. From this repository, run `scripts\prepare-private-rc-install.ps1 -Apply`.
   Follow the exact final action printed by the script: install the displayed
   `.pext`, then launch normal Playnite.
3. Run `scripts\verify-private-rc.ps1 -GameInstallPath <ZERO-PARADES-install-directory>` after Playnite starts.

The procedure preserves `ExtensionsData` and the achievement database.

## Update

1. Close Playnite completely.
2. Run `scripts\prepare-private-rc-install.ps1 -Apply` for the new `.pext`,
   follow its printed installation action, and launch normal Playnite.
3. Run `scripts\verify-private-rc.ps1 -GameInstallPath <ZERO-PARADES-install-directory>`.
