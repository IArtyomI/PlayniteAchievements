using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.ExternalSnapshot
{
    internal sealed class ExternalSnapshotCatalogReader
    {
        internal const string IndexFormat = "playnite-achievement-sources.index";
        internal const string SnapshotFormat = "playnite-achievement-sources.snapshot";
        internal const int SupportedSchemaVersion = 1;

        private const long MaximumIndexBytes = 5L * 1024L * 1024L;
        private const long MaximumSnapshotBytes = 25L * 1024L * 1024L;
        private const int MaximumProducerDirectories = 1024;
        private const int MaximumIndexEntries = 50000;
        private const int MaximumAchievementsPerSnapshot = 10000;

        public ExternalSnapshotCatalogLoadResult Load(string extensionsDataRoot)
        {
            var result = new ExternalSnapshotCatalogLoadResult();
            if (string.IsNullOrWhiteSpace(extensionsDataRoot) || !Directory.Exists(extensionsDataRoot))
            {
                return result;
            }

            IReadOnlyList<string> producerRoots;
            try
            {
                producerRoots = Directory.EnumerateDirectories(extensionsDataRoot)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumProducerDirectories)
                    .ToList();
            }
            catch (Exception exception)
            {
                result.Diagnostics.Add("External snapshot producer directories could not be enumerated: " + exception.Message);
                return result;
            }

            foreach (var producerRoot in producerRoots)
            {
                TryLoadProducer(producerRoot, result);
            }

            return result;
        }

        private static void TryLoadProducer(string producerRoot, ExternalSnapshotCatalogLoadResult result)
        {
            var indexPath = Path.Combine(producerRoot, "bridge", "v1", "index.json");
            if (!File.Exists(indexPath))
            {
                return;
            }

            var producerDirectoryName = new DirectoryInfo(producerRoot).Name;
            JObject index;
            try
            {
                index = ReadObject(indexPath, MaximumIndexBytes);
                ValidateFormat(index, IndexFormat, "index");
            }
            catch (Exception exception)
            {
                result.Diagnostics.Add($"External snapshot producer '{producerDirectoryName}' has an invalid index: {exception.Message}");
                return;
            }

            var producerId = ReadString(index, "ProducerId");
            if (string.IsNullOrWhiteSpace(producerId))
            {
                producerId = producerDirectoryName;
            }

            var producerName = ReadString(index, "ProducerName");
            var producerVersion = ReadString(index, "ProducerVersion");
            var entries = index["Entries"] as JArray;
            if (entries == null)
            {
                return;
            }

            if (entries.Count > MaximumIndexEntries)
            {
                result.Diagnostics.Add($"External snapshot producer '{producerId}' exceeded the entry limit.");
                return;
            }

            foreach (var entryToken in entries)
            {
                if (!(entryToken is JObject entry))
                {
                    result.Diagnostics.Add($"External snapshot producer '{producerId}' contains an empty index entry.");
                    continue;
                }

                TryLoadEntry(producerRoot, producerId, producerName, producerVersion, entry, result);
            }
        }

        private static void TryLoadEntry(
            string producerRoot,
            string producerId,
            string producerName,
            string producerVersion,
            JObject entry,
            ExternalSnapshotCatalogLoadResult result)
        {
            if (!Guid.TryParse(ReadString(entry, "PlayniteGameId"), out var indexedGameId) || indexedGameId == Guid.Empty)
            {
                result.Diagnostics.Add($"External snapshot producer '{producerId}' contains an invalid Playnite game ID.");
                return;
            }

            if (!TryResolveSafeJsonPath(
                    producerRoot,
                    ReadString(entry, "SnapshotRelativePath"),
                    out var snapshotPath,
                    out var pathError))
            {
                result.Diagnostics.Add($"External snapshot producer '{producerId}' contains an unsafe snapshot path: {pathError}");
                return;
            }

            if (!File.Exists(snapshotPath))
            {
                result.Diagnostics.Add($"External snapshot producer '{producerId}' references a missing snapshot for game {indexedGameId:D}.");
                return;
            }

            JObject payload;
            try
            {
                payload = ReadObject(snapshotPath, MaximumSnapshotBytes);
                ValidateFormat(payload, SnapshotFormat, "snapshot");
            }
            catch (Exception exception)
            {
                result.Diagnostics.Add($"External snapshot producer '{producerId}' has an invalid snapshot for game {indexedGameId:D}: {exception.Message}");
                return;
            }

            if (!Guid.TryParse(ReadString(payload, "PlayniteGameId"), out var snapshotGameId) || snapshotGameId != indexedGameId)
            {
                result.Diagnostics.Add($"External snapshot producer '{producerId}' has a snapshot game-ID mismatch.");
                return;
            }

            var generatedAtUtc = ReadUtcDate(payload["GeneratedAtUtc"])
                ?? ReadUtcDate(entry["SnapshotGeneratedAtUtc"])
                ?? File.GetLastWriteTimeUtc(snapshotPath);

            var document = new ExternalSnapshotDocument
            {
                ProducerId = producerId,
                ProducerName = producerName,
                ProducerVersion = producerVersion,
                ProducerRoot = Path.GetFullPath(producerRoot),
                SnapshotPath = snapshotPath,
                PlayniteGameId = snapshotGameId,
                PlayniteGameName = ReadString(payload, "PlayniteGameName"),
                GeneratedAtUtc = EnsureUtc(generatedAtUtc),
                SourceKey = ReadString(payload, "SourceKey"),
                SourceGameId = ReadString(payload, "SourceGameId"),
                StateKnown = ReadBoolean(payload["StateKnown"]),
                IsCompleteSnapshot = ReadBoolean(payload["IsCompleteSnapshot"]),
                Achievements = ReadAchievements(payload["Achievements"], producerId, result)
            };

            if (result.Snapshots.TryGetValue(document.PlayniteGameId, out var existing))
            {
                if (!ShouldReplace(existing, document))
                {
                    return;
                }

                result.Diagnostics.Add($"Multiple external snapshot producers target game {document.PlayniteGameId:D}; the newest valid snapshot was selected.");
            }

            result.Snapshots[document.PlayniteGameId] = document;
        }

        private static List<ExternalSnapshotAchievement> ReadAchievements(
            JToken token,
            string producerId,
            ExternalSnapshotCatalogLoadResult result)
        {
            var achievements = new List<ExternalSnapshotAchievement>();
            if (!(token is JArray array))
            {
                return achievements;
            }

            if (array.Count > MaximumAchievementsPerSnapshot)
            {
                result.Diagnostics.Add($"External snapshot producer '{producerId}' exceeded the achievement limit.");
                return achievements;
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var itemToken in array)
            {
                if (!(itemToken is JObject item))
                {
                    continue;
                }

                var achievementId = ReadString(item, "AchievementId");
                if (string.IsNullOrWhiteSpace(achievementId) || !seenIds.Add(achievementId))
                {
                    continue;
                }

                var displayName = ReadString(item, "DisplayName");
                achievements.Add(new ExternalSnapshotAchievement
                {
                    AchievementId = achievementId,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? achievementId : displayName,
                    Description = ReadString(item, "Description"),
                    LockedIconPath = ReadString(item, "LockedIconPath"),
                    UnlockedIconPath = ReadString(item, "UnlockedIconPath"),
                    IsHidden = ReadBoolean(item["IsHidden"]),
                    IsUnlocked = ReadBoolean(item["IsUnlocked"]),
                    UnlockTimeUtc = ReadUtcDate(item["UnlockTimeUtc"]),
                    CurrentProgress = ReadNullableInt32(item["CurrentProgress"]),
                    MaximumProgress = ReadNullableInt32(item["MaximumProgress"])
                });
            }

            return achievements;
        }

        private static bool ShouldReplace(ExternalSnapshotDocument existing, ExternalSnapshotDocument candidate)
        {
            if (candidate.GeneratedAtUtc != existing.GeneratedAtUtc)
            {
                return candidate.GeneratedAtUtc > existing.GeneratedAtUtc;
            }

            var producerComparison = string.Compare(
                candidate.ProducerId,
                existing.ProducerId,
                StringComparison.OrdinalIgnoreCase);
            if (producerComparison != 0)
            {
                return producerComparison < 0;
            }

            return string.Compare(candidate.SnapshotPath, existing.SnapshotPath, StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static JObject ReadObject(string path, long maximumBytes)
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException("The JSON file does not exist.");
            }

            if (file.Length > maximumBytes)
            {
                throw new InvalidDataException("The JSON file exceeds the supported size limit.");
            }

            var token = JToken.Parse(File.ReadAllText(path));
            if (!(token is JObject obj))
            {
                throw new InvalidDataException("The JSON root must be an object.");
            }

            return obj;
        }

        private static void ValidateFormat(JObject payload, string expectedFormat, string label)
        {
            if (!string.Equals(ReadString(payload, "Format"), expectedFormat, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The {label} format is not supported.");
            }

            var schemaVersion = ReadNullableInt32(payload["SchemaVersion"]) ?? 0;
            if (schemaVersion != SupportedSchemaVersion)
            {
                throw new InvalidDataException($"The {label} schema version {schemaVersion} is not supported.");
            }
        }

        private static bool TryResolveSafeJsonPath(
            string producerRoot,
            string relativePath,
            out string fullPath,
            out string error)
        {
            fullPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                error = "rooted or empty paths are not allowed";
                return false;
            }

            try
            {
                var root = GetRootWithSeparator(producerRoot);
                var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
                var candidate = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    error = "path traversal is not allowed";
                    return false;
                }

                if (!string.Equals(Path.GetExtension(candidate), ".json", StringComparison.OrdinalIgnoreCase))
                {
                    error = "only JSON files are allowed";
                    return false;
                }

                fullPath = candidate;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static string GetRootWithSeparator(string rootPath)
        {
            var root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return root + Path.DirectorySeparatorChar;
        }

        private static string ReadString(JObject payload, string propertyName)
        {
            var token = payload?[propertyName];
            return token == null || token.Type == JTokenType.Null
                ? string.Empty
                : Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static bool ReadBoolean(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            return bool.TryParse(Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture), out var value) && value;
        }

        private static int? ReadNullableInt32(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            try
            {
                var number = Convert.ToDouble(((JValue)token).Value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number) || number < int.MinValue || number > int.MaxValue)
                {
                    return null;
                }

                return Convert.ToInt32(Math.Round(number, MidpointRounding.AwayFromZero));
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ReadUtcDate(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var text = token.Type == JTokenType.Date
                ? token.Value<DateTime>().ToString("o", CultureInfo.InvariantCulture)
                : Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);

            if (!DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var value))
            {
                return null;
            }

            return EnsureUtc(value);
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            return value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
        }
    }
}
