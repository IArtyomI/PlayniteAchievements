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

namespace PlayniteAchievements.Providers.ExternalSnapshot
{
    public sealed class ExternalSnapshotDataProvider : DataProviderBase<ExternalSnapshotSettings>, IDataProvider, IProviderOverride
    {
        public const string Key = "ExternalSnapshot";

        private const string ProviderDisplayName = "External Snapshot";
        private const string ProviderLocalizationKey = "LOCPlayAch_Provider_ExternalSnapshot";
        private static readonly TimeSpan CatalogCacheDuration = TimeSpan.FromSeconds(2);
        private static readonly ProviderOverrideDescriptor ProviderOverride = ProviderOverrideDescriptor.None();

        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _extensionsDataRoot;
        private readonly ExternalSnapshotCatalogReader _catalogReader = new ExternalSnapshotCatalogReader();
        private readonly object _catalogLock = new object();

        private ExternalSnapshotCatalogLoadResult _catalog = new ExternalSnapshotCatalogLoadResult();
        private DateTime _catalogExpiresUtc = DateTime.MinValue;

        public ExternalSnapshotDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _extensionsDataRoot = ResolveExtensionsDataRoot(playniteApi, pluginUserDataPath);
            EnsureProviderNameResource();
        }

        public string ProviderName => ProviderDisplayName;
        public string ProviderKey => Key;
        public string ProviderIconKey => "ProviderIconManual";
        public string ProviderColorHex => "#9AA0A6";
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

            var catalog = GetCatalog(forceReload: false);
            return catalog.TryGetSnapshot(game.Id, out var snapshot) && snapshot.IsAuthoritative;
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

            var catalog = GetCatalog(forceReload: true);
            var supportedGames = gamesToRefresh
                .Where(game => game != null &&
                               game.Id != Guid.Empty &&
                               catalog.TryGetSnapshot(game.Id, out var snapshot) &&
                               snapshot.IsAuthoritative)
                .ToList();

            _logger.Debug(
                $"[ExternalSnapshot] Authoritative snapshot selection completed: requestedGames={gamesToRefresh.Count} selectedGames={supportedGames.Count}.");

            if (supportedGames.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                supportedGames,
                onGameStarting,
                (game, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!catalog.TryGetSnapshot(game.Id, out var snapshot) || !snapshot.IsAuthoritative)
                    {
                        return Task.FromResult(ProviderRefreshExecutor.ProviderGameResult.Skipped());
                    }

                    var data = ExternalSnapshotMapper.Map(
                        game,
                        snapshot,
                        ExpandInstallDirectory(game),
                        ProviderKey);

                    if (data != null)
                    {
                        _logger.Debug(
                            $"[ExternalSnapshot] Snapshot mapped for game {game.Id:D}: achievements={data.Achievements?.Count ?? 0} producer={snapshot.ProducerId}.");
                    }

                    return Task.FromResult(data == null
                        ? ProviderRefreshExecutor.ProviderGameResult.Skipped()
                        : new ProviderRefreshExecutor.ProviderGameResult { Data = data });
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, exception, consecutiveErrors) =>
                    _logger.Error(exception, $"External snapshot refresh failed for '{game?.Name}' ({game?.Id})."),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        public ProviderSettingsViewBase CreateSettingsView() => null;

        private ExternalSnapshotCatalogLoadResult GetCatalog(bool forceReload)
        {
            lock (_catalogLock)
            {
                if (!forceReload && DateTime.UtcNow < _catalogExpiresUtc)
                {
                    return _catalog;
                }

                _catalog = _catalogReader.Load(_extensionsDataRoot);
                _catalogExpiresUtc = DateTime.UtcNow.Add(CatalogCacheDuration);

                if (forceReload)
                {
                    var authoritativeCount = _catalog.Snapshots.Values.Count(snapshot => snapshot.IsAuthoritative);
                    _logger.Debug(
                        $"[ExternalSnapshot] Catalog discovery completed: snapshots={_catalog.Snapshots.Count} authoritative={authoritativeCount} rejections={_catalog.Diagnostics.Count}.");
                }

                foreach (var diagnostic in _catalog.Diagnostics)
                {
                    _logger.Warn(diagnostic);
                }

                return _catalog;
            }
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

        private static string ResolveExtensionsDataRoot(IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            var configuredRoot = playniteApi?.Paths?.ExtensionsDataPath;
            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                try
                {
                    return Path.GetFullPath(configuredRoot);
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                try
                {
                    var pluginDirectory = new DirectoryInfo(Path.GetFullPath(pluginUserDataPath));
                    if (pluginDirectory.Parent != null)
                    {
                        return pluginDirectory.Parent.FullName;
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }
    }
}
