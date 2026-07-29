using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.ExternalSnapshot;
using System;
using System.IO;
using System.Linq;

namespace ExternalSnapshot.ContractTests
{
    [TestClass]
    public sealed class ExternalSnapshotCatalogReaderTests
    {
        private string _rootDirectory;

        [TestInitialize]
        public void Initialize()
        {
            _rootDirectory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsExternalSnapshotTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootDirectory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, true);
            }
        }

        [TestMethod]
        public void Load_AcceptsAuthoritativeSnapshot()
        {
            var gameId = Guid.NewGuid();
            WriteProducer(
                "producer-a",
                gameId,
                gameId,
                DateTime.Parse("2026-07-29T00:00:00Z").ToUniversalTime(),
                stateKnown: true,
                complete: true);

            var result = new ExternalSnapshotCatalogReader().Load(_rootDirectory);

            Assert.AreEqual(0, result.Diagnostics.Count);
            Assert.IsTrue(result.TryGetSnapshot(gameId, out var snapshot));
            Assert.IsTrue(snapshot.IsAuthoritative);
            Assert.AreEqual("gbe-compatible", snapshot.SourceKey);
            Assert.AreEqual("632470", snapshot.SourceGameId);
            Assert.AreEqual(2, snapshot.Achievements.Count);
            Assert.IsTrue(snapshot.Achievements[0].IsUnlocked);
            Assert.AreEqual(5, snapshot.Achievements[1].CurrentProgress);
            Assert.AreEqual(10, snapshot.Achievements[1].MaximumProgress);
        }

        [TestMethod]
        public void Load_PreservesUnknownStateAsNonAuthoritative()
        {
            var gameId = Guid.NewGuid();
            WriteProducer(
                "producer-a",
                gameId,
                gameId,
                DateTime.UtcNow,
                stateKnown: false,
                complete: false);

            var result = new ExternalSnapshotCatalogReader().Load(_rootDirectory);

            Assert.IsTrue(result.TryGetSnapshot(gameId, out var snapshot));
            Assert.IsFalse(snapshot.IsAuthoritative);
            Assert.IsFalse(snapshot.StateKnown);
            Assert.IsFalse(snapshot.IsCompleteSnapshot);
        }

        [TestMethod]
        public void Load_RejectsPathTraversal()
        {
            var gameId = Guid.NewGuid();
            WriteIndexOnly("producer-a", gameId, "../../outside.json");

            var result = new ExternalSnapshotCatalogReader().Load(_rootDirectory);

            Assert.IsFalse(result.TryGetSnapshot(gameId, out _));
            Assert.IsTrue(result.Diagnostics.Any(message =>
                message.IndexOf("unsafe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("traversal", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public void Load_RejectsSnapshotGameIdMismatch()
        {
            var indexedGameId = Guid.NewGuid();
            WriteProducer(
                "producer-a",
                indexedGameId,
                Guid.NewGuid(),
                DateTime.UtcNow,
                stateKnown: true,
                complete: true);

            var result = new ExternalSnapshotCatalogReader().Load(_rootDirectory);

            Assert.IsFalse(result.TryGetSnapshot(indexedGameId, out _));
            Assert.IsTrue(result.Diagnostics.Any(message =>
                message.IndexOf("mismatch", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public void Load_RejectsUnsupportedSnapshotSchema()
        {
            var gameId = Guid.NewGuid();
            WriteProducer(
                "producer-a",
                gameId,
                gameId,
                DateTime.UtcNow,
                stateKnown: true,
                complete: true,
                snapshotSchemaVersion: 2);

            var result = new ExternalSnapshotCatalogReader().Load(_rootDirectory);

            Assert.IsFalse(result.TryGetSnapshot(gameId, out _));
            Assert.IsTrue(result.Diagnostics.Any(message =>
                message.IndexOf("schema version 2", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public void Load_SelectsNewestSnapshotAcrossProducers()
        {
            var gameId = Guid.NewGuid();
            WriteProducer(
                "producer-old",
                gameId,
                gameId,
                DateTime.Parse("2026-07-28T00:00:00Z").ToUniversalTime(),
                stateKnown: true,
                complete: true,
                sourceGameId: "111");
            WriteProducer(
                "producer-new",
                gameId,
                gameId,
                DateTime.Parse("2026-07-29T00:00:00Z").ToUniversalTime(),
                stateKnown: true,
                complete: true,
                sourceGameId: "222");

            var result = new ExternalSnapshotCatalogReader().Load(_rootDirectory);

            Assert.IsTrue(result.TryGetSnapshot(gameId, out var snapshot));
            Assert.AreEqual("222", snapshot.SourceGameId);
            Assert.IsTrue(result.Diagnostics.Any(message =>
                message.IndexOf("newest", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private void WriteProducer(
            string producerDirectoryName,
            Guid indexedGameId,
            Guid snapshotGameId,
            DateTime generatedAtUtc,
            bool stateKnown,
            bool complete,
            int snapshotSchemaVersion = 1,
            string sourceGameId = "632470")
        {
            var producerRoot = Path.Combine(_rootDirectory, producerDirectoryName);
            var relativeSnapshotPath = $"snapshots/v1/{indexedGameId:N}.json";
            var snapshotPath = Path.Combine(
                producerRoot,
                relativeSnapshotPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath));

            var snapshot = new JObject
            {
                ["Format"] = ExternalSnapshotCatalogReader.SnapshotFormat,
                ["SchemaVersion"] = snapshotSchemaVersion,
                ["GeneratedAtUtc"] = generatedAtUtc.ToUniversalTime().ToString("o"),
                ["PlayniteGameId"] = snapshotGameId.ToString("D"),
                ["PlayniteGameName"] = "Fixture game",
                ["SourceKey"] = "gbe-compatible",
                ["SourceGameId"] = sourceGameId,
                ["StateKnown"] = stateKnown,
                ["IsCompleteSnapshot"] = complete,
                ["Achievements"] = new JArray
                {
                    new JObject
                    {
                        ["AchievementId"] = "ACH_ONE",
                        ["DisplayName"] = "One",
                        ["Description"] = "First",
                        ["IsHidden"] = false,
                        ["IsUnlocked"] = true,
                        ["UnlockTimeUtc"] = "2026-07-28T23:00:00Z"
                    },
                    new JObject
                    {
                        ["AchievementId"] = "ACH_TWO",
                        ["DisplayName"] = "Two",
                        ["IsHidden"] = true,
                        ["IsUnlocked"] = false,
                        ["CurrentProgress"] = 5,
                        ["MaximumProgress"] = 10
                    }
                }
            };
            File.WriteAllText(snapshotPath, snapshot.ToString());

            WriteIndex(
                producerRoot,
                producerDirectoryName,
                indexedGameId,
                relativeSnapshotPath,
                generatedAtUtc);
        }

        private void WriteIndexOnly(string producerDirectoryName, Guid gameId, string relativeSnapshotPath)
        {
            var producerRoot = Path.Combine(_rootDirectory, producerDirectoryName);
            WriteIndex(producerRoot, producerDirectoryName, gameId, relativeSnapshotPath, DateTime.UtcNow);
        }

        private static void WriteIndex(
            string producerRoot,
            string producerId,
            Guid gameId,
            string relativeSnapshotPath,
            DateTime generatedAtUtc)
        {
            var indexPath = Path.Combine(producerRoot, "bridge", "v1", "index.json");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath));
            var index = new JObject
            {
                ["Format"] = ExternalSnapshotCatalogReader.IndexFormat,
                ["SchemaVersion"] = ExternalSnapshotCatalogReader.SupportedSchemaVersion,
                ["ProducerId"] = producerId,
                ["ProducerName"] = producerId,
                ["ProducerVersion"] = "0.5.0",
                ["GeneratedAtUtc"] = generatedAtUtc.ToUniversalTime().ToString("o"),
                ["Entries"] = new JArray
                {
                    new JObject
                    {
                        ["PlayniteGameId"] = gameId.ToString("D"),
                        ["SnapshotRelativePath"] = relativeSnapshotPath,
                        ["SnapshotGeneratedAtUtc"] = generatedAtUtc.ToUniversalTime().ToString("o")
                    }
                }
            };
            File.WriteAllText(indexPath, index.ToString());
        }
    }
}
