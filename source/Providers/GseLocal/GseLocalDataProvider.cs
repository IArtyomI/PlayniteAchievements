using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PlayniteAchievements.Providers.GseLocal
{
    public sealed class GseLocalDataProvider : DataProviderBase<GseLocalSettings>, IDataProvider, IProviderOverride, IDisposable
    {
        public const string Key = "GseLocal";

        private const string ProviderDisplayName = "GSE / Goldberg";
        private const string ProviderLocalizationKey = "LOCPlayAch_Provider_GseLocal";
        private static readonly TimeSpan CapabilityCacheDuration = TimeSpan.FromSeconds(3);
        private static readonly ProviderOverrideDescriptor ProviderOverride = ProviderOverrideDescriptor.None();

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _applicationDataDirectory;
        private readonly GseLocalSourceReader _reader = new GseLocalSourceReader();
        private readonly GseLocalIconMaterializer _iconMaterializer;
        private readonly GseSteamSchemaClient _steamSchemaClient;
        private readonly object _capabilityLock = new object();
        private readonly Dictionary<Guid, CapabilityCacheEntry> _capabilityCache =
            new Dictionary<Guid, CapabilityCacheEntry>();
        private readonly Dictionary<Guid, string> _lastCapabilityDiagnostics =
            new Dictionary<Guid, string>();

        public GseLocalDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _iconMaterializer = new GseLocalIconMaterializer(pluginUserDataPath);
            _steamSchemaClient = new GseSteamSchemaClient(
                logger,
                settings,
                playniteApi,
                pluginUserDataPath);
            _applicationDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            EnsureProviderNameResource();
        }

        public string ProviderName => ProviderDisplayName;
        public string ProviderKey => Key;
        public string ProviderIconKey => "ProviderIconSteam";
        public string ProviderColorHex => "#66C0F4";
        public bool IsAuthenticated => true;
        public ISessionManager AuthSession => null;
        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;
        public ProviderOverrideDescriptor OverrideDescriptor => ProviderOverride;

        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            lock (_capabilityLock)
            {
                if (_capabilityCache.TryGetValue(game.Id, out var cached) &&
                    cached.ExpiresUtc > DateTime.UtcNow)
                {
                    return cached.IsCapable;
                }
            }

            // Requiring the runtime achievements.json prevents an empty AppID directory or
            // playtime.txt-only setup from shadowing the normal Steam provider. GSE becomes
            // authoritative only after it has published complete earned/locked state.
            var located = TryLocate(
                game,
                out var location,
                out var resolvedInstallDirectory,
                out var installDirectoryCandidates);
            var capable = located && location.RuntimeStateExists;

            var diagnostic = BuildCapabilityDiagnostic(
                game,
                capable,
                location,
                resolvedInstallDirectory,
                installDirectoryCandidates);

            lock (_capabilityLock)
            {
                _capabilityCache[game.Id] = new CapabilityCacheEntry
                {
                    IsCapable = capable,
                    ExpiresUtc = DateTime.UtcNow.Add(CapabilityCacheDuration)
                };

                if (!_lastCapabilityDiagnostics.TryGetValue(game.Id, out var previousDiagnostic) ||
                    !string.Equals(previousDiagnostic, diagnostic, StringComparison.Ordinal))
                {
                    _lastCapabilityDiagnostics[game.Id] = diagnostic;
                    _logger.Debug(diagnostic);
                }
            }

            return capable;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh.Where(game => game != null).ToList(),
                onGameStarting,
                async (game, token) =>
                {
                    token.ThrowIfCancellationRequested();

                    if (!TryLocate(
                            game,
                            out var location,
                            out var installDirectory,
                            out var installDirectoryCandidates))
                    {
                        _logger.Debug(BuildCapabilityDiagnostic(
                            game,
                            capable: false,
                            location: null,
                            resolvedInstallDirectory: null,
                            installDirectoryCandidates));
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var preferredAppId = NormalizeAppId(game.GameId);
                    if (!_reader.TryRead(
                            installDirectory,
                            _applicationDataDirectory,
                            preferredAppId,
                            out var snapshot) ||
                        snapshot == null)
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    foreach (var diagnosticMessage in snapshot.Diagnostics)
                    {
                        _logger.Debug($"[GseLocal] {game.Name}: {diagnosticMessage}");
                    }

                    if (!snapshot.IsAuthoritative)
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    // Preserve local icons first so every row has a stable offline fallback.
                    // Official Steam URLs replace these paths only when the Web API schema
                    // resolves and matches the same achievement API name.
                    var iconResult = _iconMaterializer.Materialize(game.Id, snapshot);
                    foreach (var diagnosticMessage in iconResult.Diagnostics)
                    {
                        _logger.Debug($"[GseLocal] {game.Name}: {diagnosticMessage}");
                    }

                    _logger.Debug(
                        $"[GseLocal] Materialized local fallback icons for '{game.Name}': " +
                        $"achievements={iconResult.AchievementCount}, " +
                        $"unlocked={iconResult.UnlockedIconCount}, locked={iconResult.LockedIconCount}.");

                    if (int.TryParse(snapshot.AppId, out var steamAppId) && steamAppId > 0)
                    {
                        var steamSchema = await _steamSchemaClient
                            .GetSchemaAsync(steamAppId, token)
                            .ConfigureAwait(false);
                        var mergeResult = GseSteamSchemaEnricher.Apply(snapshot, steamSchema);

                        if (mergeResult.Applied)
                        {
                            _logger.Info(
                                $"[GseLocal] Enriched '{game.Name}' from Steam Web API schema: " +
                                $"matched={mergeResult.MatchedAchievementCount}/" +
                                $"{mergeResult.LocalAchievementCount}, appId={steamAppId}. " +
                                "GSE unlock state remained authoritative.");
                        }
                    }

                    var data = Map(game, snapshot);
                    _logger.Info(
                        $"[GseLocal] Loaded {data.AchievementCount} achievements for '{game.Name}' " +
                        $"with unlocked={data.UnlockedCount} " +
                        $"(AppID {snapshot.AppId}, root '{location.InstallDirectory}', " +
                        $"sourceStateUtc={snapshot.GeneratedAtUtc:O}, refreshUtc={data.LastUpdatedUtc:O}).");

                    return new ProviderRefreshExecutor.ProviderGameResult { Data = data };
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, exception, consecutiveErrors) =>
                    _logger.Error(exception, $"GSE local achievement refresh failed for '{game?.Name}' ({game?.Id})."),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        public ProviderSettingsViewBase CreateSettingsView() => null;

        public void Dispose()
        {
            _steamSchemaClient?.Dispose();
        }

        private bool TryLocate(
            Game game,
            out GseLocalSourceLocation location,
            out string resolvedInstallDirectory,
            out IReadOnlyList<string> installDirectoryCandidates)
        {
            location = null;
            resolvedInstallDirectory = null;
            installDirectoryCandidates = ResolveInstallDirectoryCandidates(game);
            var preferredAppId = NormalizeAppId(game?.GameId);

            foreach (var installDirectory in installDirectoryCandidates)
            {
                if (!_reader.TryLocate(
                        installDirectory,
                        _applicationDataDirectory,
                        preferredAppId,
                        out var candidateLocation))
                {
                    continue;
                }

                location = candidateLocation;
                resolvedInstallDirectory = installDirectory;
                return true;
            }

            return false;
        }

        private IReadOnlyList<string> ResolveInstallDirectoryCandidates(Game game)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (game == null)
            {
                return results;
            }

            AddExistingDirectory(results, seen, ExpandGamePath(game, game.InstallDirectory));

            var actions = game.GameActions?
                .Where(action => action != null)
                .OrderByDescending(action => action.IsPlayAction)
                .ToList() ?? new List<GameAction>();

            foreach (var action in actions)
            {
                var workingDirectory = ExpandGamePath(game, action.WorkingDir);
                AddExistingDirectory(results, seen, workingDirectory);

                var trackingPath = ExpandGamePath(game, action.TrackingPath);
                AddPathOrParentDirectory(
                    results,
                    seen,
                    trackingPath,
                    workingDirectory,
                    results.FirstOrDefault());

                var executablePath = ExpandGamePath(game, action.Path);
                AddPathOrParentDirectory(
                    results,
                    seen,
                    executablePath,
                    workingDirectory,
                    results.FirstOrDefault());
            }

            return results;
        }

        private string ExpandGamePath(Game game, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            try
            {
                return _playniteApi.ExpandGameVariables(game, value)?.Trim() ?? string.Empty;
            }
            catch
            {
                return value.Trim();
            }
        }

        private static void AddPathOrParentDirectory(
            ICollection<string> results,
            ISet<string> seen,
            string path,
            params string[] baseDirectories)
        {
            var normalizedPath = TrimOuterQuotes(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return;
            }

            var candidates = new List<string> { normalizedPath };
            if (!Path.IsPathRooted(normalizedPath))
            {
                foreach (var baseDirectory in baseDirectories ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(baseDirectory))
                    {
                        continue;
                    }

                    try
                    {
                        candidates.Add(Path.Combine(baseDirectory, normalizedPath));
                    }
                    catch
                    {
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (Directory.Exists(fullPath))
                    {
                        AddExistingDirectory(results, seen, fullPath);
                        continue;
                    }

                    if (File.Exists(fullPath))
                    {
                        AddExistingDirectory(results, seen, Path.GetDirectoryName(fullPath));
                    }
                }
                catch
                {
                }
            }
        }

        private static void AddExistingDirectory(
            ICollection<string> results,
            ISet<string> seen,
            string path)
        {
            var normalizedPath = TrimOuterQuotes(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(normalizedPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Directory.Exists(fullPath) && seen.Add(fullPath))
                {
                    results.Add(fullPath);
                }
            }
            catch
            {
            }
        }

        private static string TrimOuterQuotes(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length >= 2 &&
                ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"') ||
                 (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')))
            {
                return trimmed.Substring(1, trimmed.Length - 2).Trim();
            }

            return trimmed;
        }

        private static string BuildCapabilityDiagnostic(
            Game game,
            bool capable,
            GseLocalSourceLocation location,
            string resolvedInstallDirectory,
            IReadOnlyList<string> installDirectoryCandidates)
        {
            var gameName = game?.Name ?? "(unknown)";
            var candidates = installDirectoryCandidates == null || installDirectoryCandidates.Count == 0
                ? "(none)"
                : string.Join("; ", installDirectoryCandidates);

            if (capable && location != null)
            {
                return $"[GseLocal] IsCapable for '{gameName}': true " +
                    $"(AppID {location.AppId}, root '{resolvedInstallDirectory}', runtime '{location.RuntimePath}').";
            }

            if (location != null)
            {
                return $"[GseLocal] IsCapable for '{gameName}': false; " +
                    $"steam_settings resolved at '{location.SettingsDirectory}', but runtime state is missing at " +
                    $"'{location.RuntimePath}'. Candidates: {candidates}";
            }

            return $"[GseLocal] IsCapable for '{gameName}': false; no matching steam_settings source was found. " +
                $"Candidates: {candidates}";
        }

        internal static GameAchievementData Map(Game game, GseLocalSnapshot snapshot)
        {
            return Map(game, snapshot, DateTime.UtcNow);
        }

        internal static GameAchievementData Map(
            Game game,
            GseLocalSnapshot snapshot,
            DateTime refreshedAtUtc)
        {
            var achievements = snapshot.Achievements
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.AchievementId))
                .Select(item => new AchievementDetail
                {
                    ApiName = item.AchievementId,
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName)
                        ? item.AchievementId
                        : item.DisplayName,
                    Description = item.Description ?? string.Empty,
                    UnlockedIconPath = item.UnlockedIconPath,
                    LockedIconPath = item.LockedIconPath,
                    Hidden = item.IsHidden,
                    Unlocked = item.IsUnlocked,
                    UnlockTimeUtc = item.IsUnlocked ? item.UnlockTimeUtc : null,
                    GlobalPercentUnlocked = item.GlobalPercentUnlocked,
                    Rarity = item.GlobalPercentUnlocked.HasValue
                        ? PercentRarityHelper.GetRarityTier(item.GlobalPercentUnlocked.Value)
                        : RarityTier.Common
                })
                .ToList();

            var refreshUtc = refreshedAtUtc == default(DateTime)
                ? DateTime.UtcNow
                : refreshedAtUtc.Kind == DateTimeKind.Utc
                    ? refreshedAtUtc
                    : refreshedAtUtc.ToUniversalTime();

            return new GameAchievementData
            {
                // This is cache/import recency, not source-file modification time. Using the
                // runtime file's old timestamp lets a newer Steam row remain selected after a
                // successful GSE refresh, which can incorrectly leave the UI at 0/N.
                LastUpdatedUtc = refreshUtc,
                ProviderKey = Key,
                LibrarySourceName = ProviderDisplayName,
                HasAchievements = achievements.Count > 0,
                GameName = game.Name,
                AppId = int.TryParse(snapshot.AppId, out var appId) ? appId : 0,
                ProviderGameKey = "gse-local:" + (snapshot.AppId ?? string.Empty),
                PlayniteGameId = game.Id,
                Game = game,
                Achievements = achievements
            };
        }

        private static string NormalizeAppId(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length > 0 && trimmed.All(char.IsDigit)
                ? trimmed
                : string.Empty;
        }

        private static void EnsureProviderNameResource()
        {
            try
            {
                var resources = Application.Current?.Resources;
                if (resources != null && !resources.Contains(ProviderLocalizationKey))
                {
                    resources.Add(ProviderLocalizationKey, ProviderDisplayName);
                }
            }
            catch
            {
            }
        }

        private sealed class CapabilityCacheEntry
        {
            public bool IsCapable { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }
    }
}
