using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.ExternalSnapshot
{
    internal static class ExternalSnapshotMapper
    {
        public static GameAchievementData Map(
            Game game,
            ExternalSnapshotDocument snapshot,
            string expandedInstallDirectory,
            string providerKey)
        {
            if (game == null || snapshot == null || !snapshot.IsAuthoritative || game.Id != snapshot.PlayniteGameId)
            {
                return null;
            }

            var achievements = (snapshot.Achievements ?? new List<ExternalSnapshotAchievement>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.AchievementId))
                .Select(item => new AchievementDetail
                {
                    ApiName = item.AchievementId,
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.AchievementId : item.DisplayName,
                    Description = item.Description ?? string.Empty,
                    UnlockedIconPath = ResolveTrustedIconPath(
                        item.UnlockedIconPath,
                        snapshot,
                        expandedInstallDirectory),
                    LockedIconPath = ResolveTrustedIconPath(
                        item.LockedIconPath,
                        snapshot,
                        expandedInstallDirectory),
                    Hidden = item.IsHidden,
                    Unlocked = item.IsUnlocked,
                    UnlockTimeUtc = item.IsUnlocked ? item.UnlockTimeUtc : null,
                    ProgressNum = item.CurrentProgress,
                    ProgressDenom = item.MaximumProgress
                })
                .ToList();

            return new GameAchievementData
            {
                LastUpdatedUtc = snapshot.GeneratedAtUtc == default(DateTime)
                    ? DateTime.UtcNow
                    : snapshot.GeneratedAtUtc,
                ProviderKey = providerKey,
                LibrarySourceName = game.Source?.Name ?? game.PluginId.ToString(),
                HasAchievements = achievements.Count > 0,
                GameName = string.IsNullOrWhiteSpace(snapshot.PlayniteGameName)
                    ? game.Name
                    : snapshot.PlayniteGameName,
                AppId = int.TryParse(snapshot.SourceGameId, out var appId) ? appId : 0,
                ProviderGameKey = BuildProviderGameKey(snapshot),
                PlayniteGameId = game.Id,
                Game = game,
                Achievements = achievements
            };
        }

        private static string BuildProviderGameKey(ExternalSnapshotDocument snapshot)
        {
            return string.Join(
                ":",
                new[]
                {
                    "external-snapshot",
                    snapshot.ProducerId ?? string.Empty,
                    snapshot.SourceKey ?? string.Empty,
                    snapshot.SourceGameId ?? string.Empty
                });
        }

        private static string ResolveTrustedIconPath(
            string configuredPath,
            ExternalSnapshotDocument snapshot,
            string expandedInstallDirectory)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            try
            {
                string candidate;
                if (Uri.TryCreate(configuredPath, UriKind.Absolute, out var uri) && !uri.IsFile)
                {
                    return null;
                }

                if (Uri.TryCreate(configuredPath, UriKind.Absolute, out uri) && uri.IsFile)
                {
                    candidate = uri.LocalPath;
                }
                else if (Path.IsPathRooted(configuredPath))
                {
                    candidate = configuredPath;
                }
                else
                {
                    candidate = Path.Combine(Path.GetDirectoryName(snapshot.SnapshotPath), configuredPath);
                }

                candidate = Path.GetFullPath(candidate);
                if (!File.Exists(candidate))
                {
                    return null;
                }

                if (IsPathWithin(candidate, snapshot.ProducerRoot) ||
                    IsPathWithin(candidate, expandedInstallDirectory))
                {
                    return candidate;
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool IsPathWithin(string candidatePath, string allowedRoot)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(allowedRoot))
            {
                return false;
            }

            try
            {
                var candidate = Path.GetFullPath(candidatePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var root = Path.GetFullPath(allowedRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
                       candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
