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
    public sealed class GseLocalDataProvider : DataProviderBase<GseLocalSettings>, IDataProvider, IProviderOverride
    {
        public const string Key = "GseLocal";

        private const string ProviderDisplayName = "GSE / Goldberg";
        private const string ProviderLocalizationKey = "LOCPlayAch_Provider_GseLocal";
        private static readonly TimeSpan CapabilityCacheDuration = TimeSpan.FromSeconds(3);
        private static readonly ProviderOverrideDescriptor ProviderOverride = ProviderOverrideDescriptor.None();

        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _applicationDataDirectory;
        private readonly GseLocalSourceReader _reader = new GseLocalSourceReader();
        private readonly object _capabilityLock = new object();
        private readonly Dictionary<Guid, CapabilityCacheEntry> _capabilityCache =
            new Dictionary<Guid, CapabilityCacheEntry>();

        public GseLocalDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _ = pluginUserDataPath;
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

            var capable = TryLocate(game, out var location) &&
                location.RuntimeDirectoryExists;

            lock (_capabilityLock)
            {
                _capabilityCache[game.Id] = new CapabilityCacheEntry
                {
                    IsCapable = capable,
                    ExpiresUtc = DateTime.UtcNow.Add(CapabilityCacheDuration)
                };
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
                (game, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var installDirectory = ExpandInstallDirectory(game);
                    var preferredAppId = NormalizeAppId(game.GameId);

                    if (!_reader.TryRead(
                            installDirectory,
                            _applicationDataDirectory,
                            preferredAppId,
                            out var snapshot) ||
                        snapshot == null)
                    {
                        return Task.FromResult(ProviderRefreshExecutor.ProviderGameResult.Skipped());
                    }

                    foreach (var diagnostic in snapshot.Diagnostics)
                    {
                        _logger.Debug($"[GseLocal] {game.Name}: {diagnostic}");
                    }

                    if (!snapshot.IsAuthoritative)
                    {
                        return Task.FromResult(ProviderRefreshExecutor.ProviderGameResult.Skipped());
                    }

                    var data = Map(game, snapshot);
                    _logger.Debug(
                        $"[GseLocal] Loaded {data.Achievements?.Count ?? 0} achievements for '{game.Name}' (AppID {snapshot.AppId}).");

                    return Task.FromResult(
                        new ProviderRefreshExecutor.ProviderGameResult { Data = data });
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

        private bool TryLocate(Game game, out GseLocalSourceLocation location)
        {
            location = null;
            var installDirectory = ExpandInstallDirectory(game);
            return _reader.TryLocate(
                installDirectory,
                _applicationDataDirectory,
                NormalizeAppId(game?.GameId),
                out location);
        }

        private string ExpandInstallDirectory(Game game)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.InstallDirectory))
            {
                return string.Empty;
            }

            try
            {
                return _playniteApi.ExpandGameVariables(game, game.InstallDirectory);
            }
            catch
            {
                return game.InstallDirectory;
            }
        }

        private static GameAchievementData Map(Game game, GseLocalSnapshot snapshot)
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
                    UnlockTimeUtc = item.IsUnlocked ? item.UnlockTimeUtc : null
                })
                .ToList();

            return new GameAchievementData
            {
                LastUpdatedUtc = snapshot.GeneratedAtUtc == default(DateTime)
                    ? DateTime.UtcNow
                    : snapshot.GeneratedAtUtc,
                ProviderKey = Key,
                LibrarySourceName = game.Source?.Name ?? ProviderDisplayName,
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
