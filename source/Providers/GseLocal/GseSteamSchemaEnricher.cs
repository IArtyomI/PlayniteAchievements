using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.GseLocal
{
    internal static class GseSteamSchemaEnricher
    {
        public static GseSteamSchemaMergeResult Apply(
            GseLocalSnapshot snapshot,
            SchemaAndPercentages schema)
        {
            var result = new GseSteamSchemaMergeResult
            {
                LocalAchievementCount = snapshot?.Achievements?.Count ?? 0,
                SteamSchemaCount = schema?.Achievements?.Count ?? 0
            };

            if (snapshot?.Achievements == null ||
                snapshot.Achievements.Count == 0 ||
                schema?.Achievements == null ||
                schema.Achievements.Count == 0)
            {
                return result;
            }

            var steamByApiName = schema.Achievements
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var local in snapshot.Achievements)
            {
                if (local == null ||
                    string.IsNullOrWhiteSpace(local.AchievementId) ||
                    !steamByApiName.TryGetValue(local.AchievementId.Trim(), out var steam))
                {
                    continue;
                }

                // Steam supplies presentation only. IsUnlocked and UnlockTimeUtc remain the
                // exact values read from GSE runtime state and are never touched here.
                if (!string.IsNullOrWhiteSpace(steam.DisplayName))
                {
                    local.DisplayName = steam.DisplayName.Trim();
                }

                if (!string.IsNullOrWhiteSpace(steam.Description))
                {
                    local.Description = steam.Description.Trim();
                }

                if (!string.IsNullOrWhiteSpace(steam.Icon))
                {
                    local.UnlockedIconPath = steam.Icon.Trim();
                }

                if (!string.IsNullOrWhiteSpace(steam.IconGray))
                {
                    local.LockedIconPath = steam.IconGray.Trim();
                }

                local.IsHidden = steam.Hidden == 1;

                if (schema.GlobalPercentages != null &&
                    schema.GlobalPercentages.TryGetValue(steam.Name, out var globalPercent))
                {
                    local.GlobalPercentUnlocked = globalPercent;
                }

                result.MatchedAchievementCount++;
            }

            return result;
        }
    }

    internal sealed class GseSteamSchemaMergeResult
    {
        public int LocalAchievementCount { get; set; }
        public int SteamSchemaCount { get; set; }
        public int MatchedAchievementCount { get; set; }

        public bool Applied => MatchedAchievementCount > 0;
    }
}
