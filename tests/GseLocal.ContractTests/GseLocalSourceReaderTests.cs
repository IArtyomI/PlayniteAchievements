using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.GseLocal;
using System;
using System.IO;
using System.Linq;

namespace GseLocal.ContractTests
{
    [TestClass]
    public sealed class GseLocalSourceReaderTests
    {
        [TestMethod]
        public void ReadsCurrentGseFormatWithLocalizedMetadataAndIcons()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                fixture.WriteRuntime(includeSecondAchievement: true);

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    fixture.AppId,
                    out var snapshot));

                Assert.IsNotNull(snapshot);
                Assert.IsTrue(snapshot.IsAuthoritative);
                Assert.AreEqual(2, snapshot.Achievements.Count);

                var conditioning = snapshot.Achievements.Single(item => item.AchievementId == "ACH_CONDITIONING");
                Assert.AreEqual("Conditioning", conditioning.DisplayName);
                Assert.AreEqual("Internalise a thought.", conditioning.Description);
                Assert.IsTrue(conditioning.IsUnlocked);
                Assert.AreEqual(
                    new DateTime(2026, 7, 31, 4, 58, 43, DateTimeKind.Utc),
                    conditioning.UnlockTimeUtc);
                Assert.AreEqual(fixture.UnlockedIconPath, conditioning.UnlockedIconPath);
                Assert.AreEqual(fixture.LockedIconPath, conditioning.LockedIconPath);

                var pressure = snapshot.Achievements.Single(item => item.AchievementId == "ACH_UNDER_PRESSURE");
                Assert.AreEqual("Under Pressure", pressure.DisplayName);
                Assert.IsFalse(pressure.IsUnlocked);
                Assert.IsNull(pressure.UnlockTimeUtc);
            }
        }

        [TestMethod]
        public void MaterializesIconsIntoStableGameScopedPngCacheAndRecreatesDeletedCache()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                fixture.WriteRuntime(includeSecondAchievement: true);

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    fixture.AppId,
                    out var snapshot));
                Assert.IsTrue(snapshot.IsAuthoritative);

                var gameId = Guid.NewGuid();
                var materializer = new GseLocalIconMaterializer(fixture.PluginUserDataDirectory);
                var firstResult = materializer.Materialize(gameId, snapshot);

                Assert.AreEqual(2, firstResult.AchievementCount);
                Assert.AreEqual(2, firstResult.UnlockedIconCount);
                Assert.AreEqual(2, firstResult.LockedIconCount);
                Assert.AreEqual(0, firstResult.Diagnostics.Count);

                var expectedDirectory = Path.Combine(
                    fixture.PluginUserDataDirectory,
                    "icon_cache",
                    gameId.ToString("D"),
                    "gse-local");
                Assert.IsTrue(Directory.Exists(expectedDirectory));

                foreach (var achievement in snapshot.Achievements)
                {
                    AssertMaterializedPng(expectedDirectory, achievement.UnlockedIconPath);
                    AssertMaterializedPng(expectedDirectory, achievement.LockedIconPath);
                }

                Directory.Delete(expectedDirectory, recursive: true);
                Assert.IsFalse(Directory.Exists(expectedDirectory));

                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    fixture.AppId,
                    out var freshSnapshot));
                var secondResult = materializer.Materialize(gameId, freshSnapshot);

                Assert.AreEqual(2, secondResult.UnlockedIconCount);
                Assert.AreEqual(2, secondResult.LockedIconCount);
                Assert.AreEqual(0, secondResult.Diagnostics.Count);
                Assert.IsTrue(Directory.Exists(expectedDirectory));
                Assert.AreEqual(4, Directory.GetFiles(expectedDirectory, "*.png").Length);
            }
        }

        [TestMethod]
        public void LocatesUnitySteamSettingsAndUsesExistingGseRuntimeDirectory()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                Directory.CreateDirectory(fixture.RuntimeDirectory);
                File.WriteAllText(Path.Combine(fixture.RuntimeDirectory, "playtime.txt"), "30");

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryLocate(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    string.Empty,
                    out var location));

                Assert.AreEqual(fixture.AppId, location.AppId);
                Assert.AreEqual(fixture.SettingsDirectory, location.SettingsDirectory);
                Assert.AreEqual(fixture.RuntimeDirectory, location.RuntimeDirectory);
                Assert.IsTrue(location.RuntimeDirectoryExists);
                Assert.IsFalse(location.RuntimeStateExists);
            }
        }

        [TestMethod]
        public void ReadsAppIdFromBoundedSteamSettingsConfigurationWithoutGameOverride()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                File.Delete(Path.Combine(fixture.SettingsDirectory, "steam_appid.txt"));
                File.WriteAllText(
                    Path.Combine(fixture.SettingsDirectory, "configs.app"),
                    "appid=2863680\r\noffline=1\r\n");
                fixture.WriteRuntime(includeSecondAchievement: true);

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    string.Empty,
                    out var snapshot));

                Assert.IsTrue(snapshot.IsAuthoritative);
                Assert.AreEqual(fixture.AppId, snapshot.AppId);
            }
        }

        [TestMethod]
        public void SupportedLegacyGoldbergSaveLayoutIsAuthoritative()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                fixture.WriteRuntime(includeSecondAchievement: true, useLegacyGoldbergLayout: true);

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    string.Empty,
                    out var snapshot));

                Assert.IsTrue(snapshot.IsAuthoritative);
                Assert.AreEqual(2, snapshot.Achievements.Count);
                Assert.IsTrue(snapshot.Achievements.Single(item => item.AchievementId == "ACH_CONDITIONING").IsUnlocked);
            }
        }

        [TestMethod]
        public void UnrelatedGseSaveDoesNotHijackTheMatchingGame()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                fixture.WriteRuntimeForAppId("999999", includeSecondAchievement: true);

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    fixture.AppId,
                    out var snapshot));

                Assert.IsFalse(snapshot.StateKnown);
                Assert.IsFalse(snapshot.IsAuthoritative);
                Assert.IsTrue(snapshot.RuntimePath.EndsWith(
                    Path.Combine("GSE Saves", fixture.AppId, "achievements.json"),
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        [TestMethod]
        public void AmbiguousSteamSettingsAppIdsAreRejected()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                File.WriteAllText(
                    Path.Combine(fixture.SettingsDirectory, "configs.app"),
                    "appid=999999\r\n");

                var reader = new GseLocalSourceReader();

                Assert.IsFalse(reader.TryLocate(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    string.Empty,
                    out _));
            }
        }

        [TestMethod]
        public void GenericNonGseInstallIsNotDetected()
        {
            using (var fixture = GseFixture.Create())
            {
                var reader = new GseLocalSourceReader();

                Assert.IsFalse(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    string.Empty,
                    out _));
            }
        }

        [TestMethod]
        public void MissingRuntimeStateRemainsNonAuthoritative()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                Directory.CreateDirectory(fixture.RuntimeDirectory);

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    fixture.AppId,
                    out var snapshot));

                Assert.IsNotNull(snapshot);
                Assert.IsFalse(snapshot.StateKnown);
                Assert.IsFalse(snapshot.IsCompleteSnapshot);
                Assert.IsFalse(snapshot.IsAuthoritative);
            }
        }

        [TestMethod]
        public void IncompleteRuntimeStateNeverManufacturesLockedAchievements()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);
                fixture.WriteRuntime(includeSecondAchievement: false);

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    fixture.AppId,
                    out var snapshot));

                Assert.IsTrue(snapshot.StateKnown);
                Assert.IsFalse(snapshot.IsCompleteSnapshot);
                Assert.IsFalse(snapshot.IsAuthoritative);
                Assert.AreEqual(1, snapshot.Achievements.Count);
                Assert.IsTrue(snapshot.Diagnostics.Any(message => message.Contains("ACH_UNDER_PRESSURE")));
            }
        }

        [TestMethod]
        public void ConflictingPreferredAppIdIsRejected()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: false);

                var reader = new GseLocalSourceReader();
                Assert.IsFalse(reader.TryLocate(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    "999999",
                    out _));
            }
        }

        [TestMethod]
        public void IconTraversalOutsideInstallDirectoryIsIgnored()
        {
            using (var fixture = GseFixture.Create())
            {
                fixture.WriteSchema(includeTraversalIcon: true);
                fixture.WriteRuntime(includeSecondAchievement: true);

                var outsideIcon = Path.Combine(fixture.RootDirectory, "outside.jpg");
                File.WriteAllBytes(outsideIcon, GseFixture.ValidImageBytes);

                var reader = new GseLocalSourceReader();
                Assert.IsTrue(reader.TryRead(
                    fixture.InstallDirectory,
                    fixture.ApplicationDataDirectory,
                    fixture.AppId,
                    out var snapshot));

                var pressure = snapshot.Achievements.Single(item => item.AchievementId == "ACH_UNDER_PRESSURE");
                Assert.IsNull(pressure.UnlockedIconPath);
            }
        }

        private static void AssertMaterializedPng(string expectedDirectory, string path)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(path));
            Assert.AreEqual(".png", Path.GetExtension(path), true);
            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(
                Path.GetFullPath(path).StartsWith(
                    Path.GetFullPath(expectedDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));

            var signature = File.ReadAllBytes(path).Take(8).ToArray();
            CollectionAssert.AreEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
                signature);
        }

        private sealed class GseFixture : IDisposable
        {
            internal static readonly byte[] ValidImageBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

            private GseFixture(string rootDirectory)
            {
                RootDirectory = rootDirectory;
                InstallDirectory = Path.Combine(rootDirectory, "Zero Parades");
                ApplicationDataDirectory = Path.Combine(rootDirectory, "Roaming");
                PluginUserDataDirectory = Path.Combine(rootDirectory, "PluginData");
                SettingsDirectory = Path.Combine(
                    InstallDirectory,
                    "ZeroParades_Data",
                    "Plugins",
                    "x86_64",
                    "steam_settings");
                RuntimeDirectory = Path.Combine(ApplicationDataDirectory, "GSE Saves", AppId);
                LegacyRuntimeDirectory = Path.Combine(
                    ApplicationDataDirectory,
                    "Goldberg SteamEmu Saves",
                    AppId);
                UnlockedIconPath = Path.Combine(SettingsDirectory, "img", "conditioning.jpg");
                LockedIconPath = Path.Combine(SettingsDirectory, "img", "conditioning_locked.jpg");

                Directory.CreateDirectory(Path.GetDirectoryName(UnlockedIconPath));
                Directory.CreateDirectory(ApplicationDataDirectory);
                Directory.CreateDirectory(PluginUserDataDirectory);
                File.WriteAllText(Path.Combine(SettingsDirectory, "steam_appid.txt"), AppId);
                File.WriteAllBytes(UnlockedIconPath, ValidImageBytes);
                File.WriteAllBytes(LockedIconPath, ValidImageBytes);
            }

            public string AppId => "2863680";
            public string RootDirectory { get; }
            public string InstallDirectory { get; }
            public string ApplicationDataDirectory { get; }
            public string PluginUserDataDirectory { get; }
            public string SettingsDirectory { get; }
            public string RuntimeDirectory { get; }
            public string LegacyRuntimeDirectory { get; }
            public string UnlockedIconPath { get; }
            public string LockedIconPath { get; }

            public static GseFixture Create()
            {
                return new GseFixture(Path.Combine(
                    Path.GetTempPath(),
                    "PlayniteAchievements-GseLocal-" + Guid.NewGuid().ToString("N")));
            }

            public void WriteSchema(bool includeTraversalIcon)
            {
                var schema = new JArray
                {
                    new JObject
                    {
                        ["name"] = "ACH_CONDITIONING",
                        ["displayName"] = new JObject
                        {
                            ["english"] = "Conditioning",
                            ["german"] = "Konditionierung"
                        },
                        ["description"] = new JObject
                        {
                            ["english"] = "Internalise a thought."
                        },
                        ["hidden"] = 0,
                        ["icon"] = "img/conditioning.jpg",
                        ["icon_gray"] = "img/conditioning_locked.jpg"
                    },
                    new JObject
                    {
                        ["name"] = "ACH_UNDER_PRESSURE",
                        ["displayName"] = "Under Pressure",
                        ["description"] = "Lose a skill point.",
                        ["hidden"] = false,
                        ["icon"] = includeTraversalIcon
                            ? "../../../../../outside.jpg"
                            : "img/conditioning.jpg",
                        ["icongray"] = "img/conditioning_locked.jpg"
                    }
                };

                File.WriteAllText(
                    Path.Combine(SettingsDirectory, "achievements.json"),
                    schema.ToString());
            }

            public void WriteRuntime(bool includeSecondAchievement, bool useLegacyGoldbergLayout = false)
            {
                WriteRuntimeForDirectory(
                    useLegacyGoldbergLayout ? LegacyRuntimeDirectory : RuntimeDirectory,
                    includeSecondAchievement);
            }

            public void WriteRuntimeForAppId(string appId, bool includeSecondAchievement)
            {
                WriteRuntimeForDirectory(
                    Path.Combine(ApplicationDataDirectory, "GSE Saves", appId),
                    includeSecondAchievement);
            }

            private void WriteRuntimeForDirectory(string runtimeDirectory, bool includeSecondAchievement)
            {
                Directory.CreateDirectory(runtimeDirectory);
                var runtime = new JObject
                {
                    ["ACH_CONDITIONING"] = new JObject
                    {
                        ["earned"] = true,
                        ["earned_time"] = 1785473923L
                    }
                };

                if (includeSecondAchievement)
                {
                    runtime["ACH_UNDER_PRESSURE"] = new JObject
                    {
                        ["earned"] = false,
                        ["earned_time"] = 0
                    };
                }

                File.WriteAllText(
                    Path.Combine(runtimeDirectory, "achievements.json"),
                    runtime.ToString());
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(RootDirectory))
                    {
                        Directory.Delete(RootDirectory, true);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
