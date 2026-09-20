using System;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class SourceSearchMetadataCacheTests
    {
        private readonly ITestOutputHelper _output;

        public SourceSearchMetadataCacheTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void MultiFieldCandidate_ResolvesOnceAndReadsEachPartOnce()
        {
            int resolutions = 0;
            int partReads = 0;
            var cache = new SourceSearchService.MetadataCandidateCache<string>(
                () =>
                {
                    resolutions++;
                    return "candidate";
                },
                (candidate, part) =>
                {
                    partReads++;
                    return candidate + ":" + part;
                });

            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal("candidate:rules", cache.ReadPart("rules"));
            Assert.Equal("candidate:rules", cache.ReadPart("rules"));
            Assert.Equal("candidate:webForm", cache.ReadPart("webForm"));
            Assert.Equal("candidate:webForm", cache.ReadPart("webForm"));
            Assert.Equal("candidate", cache.Object);
            started.Stop();

            Assert.Equal(1, resolutions);
            Assert.Equal(2, partReads);
            Assert.Equal(1, cache.ResolutionCount);
            Assert.Equal(2, cache.PartReadCount);
            _output.WriteLine("metadata multi-field elapsed_ms={0:F3} resolutions={1} part_reads={2}",
                started.Elapsed.TotalMilliseconds, resolutions, partReads);
        }

        [Fact]
        public void FailedCandidate_ResolutionIsNotRetriedAcrossFields()
        {
            int resolutions = 0;
            var cache = new SourceSearchService.MetadataCandidateCache<string>(
                () =>
                {
                    resolutions++;
                    return null;
                },
                (_, _) => throw new InvalidOperationException("part reader must not run"));

            Assert.Null(cache.Object);
            Assert.Null(cache.Object);
            Assert.Equal(1, resolutions);
            Assert.Equal(0, cache.PartReadCount);
        }
    }
}
