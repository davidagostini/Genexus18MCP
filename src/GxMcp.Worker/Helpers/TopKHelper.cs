using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Canonical bounded top-K selection helper using a min- or max-heap.
    /// Eliminates sorting entire collections of N items when only top-K items are needed.
    /// </summary>
    public static class TopKHelper
    {
        public static List<T> SelectTopK<T>(IEnumerable<T> source, int k, IComparer<T> comparer, out int totalCount)
        {
            if (source == null || k <= 0)
            {
                totalCount = 0;
                return new List<T>(0);
            }

            var heap = new BoundedHeap<T>(k, comparer);
            int count = 0;
            foreach (var item in source)
            {
                count++;
                heap.Push(item);
            }

            totalCount = count;
            return heap.ToSortedList();
        }

        /// <summary>
        /// Bounded heap container that maintains the top-K items according to the order defined by <paramref name="comparer"/>.
        /// <para>
        /// <b>Ordering invariant:</b> The comparer must define the desired final sort order (i.e. <c>Compare(a, b) &lt; 0</c>
        /// means <c>a</c> precedes <c>b</c> in the final sorted list).
        /// Internally, the heap keeps the <i>least preferred</i> (worst) candidate at the root (<c>_heap[0]</c>).
        /// When the heap is full, incoming items comparing less than the root (<c>Compare(item, root) &lt; 0</c>)
        /// evict the root in <c>O(log K)</c> time. Calling <see cref="ToSortedList"/> sorts and returns the top-K in final order.
        /// </para>
        /// </summary>
        public sealed class BoundedHeap<T>
        {
            private readonly int _capacity;
            private readonly IComparer<T> _comparer;
            private readonly T[] _heap;
            private int _count;

            public BoundedHeap(int capacity, IComparer<T> comparer = null)
            {
                if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
                _capacity = capacity;
                _comparer = comparer ?? Comparer<T>.Default;
                _heap = new T[capacity];
            }

            public int Count => _count;

            public void Push(T item)
            {
                if (_count < _capacity)
                {
                    _heap[_count] = item;
                    SiftUp(_count);
                    _count++;
                }
                else if (_comparer.Compare(item, _heap[0]) < 0)
                {
                    _heap[0] = item;
                    SiftDown(0);
                }
            }

            private void SiftUp(int child)
            {
                while (child > 0)
                {
                    int parent = (child - 1) >> 1;
                    if (_comparer.Compare(_heap[child], _heap[parent]) > 0)
                    {
                        var tmp = _heap[child];
                        _heap[child] = _heap[parent];
                        _heap[parent] = tmp;
                        child = parent;
                    }
                    else break;
                }
            }

            private void SiftDown(int parent)
            {
                while (true)
                {
                    int left = (parent << 1) + 1;
                    if (left >= _capacity) break;
                    int right = left + 1;
                    int bestChild = (right < _capacity && _comparer.Compare(_heap[right], _heap[left]) > 0) ? right : left;
                    if (_comparer.Compare(_heap[bestChild], _heap[parent]) > 0)
                    {
                        var tmp = _heap[parent];
                        _heap[parent] = _heap[bestChild];
                        _heap[bestChild] = tmp;
                        parent = bestChild;
                    }
                    else break;
                }
            }

            public List<T> ToSortedList()
            {
                if (_count == 0) return new List<T>(0);
                var result = new T[_count];
                Array.Copy(_heap, result, _count);
                Array.Sort(result, _comparer);
                return new List<T>(result);
            }
        }
    }
}
