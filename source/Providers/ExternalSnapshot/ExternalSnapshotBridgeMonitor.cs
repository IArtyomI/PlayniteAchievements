using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.ExternalSnapshot
{
    internal sealed class ExternalSnapshotBridgeMonitor : IDisposable
    {
        private readonly string extensionsDataRoot;
        private readonly Func<Guid, Task> refreshGameAsync;
        private readonly Func<Guid, GameAchievementData> loadCachedGame;
        private readonly Func<Guid, Game> loadPlayniteGame;
        private readonly Action<AchievementUnlockedEventArgs> notifyUnlocked;
        private readonly Func<Guid, bool> deferToInGamePolling;
        private readonly ILogger logger;
        private readonly ExternalSnapshotCatalogReader reader;
        private readonly AchievementUnlockDiffer differ = new AchievementUnlockDiffer();
        private readonly object gate = new object();
        private readonly Dictionary<Guid, string> signatures = new Dictionary<Guid, string>();
        private readonly HashSet<Guid> activeGames = new HashSet<Guid>();
        private readonly HashSet<Guid> pendingGames = new HashSet<Guid>();
        private Timer timer;
        private bool disposed;
        private int scanRunning;

        public ExternalSnapshotBridgeMonitor(
            string extensionsDataRoot,
            Func<Guid, Task> refreshGameAsync,
            Func<Guid, GameAchievementData> loadCachedGame,
            Func<Guid, Game> loadPlayniteGame,
            Action<AchievementUnlockedEventArgs> notifyUnlocked,
            ILogger logger,
            Func<Guid, bool> deferToInGamePolling = null,
            ExternalSnapshotCatalogReader reader = null)
        {
            this.extensionsDataRoot = extensionsDataRoot ?? string.Empty;
            this.refreshGameAsync = refreshGameAsync ?? throw new ArgumentNullException(nameof(refreshGameAsync));
            this.loadCachedGame = loadCachedGame ?? throw new ArgumentNullException(nameof(loadCachedGame));
            this.loadPlayniteGame = loadPlayniteGame ?? throw new ArgumentNullException(nameof(loadPlayniteGame));
            this.notifyUnlocked = notifyUnlocked ?? throw new ArgumentNullException(nameof(notifyUnlocked));
            this.deferToInGamePolling = deferToInGamePolling;
            this.logger = logger;
            this.reader = reader ?? new ExternalSnapshotCatalogReader();
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed || timer != null)
                {
                    return;
                }

                timer = new Timer(_ => _ = ScanAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            }

            logger?.Info("[ExternalSnapshot] Bridge monitor started.");
        }

        public Task ScanAsync()
        {
            if (disposed || Interlocked.Exchange(ref scanRunning, 1) != 0)
            {
                return Task.CompletedTask;
            }

            try
            {
                var catalog = reader.Load(extensionsDataRoot);
                var authoritativeIds = new HashSet<Guid>();
                foreach (var pair in catalog.Snapshots)
                {
                    var snapshot = pair.Value;
                    if (snapshot == null || !snapshot.IsAuthoritative)
                    {
                        continue;
                    }

                    authoritativeIds.Add(pair.Key);
                    if (deferToInGamePolling?.Invoke(pair.Key) == true)
                    {
                        continue;
                    }

                    var signature = BuildSignature(snapshot);
                    var changed = false;
                    lock (gate)
                    {
                        if (!signatures.TryGetValue(pair.Key, out var previous) ||
                            !string.Equals(previous, signature, StringComparison.Ordinal))
                        {
                            if (activeGames.Add(pair.Key))
                            {
                                signatures[pair.Key] = signature;
                                changed = true;
                            }
                            else
                            {
                                pendingGames.Add(pair.Key);
                            }
                        }
                    }

                    if (changed)
                    {
                        _ = ProcessChangeAsync(snapshot);
                    }
                }

                lock (gate)
                {
                    foreach (var missing in signatures.Keys.Where(id => !authoritativeIds.Contains(id)).ToList())
                    {
                        signatures.Remove(missing);
                    }
                }

                foreach (var diagnostic in catalog.Diagnostics)
                {
                    logger?.Warn("[ExternalSnapshot] Bridge monitor rejected input: " + diagnostic);
                }
            }
            catch (Exception exception)
            {
                logger?.Warn(exception, "[ExternalSnapshot] Bridge monitor scan failed.");
            }
            finally
            {
                Interlocked.Exchange(ref scanRunning, 0);
            }

            return Task.CompletedTask;
        }

        private async Task ProcessChangeAsync(ExternalSnapshotDocument snapshot)
        {
            var gameId = snapshot.PlayniteGameId;
            try
            {
                var before = loadCachedGame(gameId);
                logger?.Info(
                    $"[ExternalSnapshot] Bridge change selected game={gameId:D} producer={snapshot.ProducerId} timestamp={snapshot.GeneratedAtUtc:o}.");
                await refreshGameAsync(gameId).ConfigureAwait(false);
                var after = loadCachedGame(gameId);
                if (after == null ||
                    !string.Equals(after.ProviderKey, ExternalSnapshotDataProvider.Key, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.Warn($"[ExternalSnapshot] Single-game bridge refresh produced no ExternalSnapshot cache result for {gameId:D}.");
                    return;
                }

                if (before == null)
                {
                    logger?.Info($"[ExternalSnapshot] Initial authoritative baseline stored for {gameId:D}; unlock notifications suppressed.");
                    return;
                }

                var game = loadPlayniteGame(gameId);
                var newUnlockKeys = new HashSet<string>(
                    ExternalSnapshotUnlockPolicy.SelectNewUnlockKeys(
                        (before.Achievements ?? new List<AchievementDetail>())
                            .Select(item => new KeyValuePair<string, bool>(
                                AchievementUnlockDiffer.GetAchievementKey(item?.ApiName, item?.DisplayName),
                                item?.Unlocked == true)),
                        (after.Achievements ?? new List<AchievementDetail>())
                            .Select(item => new KeyValuePair<string, bool>(
                                AchievementUnlockDiffer.GetAchievementKey(item?.ApiName, item?.DisplayName),
                                item?.Unlocked == true)),
                        hasBaseline: true),
                    StringComparer.OrdinalIgnoreCase);
                var unlocks = differ.DiffUserUnlocks(before, after)
                    .Where(item => newUnlockKeys.Contains(
                        AchievementUnlockDiffer.GetAchievementKey(item.ApiName, item.DisplayName)))
                    .ToList();
                foreach (var achievement in unlocks)
                {
                    notifyUnlocked(CreateEvent(game, after, achievement));
                }

                logger?.Info($"[ExternalSnapshot] Single-game bridge refresh completed game={gameId:D} newUnlocks={unlocks.Count}.");
            }
            catch (Exception exception)
            {
                logger?.Error(exception, $"[ExternalSnapshot] Bridge refresh failed for {gameId:D}.");
            }
            finally
            {
                lock (gate)
                {
                    activeGames.Remove(gameId);
                    if (pendingGames.Remove(gameId))
                    {
                        signatures.Remove(gameId);
                    }
                }
            }
        }

        private AchievementUnlockedEventArgs CreateEvent(
            Game game,
            GameAchievementData data,
            AchievementDetail achievement)
        {
            return new AchievementUnlockedEventArgs
            {
                PlayniteGameId = data?.PlayniteGameId ?? game?.Id ?? Guid.Empty,
                GameName = data?.GameName ?? game?.Name,
                GameIconPath = ResolveGameArt(game?.Icon),
                GameCoverPath = ResolveGameArt(game?.CoverImage),
                ProviderKey = ExternalSnapshotDataProvider.Key,
                ApiName = achievement?.ApiName,
                DisplayName = achievement?.DisplayName,
                Description = achievement?.Description,
                Category = achievement?.Category,
                IconPath = achievement?.UnlockedIconPath,
                GlobalPercent = achievement?.GlobalPercentUnlocked,
                RarityTier = achievement?.Rarity.ToString(),
                TrophyType = achievement?.TrophyType,
                IsCapstone = achievement?.IsCapstone == true,
                Points = achievement?.Points,
                ScaledPoints = achievement?.ScaledPoints,
                UnlockTimeUtc = achievement?.UnlockTimeUtc,
                UnlockedCount = data?.UnlockedCount ?? 0,
                TotalCount = data?.AchievementCount ?? 0,
                IsCompletionAchievement = data?.IsCompleted == true
            };
        }

        private string ResolveGameArt(string databasePath)
        {
            try
            {
                return string.IsNullOrWhiteSpace(databasePath)
                    ? null
                    : PlayniteAchievementsPlugin.Instance?.PlayniteApi?.Database?.GetFullFilePath(databasePath);
            }
            catch
            {
                return null;
            }
        }

        private static string BuildSignature(ExternalSnapshotDocument snapshot)
        {
            return string.Join(
                "|",
                snapshot.GeneratedAtUtc.ToUniversalTime().Ticks,
                snapshot.ProducerId ?? string.Empty,
                snapshot.SnapshotPath ?? string.Empty);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                timer?.Dispose();
                timer = null;
                signatures.Clear();
                activeGames.Clear();
                pendingGames.Clear();
            }

            logger?.Info("[ExternalSnapshot] Bridge monitor stopped.");
        }
    }
}
