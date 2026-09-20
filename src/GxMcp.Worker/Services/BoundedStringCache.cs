using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Services
{
    internal sealed class BoundedStringCache
    {
        private readonly int _capacity;
        private readonly Dictionary<string, Entry> _map;
        private readonly LinkedList<string> _lru = new LinkedList<string>();
        private readonly object _lock = new object();

        // PERFORMANCE (observability): track cache hit/miss/eviction counts so a degraded
        // hit ratio (e.g. capacity too small for the query mix) is visible from whoami /
        // gateway:metrics without standing up an external profiler.
        private long _hits;
        private long _misses;
        private long _evictions;
        public long Hits => System.Threading.Interlocked.Read(ref _hits);
        public long Misses => System.Threading.Interlocked.Read(ref _misses);
        public long Evictions => System.Threading.Interlocked.Read(ref _evictions);
        public int Count { get { lock (_lock) return _map.Count; } }
        public int Capacity => _capacity;

        public BoundedStringCache(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGetValue(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) { System.Threading.Interlocked.Increment(ref _misses); return false; }

            lock (_lock)
            {
                if (!_map.TryGetValue(key, out var entry))
                {
                    System.Threading.Interlocked.Increment(ref _misses);
                    return false;
                }
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                value = entry.Value;
            }
            System.Threading.Interlocked.Increment(ref _hits);
            return true;
        }

        public void TryAdd(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return;

            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value = value;
                    _lru.Remove(existing.Node);
                    _lru.AddFirst(existing.Node);
                    return;
                }

                while (_map.Count >= _capacity)
                {
                    var last = _lru.Last;
                    if (last == null) break;
                    _map.Remove(last.Value);
                    _lru.RemoveLast();
                    System.Threading.Interlocked.Increment(ref _evictions);
                }

                var node = new LinkedListNode<string>(key);
                _lru.AddFirst(node);
                _map[key] = new Entry { Value = value, Node = node };
            }
        }

        public bool TryRemove(string key) => TryRemove(key, out _);

        public bool TryRemove(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    value = entry.Value;
                    _map.Remove(key);
                    if (entry.Node != null) _lru.Remove(entry.Node);
                    return true;
                }
                return false;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _lru.Clear();
            }
        }

        private sealed class Entry
        {
            public string Value;
            public LinkedListNode<string> Node;
        }
    }
}
