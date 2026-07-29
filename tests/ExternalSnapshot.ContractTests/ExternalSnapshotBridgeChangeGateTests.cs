using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.ExternalSnapshot;
using System;

namespace ExternalSnapshot.ContractTests
{
    [TestClass]
    public sealed class ExternalSnapshotBridgeChangeGateTests
    {
        [TestMethod]
        public void DuplicateAndUnchangedSignaturesDoNotRefreshTwice()
        {
            var gate = new ExternalSnapshotBridgeChangeGate();
            var game = Guid.NewGuid();
            Assert.IsTrue(gate.TryBegin(game, "one"));
            Assert.IsFalse(gate.TryBegin(game, "one"));
            Assert.IsFalse(gate.Complete(game));
            Assert.IsFalse(gate.TryBegin(game, "one"));
        }

        [TestMethod]
        public void ChangeDuringActiveRefreshIsProcessedOnceAfterward()
        {
            var gate = new ExternalSnapshotBridgeChangeGate();
            var game = Guid.NewGuid();
            Assert.IsTrue(gate.TryBegin(game, "one"));
            Assert.IsFalse(gate.TryBegin(game, "two"));
            Assert.IsFalse(gate.TryBegin(game, "two"));
            Assert.IsTrue(gate.Complete(game));
            Assert.IsTrue(gate.TryBegin(game, "two"));
            Assert.IsFalse(gate.Complete(game));
        }

        [TestMethod]
        public void DisposalPreventsLaterCallbacks()
        {
            var gate = new ExternalSnapshotBridgeChangeGate();
            gate.Dispose();
            Assert.IsFalse(gate.TryBegin(Guid.NewGuid(), "one"));
        }
    }
}
