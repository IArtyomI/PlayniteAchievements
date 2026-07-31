using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GseLocal
{
    internal sealed class GseSteamSchemaClient : IDisposable
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly SteamHttpClient _steamHttpClient;
        private readonly SteamApiClient _steamApiClient;
        private readonly SteamWebApiTokenResolver _tokenResolver;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<int, CacheEntry> _cache = new Dictionary<int, CacheEntry>();

        public GseSteamSchemaClient(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            var sessionManager = new SteamSessionManager(
                playniteApi ?? throw new ArgumentNullException(nameof(playniteApi)),
                logger);

            _steamHttpClient = new SteamHttpClient(
                playniteApi,
                logger,
                sessionManager,
                pluginUserDataPath);
            _steamApiClient = new SteamApiClient(_steamHttpClient.ApiHttpClient, logger);
            _tokenResolver = new SteamWebApiTokenResolver(sessionManager, logger);
            sessionManager.SetClearInMemoryAuthState(_steamHttpClient.ClearInMemoryAuthState);
        }

        public async Task<SchemaAndPercentages> GetSchemaAsync(
            int appId,
            CancellationToken cancel)
        {
            if (appId <= 0)
            {
                return null;
            }

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(appId, out var cached) &&
                    cached.ExpiresUtc > DateTime.UtcNow &&
                    cached.Schema != null)
                {
                    return cached.Schema;
                }
            }

            try
            {
                var tokenResolution = await _tokenResolver.ResolveAsync(cancel).ConfigureAwait(false);
                if (!tokenResolution.IsSuccess)
                {
                    _logger.Debug(
                        $"[GseLocal] Steam schema enrichment unavailable for AppID {appId}: " +
                        "Steam Web API authentication could not be resolved. Local metadata will be used.");
                    return null;
                }

                var language = string.IsNullOrWhiteSpace(_settings.Persisted?.GlobalLanguage)
                    ? "english"
                    : _settings.Persisted.GlobalLanguage.Trim();

                var schema = await _steamApiClient
                    .GetSchemaForGameDetailedAsync(
                        tokenResolution.Token,
                        appId,
                        language,
                        cancel)
                    .ConfigureAwait(false);

                if (schema?.Achievements == null || schema.Achievements.Count == 0)
                {
                    _logger.Debug(
                        $"[GseLocal] Steam returned no achievement schema for AppID {appId}. " +
                        "Local metadata will be used.");
                    return null;
                }

                lock (_cacheLock)
                {
                    _cache[appId] = new CacheEntry
                    {
                        Schema = schema,
                        ExpiresUtc = DateTime.UtcNow.Add(CacheDuration)
                    };
                }

                return schema;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug(
                    ex,
                    $"[GseLocal] Steam schema enrichment failed for AppID {appId}; local metadata will be used.");
                return null;
            }
        }

        public void Dispose()
        {
            _steamHttpClient?.Dispose();
        }

        private sealed class CacheEntry
        {
            public SchemaAndPercentages Schema { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }
    }
}
