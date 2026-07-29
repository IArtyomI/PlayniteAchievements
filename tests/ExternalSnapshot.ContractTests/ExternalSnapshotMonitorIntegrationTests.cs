using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.ExternalSnapshot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ExternalSnapshot.ContractTests
{
    [TestClass]
    public sealed class ExternalSnapshotMonitorIntegrationTests
    {
        [TestMethod]
        public void ProviderTransitionBecomesBaselineThenOneLaterUnlockIsEmitted()
        {
            var providerTransition = ExternalSnapshotUnlockPolicy.SelectNewUnlockKeys(
                States(("A", false), ("B", false)),
                States(("A", true), ("B", false)),
                ExternalSnapshotUnlockPolicy.HasAuthoritativeBaseline("Steam"));
            var subsequent = ExternalSnapshotUnlockPolicy.SelectNewUnlockKeys(
                States(("A", true), ("B", false)),
                States(("A", true), ("B", true)),
                ExternalSnapshotUnlockPolicy.HasAuthoritativeBaseline("ExternalSnapshot"));

            Assert.AreEqual(0, providerTransition.Count);
            CollectionAssert.AreEqual(new[] { "B" }, subsequent.ToArray());
        }

        [TestMethod]
        public void DuplicateEventAndSecondActiveChangeScheduleExactlyTwoRefreshes()
        {
            var gate = new ExternalSnapshotBridgeChangeGate();
            var gameId = Guid.NewGuid();
            var refreshes = 0;

            if (gate.TryBegin(gameId, "baseline"))
            {
                refreshes++;
            }
            Assert.IsFalse(gate.TryBegin(gameId, "baseline"));
            Assert.IsFalse(gate.TryBegin(gameId, "unlock-one"));
            Assert.IsTrue(gate.Complete(gameId));
            if (gate.TryBegin(gameId, "unlock-one"))
            {
                refreshes++;
            }
            Assert.IsFalse(gate.Complete(gameId));

            Assert.AreEqual(2, refreshes);
        }

        [TestMethod]
        public void MalformedReplacementOrProducerDisappearanceDoesNotScheduleCacheMutation()
        {
            var gate = new ExternalSnapshotBridgeChangeGate();
            var gameId = Guid.NewGuid();
            Assert.IsTrue(gate.TryBegin(gameId, "valid"));
            Assert.IsFalse(gate.Complete(gameId));

            gate.RetainOnly(Array.Empty<Guid>());

            Assert.IsTrue(gate.TryBegin(gameId, "restored-valid"));
        }

        private static IEnumerable<KeyValuePair<string, bool>> States(
            params (string Key, bool Unlocked)[] values)
        {
            return values.Select(value => new KeyValuePair<string, bool>(value.Key, value.Unlocked));
        }
    }
}
