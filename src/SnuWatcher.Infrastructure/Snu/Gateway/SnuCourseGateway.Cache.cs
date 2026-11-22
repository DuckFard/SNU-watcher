using SnuWatcher.Application.Sources;

namespace SnuWatcher.Infrastructure.Snu.Gateway;

public sealed partial class SnuCourseGateway
{
    // All cache access is protected by the gateway lock. Reads retain the original immutable result/time.
    private sealed class MetadataCache<TKey, T>(int capacity, TimeSpan ttl, TimeProvider clock)
        where TKey : notnull where T : class
    {
        private readonly Dictionary<TKey, Entry> _entries = [];
        private readonly LinkedList<TKey> _order = [];
        private sealed record Entry(SourceRead<T> Read, long StoredAt, LinkedListNode<TKey> Node);

        public bool TryGet(TKey key, out SourceRead<T>? read)
        {
            read = null;
            if (!_entries.TryGetValue(key, out var entry)) return false;
            var elapsed = clock.GetElapsedTime(entry.StoredAt, clock.GetTimestamp());
            if (elapsed < TimeSpan.Zero || elapsed >= ttl)
            {
                _entries.Remove(key);
                _order.Remove(entry.Node);
                return false;
            }
            _order.Remove(entry.Node);
            _order.AddLast(entry.Node);
            read = entry.Read;
            return true;
        }

        public void Set(TKey key, SourceRead<T> read)
        {
            if (_entries.Remove(key, out var existing)) _order.Remove(existing.Node);
            while (_entries.Count >= capacity)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _entries.Remove(oldest.Value);
            }
            _entries[key] = new(read, clock.GetTimestamp(), _order.AddLast(key));
        }
    }
}
