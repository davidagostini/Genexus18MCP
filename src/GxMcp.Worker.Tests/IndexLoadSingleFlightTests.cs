using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IndexLoadSingleFlightTests
    {
        private static string UniqueKbPath() =>
            Path.Combine(Path.GetTempPath(), "gxmcp-singleflight-" + Guid.NewGuid().ToString("N"));

        private static SearchIndex.IndexEntry Entry(string name) =>
            new SearchIndex.IndexEntry { Name = name, Type = "Procedure", Guid = Guid.NewGuid().ToString() };

        [Fact]
        public async Task ConcurrentColdCallersShareOneLoaderAndSameReadyIndex()
        {
            string kbPath = UniqueKbPath();
            var writer = new IndexCacheService();
            writer.Initialize(kbPath, proactiveLoad: false);
            try
            {
                writer.ReplaceAll(new[] { Entry("ColdStart") });
                Assert.True(writer.FlushNow());

                var cache = new IndexCacheService();
                cache.Initialize(kbPath, proactiveLoad: false);
                Assert.Null(cache.TryGetLoadedIndex());

                var indexes = Enumerable.Range(0, 32)
                    .Select(_ => Task.Run(() => cache.GetIndex()))
                    .ToArray();
                var results = await Task.WhenAll(indexes);

                var first = results[0];
                Assert.All(results, result => Assert.Same(first, result));
                Assert.Single(first.Objects);
                Assert.Equal(1, cache.LoadInvocationCountForTest);
                Assert.Same(first, cache.TryGetLoadedIndex());
            }
            finally
            {
                writer.DeleteOnDiskSnapshot();
            }
        }

        [Fact]
        public void FailedColdLoadResetsStateAndAllowsLaterRetry()
        {
            var cache = new IndexCacheService();
            cache.Initialize(UniqueKbPath(), proactiveLoad: false);
            try
            {
                File.WriteAllText(cache.IndexPathForTest, "not valid json");

                Assert.Empty(cache.GetIndex().Objects);
                Assert.Null(cache.TryGetLoadedIndex());
                Assert.Equal(1, cache.LoadInvocationCountForTest);

                var expected = new SearchIndex();
                var entry = Entry("AfterRetry");
                expected.Objects["Procedure:AfterRetry"] = entry;
                File.WriteAllText(cache.IndexPathForTest, JsonConvert.SerializeObject(expected));

                var loaded = cache.GetIndex();

                Assert.True(loaded.Objects.ContainsKey("Procedure:AfterRetry"));
                Assert.Equal(2, cache.LoadInvocationCountForTest);
                Assert.Same(loaded, cache.TryGetLoadedIndex());
            }
            finally
            {
                cache.DeleteOnDiskSnapshot();
            }
        }
    }
}
