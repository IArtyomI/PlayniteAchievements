using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.GseLocal;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ExternalSnapshot.ContractTests
{
    [TestClass]
    public class GseSteamSchemaEnricherTests
    {
        [TestMethod]
        public void EnrichesFiftyFiveAchievementsWithoutChangingGseUnlockState()
        {
            var unlockTime = new DateTime(2026, 7, 31, 4, 58, 43, DateTimeKind.Utc);
            var snapshot = new GseLocalSnapshot
            {
                AppId = "2863680",
                StateKnown = true,
                IsCompleteSnapshot = true
            };
            var schema = new SchemaAndPercentages
            {
                Achievements = new List<SchemaAchievement>(),
                GlobalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            };

            for (var index = 0; index < 55; index++)
            {
                var apiName = index == 12 ? "ACH_CONDITIONING" : $"ACH_TEST_{index:00}";
                snapshot.Achievements.Add(new GseLocalAchievement
                {
                    AchievementId = apiName,
                    DisplayName = "Local " + apiName,
                    Description = "Local description",
                    UnlockedIconPath = "local-unlocked.png",
                    LockedIconPath = "local-locked.png",
                    IsHidden = false,
                    IsUnlocked = apiName == "ACH_CONDITIONING",
                    UnlockTimeUtc = apiName == "ACH_CONDITIONING" ? unlockTime : (DateTime?)null
                });

                schema.Achievements.Add(new SchemaAchievement
                {
                    Name = apiName,
                    DisplayName = "Steam " + apiName,
                    Description = "Official Steam description",
                    Icon = $"https://shared.akamai.steamstatic.com/community_assets/images/apps/2863680/{apiName}.png",
                    IconGray = $"https://shared.akamai.steamstatic.com/community_assets/images/apps/2863680/{apiName}_gray.png",
                    Hidden = index % 2
                });
                schema.GlobalPercentages[apiName] = index + 0.5;
            }

            var result = GseSteamSchemaEnricher.Apply(snapshot, schema);

            Assert.AreEqual(55, result.LocalAchievementCount);
            Assert.AreEqual(55, result.SteamSchemaCount);
            Assert.AreEqual(55, result.MatchedAchievementCount);
            Assert.IsTrue(result.Applied);

            var conditioning = snapshot.Achievements.Find(item => item.AchievementId == "ACH_CONDITIONING");
            Assert.IsNotNull(conditioning);
            Assert.IsTrue(conditioning.IsUnlocked);
            Assert.AreEqual(unlockTime, conditioning.UnlockTimeUtc);
            Assert.AreEqual("Steam ACH_CONDITIONING", conditioning.DisplayName);
            Assert.AreEqual("Official Steam description", conditioning.Description);
            StringAssert.StartsWith(conditioning.UnlockedIconPath, "https://shared.akamai.steamstatic.com/");
            StringAssert.StartsWith(conditioning.LockedIconPath, "https://shared.akamai.steamstatic.com/");
            Assert.AreEqual(12.5, conditioning.GlobalPercentUnlocked);

            var locked = snapshot.Achievements[0];
            Assert.IsFalse(locked.IsUnlocked);
            Assert.IsNull(locked.UnlockTimeUtc);
        }

        [TestMethod]
        public void MapsGseStateWithRefreshTimeSoARecentSteamRowCannotRemainSelected()
        {
            var sourceStateTime = new DateTime(2026, 7, 31, 4, 58, 43, DateTimeKind.Utc);
            var refreshTime = new DateTime(2026, 7, 31, 21, 35, 0, DateTimeKind.Utc);
            var snapshot = new GseLocalSnapshot
            {
                AppId = "2863680",
                GeneratedAtUtc = sourceStateTime,
                StateKnown = true,
                IsCompleteSnapshot = true,
                Achievements = new List<GseLocalAchievement>
                {
                    new GseLocalAchievement
                    {
                        AchievementId = "ACH_CONDITIONING",
                        DisplayName = "Conditioning",
                        IsUnlocked = true,
                        UnlockTimeUtc = sourceStateTime
                    },
                    new GseLocalAchievement
                    {
                        AchievementId = "ACH_LOCKED",
                        DisplayName = "Locked",
                        IsUnlocked = false
                    }
                }
            };

            var achievements = GseLocalDataProvider.MapAchievements(snapshot);
            var resolvedRefreshTime = GseLocalDataProvider.ResolveRefreshUtc(refreshTime);

            Assert.AreEqual(refreshTime, resolvedRefreshTime);
            Assert.AreEqual(2, achievements.Count);
            Assert.AreEqual(1, achievements.Count(item => item.Unlocked));

            var conditioning = achievements.Single(item => item.ApiName == "ACH_CONDITIONING");
            Assert.IsTrue(conditioning.Unlocked);
            Assert.AreEqual(sourceStateTime, conditioning.UnlockTimeUtc);
        }

        [TestMethod]
        public void LeavesLocalMetadataAndIconsUntouchedWhenSteamSchemaIsUnavailable()
        {
            var snapshot = new GseLocalSnapshot
            {
                AppId = "2863680",
                StateKnown = true,
                IsCompleteSnapshot = true,
                Achievements = new List<GseLocalAchievement>
                {
                    new GseLocalAchievement
                    {
                        AchievementId = "ACH_CONDITIONING",
                        DisplayName = "Conditioning",
                        Description = "Local description",
                        UnlockedIconPath = @"C:\game\steam_settings\img\conditioning.png",
                        LockedIconPath = @"C:\game\steam_settings\img\conditioning_gray.png",
                        IsUnlocked = true,
                        UnlockTimeUtc = new DateTime(2026, 7, 31, 4, 58, 43, DateTimeKind.Utc)
                    }
                }
            };

            var before = snapshot.Achievements[0];
            var unlockTime = before.UnlockTimeUtc;
            var result = GseSteamSchemaEnricher.Apply(snapshot, null);

            Assert.AreEqual(0, result.MatchedAchievementCount);
            Assert.AreEqual("Conditioning", before.DisplayName);
            Assert.AreEqual("Local description", before.Description);
            Assert.AreEqual(@"C:\game\steam_settings\img\conditioning.png", before.UnlockedIconPath);
            Assert.IsTrue(before.IsUnlocked);
            Assert.AreEqual(unlockTime, before.UnlockTimeUtc);
        }

        [TestMethod]
        public void MatchesByApiNameCaseInsensitivelyAndDoesNotInventRows()
        {
            var snapshot = new GseLocalSnapshot
            {
                Achievements = new List<GseLocalAchievement>
                {
                    new GseLocalAchievement
                    {
                        AchievementId = "ACH_CONDITIONING",
                        IsUnlocked = true
                    }
                }
            };
            var schema = new SchemaAndPercentages
            {
                Achievements = new List<SchemaAchievement>
                {
                    new SchemaAchievement
                    {
                        Name = "ach_conditioning",
                        DisplayName = "Conditioning",
                        Icon = "https://example.test/unlocked.png",
                        IconGray = "https://example.test/locked.png"
                    },
                    new SchemaAchievement
                    {
                        Name = "ACH_STEAM_ONLY",
                        DisplayName = "Steam-only row"
                    }
                },
                GlobalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            };

            var result = GseSteamSchemaEnricher.Apply(snapshot, schema);

            Assert.AreEqual(1, result.MatchedAchievementCount);
            Assert.AreEqual(1, snapshot.Achievements.Count);
            Assert.AreEqual("Conditioning", snapshot.Achievements[0].DisplayName);
            Assert.IsTrue(snapshot.Achievements[0].IsUnlocked);
        }
    }
}
