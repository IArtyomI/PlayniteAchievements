using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.GseLocal
{
    internal sealed class GseLocalSourceReader
    {
        private const long MaximumSchemaBytes = 25L * 1024L * 1024L;
        private const long MaximumRuntimeBytes = 10L * 1024L * 1024L;
        private const int MaximumAchievements = 10000;
        private const int MaximumDirectoriesScanned = 512;
        private const int MaximumDirectoryDepth = 6;
        private const int MaximumAppIdConfigFiles = 16;
        private const long MaximumAppIdConfigBytes = 64L * 1024L;

        private static readonly string[] AppIdConfigurationFileNames =
        {
            "steam_appid.txt",
            "steam_appid.ini",
            "steam_appid.cfg",
            "configs.app",
            "configs.app.ini",
            "settings.app",
            "steam_settings.ini",
            "steam_settings.cfg"
        };

        private static readonly Regex AppIdConfigurationPattern = new Regex(
            @"^\s*[""']?(?:steam[_-]?)?app[_-]?id(?:64)?[""']?\s*[:=]\s*[""']?(?<appid>\d{1,10})[""']?\s*(?:[#;].*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private static readonly DateTime UnixEpochUtc =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public bool TryLocate(
            string installDirectory,
            string applicationDataDirectory,
            string preferredAppId,
            out GseLocalSourceLocation location)
        {
            location = null;
            if (!TryNormalizeDirectory(installDirectory, out var normalizedInstallDirectory) ||
                !TryNormalizeDirectory(applicationDataDirectory, out var normalizedApplicationDataDirectory))
            {
                return false;
            }

            var normalizedPreferredAppId = NormalizeAppId(preferredAppId);
            foreach (var settingsDirectory in EnumerateSettingsDirectories(normalizedInstallDirectory))
            {
                var schemaPath = Path.Combine(settingsDirectory, "achievements.json");
                if (!File.Exists(schemaPath))
                {
                    continue;
                }

                var settingsAppId = ReadAppId(settingsDirectory, out var ambiguousSettingsAppId);
                if (ambiguousSettingsAppId)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(normalizedPreferredAppId) &&
                    !string.IsNullOrWhiteSpace(settingsAppId) &&
                    !string.Equals(normalizedPreferredAppId, settingsAppId, StringComparison.Ordinal))
                {
                    continue;
                }

                var appId = !string.IsNullOrWhiteSpace(settingsAppId)
                    ? settingsAppId
                    : normalizedPreferredAppId;
                if (string.IsNullOrWhiteSpace(appId))
                {
                    continue;
                }

                var runtimeCandidates = new[]
                {
                    Path.Combine(normalizedApplicationDataDirectory, "GSE Saves", appId),
                    Path.Combine(normalizedApplicationDataDirectory, "Goldberg SteamEmu Saves", appId)
                };

                var runtimeDirectory = SelectRuntimeDirectory(runtimeCandidates);
                var runtimePath = Path.Combine(runtimeDirectory, "achievements.json");

                location = new GseLocalSourceLocation
                {
                    AppId = appId,
                    InstallDirectory = normalizedInstallDirectory,
                    SettingsDirectory = settingsDirectory,
                    SchemaPath = schemaPath,
                    RuntimeDirectory = runtimeDirectory,
                    RuntimePath = runtimePath,
                    RuntimeDirectoryExists = Directory.Exists(runtimeDirectory),
                    RuntimeStateExists = File.Exists(runtimePath)
                };

                return true;
            }

            return false;
        }

        public bool TryRead(
            string installDirectory,
            string applicationDataDirectory,
            string preferredAppId,
            out GseLocalSnapshot snapshot)
        {
            snapshot = null;
            if (!TryLocate(
                    installDirectory,
                    applicationDataDirectory,
                    preferredAppId,
                    out var location))
            {
                return false;
            }

            snapshot = new GseLocalSnapshot
            {
                AppId = location.AppId,
                SchemaPath = location.SchemaPath,
                RuntimePath = location.RuntimePath,
                StateKnown = location.RuntimeStateExists,
                IsCompleteSnapshot = false
            };

            if (!location.RuntimeStateExists)
            {
                snapshot.Diagnostics.Add("The GSE runtime achievement state does not exist yet.");
                snapshot.GeneratedAtUtc = File.GetLastWriteTimeUtc(location.SchemaPath);
                return true;
            }

            JArray schema;
            JObject runtime;
            try
            {
                schema = ReadArray(location.SchemaPath, MaximumSchemaBytes);
                runtime = ReadObject(location.RuntimePath, MaximumRuntimeBytes);
            }
            catch (Exception exception)
            {
                snapshot.Diagnostics.Add(exception.Message);
                return true;
            }

            if (schema.Count == 0 || schema.Count > MaximumAchievements)
            {
                snapshot.Diagnostics.Add("The GSE achievement schema has an unsupported achievement count.");
                return true;
            }

            var runtimeStates = ReadRuntimeStates(runtime, snapshot.Diagnostics);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var complete = true;

            foreach (var token in schema)
            {
                if (!(token is JObject definition))
                {
                    complete = false;
                    snapshot.Diagnostics.Add("The GSE achievement schema contains a non-object entry.");
                    continue;
                }

                var achievementId = ReadString(definition["name"]);
                if (string.IsNullOrWhiteSpace(achievementId) || !seenIds.Add(achievementId))
                {
                    complete = false;
                    snapshot.Diagnostics.Add("The GSE achievement schema contains an empty or duplicate API name.");
                    continue;
                }

                if (!runtimeStates.TryGetValue(achievementId, out var state))
                {
                    complete = false;
                    snapshot.Diagnostics.Add($"The GSE runtime state is missing '{achievementId}'.");
                    continue;
                }

                var displayName = ReadLocalizedString(definition["displayName"]);
                var description = ReadLocalizedString(definition["description"]);
                var unlockedIcon = ResolveTrustedIconPath(
                    ReadString(definition["icon"]),
                    location.SettingsDirectory,
                    location.InstallDirectory);
                var lockedIcon = ResolveTrustedIconPath(
                    ReadString(definition["icon_gray"]),
                    location.SettingsDirectory,
                    location.InstallDirectory);
                if (string.IsNullOrWhiteSpace(lockedIcon))
                {
                    lockedIcon = ResolveTrustedIconPath(
                        ReadString(definition["icongray"]),
                        location.SettingsDirectory,
                        location.InstallDirectory);
                }

                snapshot.Achievements.Add(new GseLocalAchievement
                {
                    AchievementId = achievementId,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? achievementId : displayName,
                    Description = description ?? string.Empty,
                    UnlockedIconPath = unlockedIcon,
                    LockedIconPath = lockedIcon,
                    IsHidden = ReadBoolean(definition["hidden"]),
                    IsUnlocked = state.Earned,
                    UnlockTimeUtc = state.Earned ? ConvertUnixSeconds(state.EarnedTime) : null
                });
            }

            snapshot.StateKnown = true;
            snapshot.IsCompleteSnapshot = complete &&
                snapshot.Achievements.Count == schema.Count;
            snapshot.GeneratedAtUtc = MaxUtc(
                File.GetLastWriteTimeUtc(location.SchemaPath),
                File.GetLastWriteTimeUtc(location.RuntimePath));

            return true;
        }

        private static Dictionary<string, RuntimeAchievementState> ReadRuntimeStates(
            JObject runtime,
            ICollection<string> diagnostics)
        {
            var result = new Dictionary<string, RuntimeAchievementState>(StringComparer.OrdinalIgnoreCase);
            if (runtime == null)
            {
                return result;
            }

            if (runtime.Properties().Take(MaximumAchievements + 1).Count() > MaximumAchievements)
            {
                diagnostics?.Add("The GSE runtime state exceeded the achievement limit.");
                return result;
            }

            foreach (var property in runtime.Properties())
            {
                if (!(property.Value is JObject stateObject) ||
                    string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                result[property.Name] = new RuntimeAchievementState
                {
                    Earned = ReadBoolean(stateObject["earned"]),
                    EarnedTime = ReadInt64(stateObject["earned_time"])
                };
            }

            return result;
        }

        private static IEnumerable<string> EnumerateSettingsDirectories(string installDirectory)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddSettingsDirectory(results, Path.Combine(installDirectory, "steam_settings"));
            AddSettingsDirectory(results, Path.Combine(installDirectory, "Plugins", "x86_64", "steam_settings"));

            try
            {
                foreach (var dataDirectory in Directory.EnumerateDirectories(installDirectory, "*_Data", SearchOption.TopDirectoryOnly))
                {
                    AddSettingsDirectory(
                        results,
                        Path.Combine(dataDirectory, "Plugins", "x86_64", "steam_settings"));
                }
            }
            catch
            {
            }

            var queue = new Queue<DirectoryScanItem>();
            queue.Enqueue(new DirectoryScanItem(installDirectory, 0));
            var scanned = 0;

            while (queue.Count > 0 && scanned < MaximumDirectoriesScanned)
            {
                var item = queue.Dequeue();
                scanned++;

                string[] children;
                try
                {
                    children = Directory.GetDirectories(item.Path);
                }
                catch
                {
                    continue;
                }

                foreach (var child in children.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    DirectoryInfo info;
                    try
                    {
                        info = new DirectoryInfo(child);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.Equals(info.Name, "steam_settings", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSettingsDirectory(results, info.FullName);
                        continue;
                    }

                    if (item.Depth < MaximumDirectoryDepth)
                    {
                        queue.Enqueue(new DirectoryScanItem(info.FullName, item.Depth + 1));
                    }
                }
            }

            return results.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddSettingsDirectory(ISet<string> results, string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath) &&
                    File.Exists(Path.Combine(fullPath, "achievements.json")))
                {
                    results.Add(fullPath);
                }
            }
            catch
            {
            }
        }

        private static string SelectRuntimeDirectory(IEnumerable<string> candidates)
        {
            var normalized = (candidates ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToList();

            var withState = normalized
                .Where(path => File.Exists(Path.Combine(path, "achievements.json")))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "achievements.json")))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(withState))
            {
                return withState;
            }

            var existing = normalized
                .Where(Directory.Exists)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return !string.IsNullOrWhiteSpace(existing)
                ? existing
                : normalized.First();
        }

        private static string ReadAppId(string settingsDirectory, out bool ambiguous)
        {
            ambiguous = false;
            var appIds = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                foreach (var fileName in AppIdConfigurationFileNames.Take(MaximumAppIdConfigFiles))
                {
                    var path = Path.Combine(settingsDirectory, fileName);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var file = new FileInfo(path);
                    if (file.Length > MaximumAppIdConfigBytes)
                    {
                        continue;
                    }

                    var contents = File.ReadAllText(path);
                    if (string.Equals(fileName, "steam_appid.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        var directAppId = NormalizeAppId(contents);
                        if (!string.IsNullOrWhiteSpace(directAppId))
                        {
                            appIds.Add(directAppId);
                        }

                        continue;
                    }

                    foreach (Match match in AppIdConfigurationPattern.Matches(contents))
                    {
                        var configuredAppId = NormalizeAppId(match.Groups["appid"].Value);
                        if (!string.IsNullOrWhiteSpace(configuredAppId))
                        {
                            appIds.Add(configuredAppId);
                        }
                    }
                }

                if (appIds.Count > 1)
                {
                    ambiguous = true;
                    return string.Empty;
                }

                return appIds.FirstOrDefault() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeAppId(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length > 0 && trimmed.All(char.IsDigit)
                ? trimmed
                : string.Empty;
        }

        private static string ResolveTrustedIconPath(
            string configuredPath,
            string settingsDirectory,
            string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            try
            {
                var candidate = Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(
                        settingsDirectory,
                        configuredPath.Replace('/', Path.DirectorySeparatorChar));
                candidate = Path.GetFullPath(candidate);

                return File.Exists(candidate) && IsPathWithin(candidate, installDirectory)
                    ? candidate
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPathWithin(string candidatePath, string rootPath)
        {
            try
            {
                var candidate = Path.GetFullPath(candidatePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var root = Path.GetFullPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static JArray ReadArray(string path, long maximumBytes)
        {
            var token = ReadJson(path, maximumBytes);
            if (!(token is JArray array))
            {
                throw new InvalidDataException("The GSE achievement schema root must be an array.");
            }

            return array;
        }

        private static JObject ReadObject(string path, long maximumBytes)
        {
            var token = ReadJson(path, maximumBytes);
            if (!(token is JObject obj))
            {
                throw new InvalidDataException("The GSE runtime achievement root must be an object.");
            }

            return obj;
        }

        private static JToken ReadJson(string path, long maximumBytes)
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException("The GSE achievement file does not exist.", path);
            }

            if (file.Length > maximumBytes)
            {
                throw new InvalidDataException("The GSE achievement file exceeds the supported size limit.");
            }

            return JToken.Parse(File.ReadAllText(path));
        }

        private static string ReadLocalizedString(JToken token)
        {
            if (token is JValue value && value.Value != null)
            {
                return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            if (!(token is JObject localized))
            {
                return string.Empty;
            }

            var english = ReadString(localized["english"]);
            if (!string.IsNullOrWhiteSpace(english))
            {
                return english;
            }

            foreach (var property in localized.Properties())
            {
                var candidate = ReadString(property.Value);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static string ReadString(JToken token)
        {
            if (!(token is JValue value) || value.Value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static bool ReadBoolean(JToken token)
        {
            if (!(token is JValue value) || value.Value == null)
            {
                return false;
            }

            if (value.Value is bool boolean)
            {
                return boolean;
            }

            if (long.TryParse(
                    Convert.ToString(value.Value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var numeric))
            {
                return numeric != 0;
            }

            return bool.TryParse(
                Convert.ToString(value.Value, CultureInfo.InvariantCulture),
                out var parsed) && parsed;
        }

        private static long ReadInt64(JToken token)
        {
            if (!(token is JValue value) || value.Value == null)
            {
                return 0;
            }

            return long.TryParse(
                Convert.ToString(value.Value, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 0;
        }

        private static DateTime? ConvertUnixSeconds(long seconds)
        {
            if (seconds <= 0)
            {
                return null;
            }

            try
            {
                return UnixEpochUtc.AddSeconds(seconds);
            }
            catch
            {
                return null;
            }
        }

        private static DateTime MaxUtc(DateTime first, DateTime second)
        {
            var firstUtc = first.Kind == DateTimeKind.Utc ? first : first.ToUniversalTime();
            var secondUtc = second.Kind == DateTimeKind.Utc ? second : second.ToUniversalTime();
            return firstUtc >= secondUtc ? firstUtc : secondUtc;
        }

        private static bool TryNormalizeDirectory(string path, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                normalized = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Directory.Exists(normalized);
            }
            catch
            {
                return false;
            }
        }

        private sealed class RuntimeAchievementState
        {
            public bool Earned { get; set; }
            public long EarnedTime { get; set; }
        }

        private sealed class DirectoryScanItem
        {
            public DirectoryScanItem(string path, int depth)
            {
                Path = path;
                Depth = depth;
            }

            public string Path { get; }
            public int Depth { get; }
        }
    }
}
