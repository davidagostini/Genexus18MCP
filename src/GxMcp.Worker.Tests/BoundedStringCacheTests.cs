using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BoundedStringCacheTests
    {
        [Fact]
        public void TryAdd_EvictsLeastRecentlyUsedItem()
        {
            var cache = new BoundedStringCache(2);

            cache.TryAdd("one", "1");
            cache.TryAdd("two", "2");
            Assert.True(cache.TryGetValue("one", out _));

            cache.TryAdd("three", "3");

            Assert.True(cache.TryGetValue("one", out _));
            Assert.False(cache.TryGetValue("two", out _));
            Assert.True(cache.TryGetValue("three", out _));
        }

        [Fact]
        public void TryRemove_RemovesItemAndSupportsOutValue()
        {
            var cache = new BoundedStringCache(4);
            cache.TryAdd("k1", "v1");
            Assert.True(cache.TryRemove("k1", out var removedVal));
            Assert.Equal("v1", removedVal);
            Assert.False(cache.TryGetValue("k1", out _));
            Assert.False(cache.TryRemove("nonexistent", out _));
        }
    }
}
