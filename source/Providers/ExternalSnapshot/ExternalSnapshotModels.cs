using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.ExternalSnapshot
{
    internal sealed class ExternalSnapshotDocument
    {
        public string ProducerId { get; set; }
        public string ProducerName { get; set; }
        public string ProducerVersion { get; set; }
        public string ProducerRoot { get; set; }
        public string SnapshotPath { get; set; }
        public Guid PlayniteGameId { get; set; }
        public string PlayniteGameName { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public string SourceKey { get; set; }
        public string SourceGameId { get; set; }
        public bool StateKnown { get; set; }
        public bool IsCompleteSnapshot { get; set; }
        public List<ExternalSnapshotAchievement> Achievements { get; set; } = new List<ExternalSnapshotAchievement>();

        public bool IsAuthoritative => StateKnown && IsCompleteSnapshot;
    }

    internal sealed class ExternalSnapshotAchievement
    {
        public string AchievementId { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string LockedIconPath { get; set; }
        public string UnlockedIconPath { get; set; }
        public bool IsHidden { get; set; }
        public bool IsUnlocked { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
        public int? CurrentProgress { get; set; }
        public int? MaximumProgress { get; set; }
    }

    internal sealed class ExternalSnapshotCatalogLoadResult
    {
        public Dictionary<Guid, ExternalSnapshotDocument> Snapshots { get; } =
            new Dictionary<Guid, ExternalSnapshotDocument>();

        public List<string> Diagnostics { get; } = new List<string>();

        public bool TryGetSnapshot(Guid gameId, out ExternalSnapshotDocument snapshot)
        {
            snapshot = null;
            return gameId != Guid.Empty && Snapshots.TryGetValue(gameId, out snapshot) && snapshot != null;
        }
    }
}
