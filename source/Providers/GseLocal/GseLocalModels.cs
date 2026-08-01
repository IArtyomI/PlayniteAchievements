using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.GseLocal
{
    internal sealed class GseLocalSourceLocation
    {
        public string AppId { get; set; }
        public string InstallDirectory { get; set; }
        public string SettingsDirectory { get; set; }
        public string SchemaPath { get; set; }
        public string RuntimeDirectory { get; set; }
        public string RuntimePath { get; set; }
        public bool RuntimeDirectoryExists { get; set; }
        public bool RuntimeStateExists { get; set; }
    }

    internal sealed class GseLocalSnapshot
    {
        public string AppId { get; set; }
        public string SchemaPath { get; set; }
        public string RuntimePath { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public bool StateKnown { get; set; }
        public bool IsCompleteSnapshot { get; set; }
        public List<GseLocalAchievement> Achievements { get; set; } = new List<GseLocalAchievement>();
        public List<string> Diagnostics { get; set; } = new List<string>();

        public bool IsAuthoritative => StateKnown && IsCompleteSnapshot;
    }

    internal sealed class GseLocalAchievement
    {
        public string AchievementId { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string LockedIconPath { get; set; }
        public string UnlockedIconPath { get; set; }
        public bool IsHidden { get; set; }
        public bool IsUnlocked { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
        public double? GlobalPercentUnlocked { get; set; }
    }

    internal static class GseLocalRefreshPolicy
    {
        public static List<GseLocalAchievement> GetValidAchievements(GseLocalSnapshot snapshot)
        {
            return (snapshot?.Achievements ?? new List<GseLocalAchievement>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.AchievementId))
                .ToList();
        }

        // Cache recency is the import time. Per-achievement unlock time stays separate.
        public static DateTime ResolveRefreshUtc(DateTime refreshedAtUtc)
        {
            if (refreshedAtUtc == default(DateTime))
            {
                return DateTime.UtcNow;
            }

            return refreshedAtUtc.Kind == DateTimeKind.Utc
                ? refreshedAtUtc
                : refreshedAtUtc.ToUniversalTime();
        }
    }
}
