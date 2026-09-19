using System;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IndexFreshnessTests
    {
        [Fact]
        public void Restored_index_is_available_but_marked_stale_until_scanned()
        {
            var cache = new IndexCacheService();
            var captured = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

            cache.MarkIndexRestored(12, captured);

            var restored = cache.GetState();
            Assert.Equal("Ready", restored.Status);
            Assert.Equal("stale", restored.Freshness);
            Assert.Equal(captured, restored.LastIndexedAt);
            Assert.Equal(captured, restored.LastSuccessfulScanAt);

            cache.MarkIndexRefreshing();
            Assert.Equal("refreshing", cache.GetState().Freshness);

            cache.MarkIndexComplete(12);
            var current = cache.GetState();
            Assert.Equal("current", current.Freshness);
            Assert.True(current.LastSuccessfulScanAt.HasValue);
            Assert.True(current.LastSuccessfulScanAt.Value >= captured);
        }
    }
}
