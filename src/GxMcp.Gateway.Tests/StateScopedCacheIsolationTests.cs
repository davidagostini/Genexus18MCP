using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class StateScopedCacheIsolationTests
    {
        private static StateScopeId Scope(string value) => StateScopeId.Parse(value);

        [Fact]
        public void SemanticCache_DoesNotLeakAcrossScopesOrGenerations()
        {
            var store = new SemanticCacheStore(16, System.TimeSpan.FromMinutes(30));
            var scopeA = Scope("0123456789abcdef0123456789abcdef");
            var scopeB = Scope("fedcba9876543210fedcba9876543210");
            var keyA = StateScopedCacheKey.Create(scopeA, "sales", 7, "genexus_query:{}");
            store.Set(keyA, new JObject { ["marker"] = "A-7" });

            Assert.True(store.TryGet(keyA, out var same));
            Assert.Equal("A-7", same["marker"]?.ToString());
            Assert.False(store.TryGet(StateScopedCacheKey.Create(scopeB, "sales", 7, "genexus_query:{}"), out _));
            Assert.False(store.TryGet(StateScopedCacheKey.Create(scopeA, "sales", 8, "genexus_query:{}"), out _));
        }

        [Fact]
        public async Task IdempotencyCache_DoesNotLeakAcrossScopesOrGenerations()
        {
            var cache = new IdempotencyCache(15, 1000);
            var scopeA = Scope("0123456789abcdef0123456789abcdef");
            var scopeB = Scope("fedcba9876543210fedcba9876543210");
            int calls = 0;
            Task<JObject> Factory()
            {
                calls++;
                return Task.FromResult(new JObject { ["call"] = calls });
            }

            await cache.GetOrCompute(scopeA, "sales", 7, "genexus_edit", "same", "hash", Factory);
            await cache.GetOrCompute(scopeB, "sales", 7, "genexus_edit", "same", "hash", Factory);
            await cache.GetOrCompute(scopeA, "sales", 8, "genexus_edit", "same", "hash", Factory);

            Assert.Equal(3, calls);
            Assert.True(cache.TryGet(scopeA, "sales", 7, "genexus_edit", "same", "hash", out _));
            Assert.False(cache.TryGet(scopeA, "sales", 9, "genexus_edit", "same", "hash", out _));
        }
    }
}
