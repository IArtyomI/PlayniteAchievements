# External snapshot provider

## Purpose

The external snapshot provider consumes achievement snapshots published by independent Playnite extensions through a versioned, read-only file boundary.

It does not reference another extension's assembly, replace the Playnite Achievements extension identity, or write to another extension's data directory.

## Discovery layout

The provider scans immediate children of Playnite's `ExtensionsData` directory for:

```text
<producer extension data root>\bridge\v1\index.json
<producer extension data root>\snapshots\v1\<playnite game id>.json
```

The index path is fixed. Snapshot paths in the index must be relative JSON paths contained by the producer's own extension-data root.

## Safety rules

The reader rejects:

- rooted or empty snapshot paths;
- path traversal outside the producer root;
- non-JSON snapshot targets;
- missing files;
- unsupported format identifiers or schema versions;
- snapshot/index Playnite game-ID mismatches;
- oversized index or snapshot files;
- excessive entry or achievement counts.

Remote icon URLs are ignored. Local icon files are accepted only when they exist under the producer extension-data root or the matching game's expanded installation directory.

## State semantics

The provider is intentionally conservative:

- `StateKnown = false`: the snapshot is discoverable but is not eligible to refresh or replace cached data.
- `StateKnown = true`, `IsCompleteSnapshot = false`: the snapshot is not authoritative and is not eligible to replace cached data.
- `StateKnown = true`, `IsCompleteSnapshot = true`: the snapshot may be mapped to a normal `GameAchievementData` payload.

This prevents unknown or partial snapshots from manufacturing locked achievements, clearing existing unlocks, or replacing a more complete provider result.

## Provider selection

The provider participates through the existing `IDataProvider` registry and refresh pipeline. A game is capable only when a valid authoritative snapshot exists for its exact Playnite game ID.

The provider is not placed ahead of native providers. It can service games that do
not resolve to a native provider, or it can be selected through the existing
presence-only per-game provider override. That override survives normalization and
restart.

## Bridge monitoring

A bounded consumer monitor checks catalogs for changed authoritative entries and
submits only the matching game and only `ExternalSnapshot` to the existing refresh
coordinator. Work is serialized per game and duplicate catalog events are debounced.
Malformed replacements, producer disappearance, and unknown/incomplete snapshots do
not clear prior cache data.

The first authoritative import establishes a baseline and suppresses unlock events.
Later locked-to-unlocked changes raise the existing `AchievementUnlocked` event so the
extension's normal notification, display, recording, and cache policies remain in
control. Unchanged snapshots, rollback, and timestamp-only corrections produce no
unlock event. While a game is running with in-game polling enabled, the bridge monitor
defers to that existing poller to avoid two concurrent unlock-diff paths.

The provider and monitor never write directly to the achievement database.

## Local validation

Run on Windows with Playnite fully closed:

```powershell
.\scripts\validate-external-snapshot.ps1
```

The script:

1. restores and rebuilds `source\PlayniteAchievements.csproj` with MSBuild;
2. runs the twelve self-contained external snapshot contract tests;
3. verifies that `source\bin\Release\PlayniteAchievements.dll` was produced.

The repository's broader test assembly is not used as the feature gate for this prototype. In the inspected upstream checkout, the test project required unrelated compatibility changes to compile and then reported 22 failures across existing localization, custom-data, category, overview, score-card, start-page, and provider tests. Those failures are tracked separately and are not modified by this provider patch.

No GitHub-hosted workflow is required.
