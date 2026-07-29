using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.ExternalSnapshot
{
    internal static class ExternalSnapshotUnlockPolicy
    {
        public static IReadOnlyCollection<string> SelectNewUnlockKeys(
            IEnumerable<KeyValuePair<string, bool>> before,
            IEnumerable<KeyValuePair<string, bool>> after,
            bool hasBaseline)
        {
            if (!hasBaseline)
            {
                return Array.Empty<string>();
            }

            var previouslyUnlocked = new HashSet<string>(
                (before ?? Enumerable.Empty<KeyValuePair<string, bool>>())
                    .Where(item => item.Value && !string.IsNullOrWhiteSpace(item.Key))
                    .Select(item => item.Key.Trim()),
                StringComparer.OrdinalIgnoreCase);

            return (after ?? Enumerable.Empty<KeyValuePair<string, bool>>())
                .Where(item => item.Value && !string.IsNullOrWhiteSpace(item.Key))
                .Select(item => item.Key.Trim())
                .Where(key => !previouslyUnlocked.Contains(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
