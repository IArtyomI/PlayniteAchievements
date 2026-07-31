using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.GseLocal
{
    internal sealed class GseLocalIconMaterializationResult
    {
        public int AchievementCount { get; set; }
        public int UnlockedIconCount { get; set; }
        public int LockedIconCount { get; set; }
        public List<string> Diagnostics { get; } = new List<string>();
    }

    /// <summary>
    /// Converts provider-owned GSE icon files into stable PNG files under the plugin's
    /// game-scoped cache before the generic achievement icon pipeline runs. This keeps
    /// local icons usable by both native views and theme bindings even when a previous
    /// provider left stale cache files or the downstream cache has been removed.
    /// </summary>
    internal sealed class GseLocalIconMaterializer
    {
        private const long MaximumIconBytes = 10L * 1024L * 1024L;
        private const int MaximumIconDimension = 4096;
        private const long MaximumIconPixels = 16L * 1024L * 1024L;
        private const string ProviderCacheFolderName = "gse-local";

        private readonly string _pluginUserDataPath;

        public GseLocalIconMaterializer(string pluginUserDataPath)
        {
            _pluginUserDataPath = NormalizeDirectory(pluginUserDataPath);
        }

        public GseLocalIconMaterializationResult Materialize(
            Guid playniteGameId,
            GseLocalSnapshot snapshot)
        {
            var result = new GseLocalIconMaterializationResult
            {
                AchievementCount = snapshot?.Achievements?.Count ?? 0
            };

            if (playniteGameId == Guid.Empty ||
                snapshot?.Achievements == null ||
                snapshot.Achievements.Count == 0 ||
                string.IsNullOrWhiteSpace(_pluginUserDataPath))
            {
                result.Diagnostics.Add("The GSE icon cache could not be initialized.");
                return result;
            }

            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                snapshot.Achievements.Select(achievement => achievement?.AchievementId));
            var gameId = playniteGameId.ToString("D");
            var providerCacheDirectory = Path.Combine(
                _pluginUserDataPath,
                "icon_cache",
                gameId,
                ProviderCacheFolderName);

            foreach (var achievement in snapshot.Achievements)
            {
                if (achievement == null ||
                    string.IsNullOrWhiteSpace(achievement.AchievementId) ||
                    !fileStems.TryGetValue(achievement.AchievementId.Trim(), out var fileStem) ||
                    string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                var unlockedPath = MaterializeVariant(
                    achievement.UnlockedIconPath,
                    providerCacheDirectory,
                    fileStem + ".png",
                    achievement.AchievementId,
                    "unlocked",
                    result.Diagnostics);
                if (!string.IsNullOrWhiteSpace(unlockedPath))
                {
                    achievement.UnlockedIconPath = unlockedPath;
                    result.UnlockedIconCount++;
                }

                var lockedPath = MaterializeVariant(
                    achievement.LockedIconPath,
                    providerCacheDirectory,
                    fileStem + ".locked.png",
                    achievement.AchievementId,
                    "locked",
                    result.Diagnostics);
                if (!string.IsNullOrWhiteSpace(lockedPath))
                {
                    achievement.LockedIconPath = lockedPath;
                    result.LockedIconCount++;
                }
            }

            return result;
        }

        private static string MaterializeVariant(
            string sourcePath,
            string targetDirectory,
            string targetFileName,
            string achievementId,
            string variant,
            ICollection<string> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return null;
            }

            string normalizedSourcePath;
            try
            {
                normalizedSourcePath = Path.GetFullPath(sourcePath);
            }
            catch (Exception exception)
            {
                diagnostics?.Add($"{achievementId} {variant} icon path is invalid: {exception.Message}");
                return null;
            }

            FileInfo sourceFile;
            try
            {
                sourceFile = new FileInfo(normalizedSourcePath);
                if (!sourceFile.Exists || sourceFile.Length <= 0 || sourceFile.Length > MaximumIconBytes)
                {
                    diagnostics?.Add($"{achievementId} {variant} icon is missing, empty, or too large.");
                    return null;
                }
            }
            catch (Exception exception)
            {
                diagnostics?.Add($"{achievementId} {variant} icon could not be inspected: {exception.Message}");
                return null;
            }

            var targetPath = Path.Combine(targetDirectory, targetFileName);
            var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                if (File.Exists(targetPath))
                {
                    var existing = new FileInfo(targetPath);
                    if (existing.Length > 0 && existing.LastWriteTimeUtc == sourceFile.LastWriteTimeUtc)
                    {
                        return targetPath;
                    }
                }

                Directory.CreateDirectory(targetDirectory);

                using (var sourceStream = new FileStream(
                    normalizedSourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var image = Image.FromStream(
                    sourceStream,
                    useEmbeddedColorManagement: false,
                    validateImageData: true))
                {
                    if (image.Width <= 0 ||
                        image.Height <= 0 ||
                        image.Width > MaximumIconDimension ||
                        image.Height > MaximumIconDimension ||
                        (long)image.Width * image.Height > MaximumIconPixels)
                    {
                        diagnostics?.Add($"{achievementId} {variant} icon dimensions are unsupported.");
                        return null;
                    }

                    image.Save(temporaryPath, ImageFormat.Png);
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(temporaryPath, targetPath);
                File.SetLastWriteTimeUtc(targetPath, sourceFile.LastWriteTimeUtc);
                return targetPath;
            }
            catch (Exception exception)
            {
                diagnostics?.Add($"{achievementId} {variant} icon could not be materialized: {exception.Message}");
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }
    }
}
