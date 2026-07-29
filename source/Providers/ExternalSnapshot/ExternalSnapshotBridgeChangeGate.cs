using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.ExternalSnapshot
{
    internal sealed class ExternalSnapshotBridgeChangeGate : IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<Guid, string> signatures = new Dictionary<Guid, string>();
        private readonly HashSet<Guid> active = new HashSet<Guid>();
        private readonly HashSet<Guid> pending = new HashSet<Guid>();
        private bool disposed;

        public bool TryBegin(Guid gameId, string signature)
        {
            lock (gate)
            {
                if (disposed ||
                    (signatures.TryGetValue(gameId, out var previous) &&
                     string.Equals(previous, signature, StringComparison.Ordinal)))
                {
                    return false;
                }

                signatures[gameId] = signature ?? string.Empty;
                if (active.Add(gameId))
                {
                    return true;
                }

                pending.Add(gameId);
                return false;
            }
        }

        public bool Complete(Guid gameId)
        {
            lock (gate)
            {
                active.Remove(gameId);
                if (disposed || !pending.Remove(gameId))
                {
                    return false;
                }

                signatures.Remove(gameId);
                return true;
            }
        }

        public void RetainOnly(IEnumerable<Guid> ids)
        {
            var retained = new HashSet<Guid>(ids ?? Enumerable.Empty<Guid>());
            lock (gate)
            {
                foreach (var missing in signatures.Keys.Where(id => !retained.Contains(id)).ToList())
                {
                    signatures.Remove(missing);
                    pending.Remove(missing);
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                signatures.Clear();
                active.Clear();
                pending.Clear();
            }
        }
    }
}
