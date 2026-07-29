using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.ExternalSnapshot;
using System.Collections.Generic;
using System.Linq;

namespace ExternalSnapshot.ContractTests
{
    [TestClass]
    public sealed class ExternalSnapshotUnlockPolicyTests
    {
        [DataTestMethod]
        [DataRow(null, false)]
        [DataRow("", false)]
        [DataRow("Steam", false)]
        [DataRow("Manual", false)]
        [DataRow("ExternalSnapshot", true)]
        [DataRow("externalsnapshot", true)]
        public void OnlyExternalSnapshotCacheIsAuthoritativeBaseline(string providerKey, bool expected)
        {
            Assert.AreEqual(expected, ExternalSnapshotUnlockPolicy.HasAuthoritativeBaseline(providerKey));
        }

        [TestMethod]
        public void InitialAuthoritativeImportIsBaselineOnly()
        {
            var result = ExternalSnapshotUnlockPolicy.SelectNewUnlockKeys(
                null,
                States(("A", true), ("B", true)),
                hasBaseline: false);

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void UnchangedRefreshAndTimestampIndependentStateEmitNothing()
        {
            var result = ExternalSnapshotUnlockPolicy.SelectNewUnlockKeys(
                States(("A", true), ("B", false)),
                States(("A", true), ("B", false)),
                hasBaseline: true);

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void OneLockedToUnlockedTransitionEmitsOneKey()
        {
            var result = ExternalSnapshotUnlockPolicy.SelectNewUnlockKeys(
                States(("A", false), ("B", false)),
                States(("A", true), ("B", false)),
                hasBaseline: true);

            CollectionAssert.AreEqual(new[] { "A" }, result.ToArray());
        }

        [TestMethod]
        public void MultipleUnlocksAreDistinctAndCaseInsensitive()
        {
            var result = ExternalSnapshotUnlockPolicy.SelectNewUnlockKeys(
                States(("A", false), ("B", false)),
                States(("A", true), ("a", true), ("B", true)),
                hasBaseline: true);

            CollectionAssert.AreEquivalent(new[] { "A", "B" }, result.ToArray());
        }

        [TestMethod]
        public void RollbackDoesNotCreateAnUnlock()
        {
            var result = ExternalSnapshotUnlockPolicy.SelectNewUnlockKeys(
                States(("A", true)),
                States(("A", false)),
                hasBaseline: true);

            Assert.AreEqual(0, result.Count);
        }

        private static IEnumerable<KeyValuePair<string, bool>> States(
            params (string Key, bool Unlocked)[] values)
        {
            return values.Select(value => new KeyValuePair<string, bool>(value.Key, value.Unlocked));
        }
    }
}
