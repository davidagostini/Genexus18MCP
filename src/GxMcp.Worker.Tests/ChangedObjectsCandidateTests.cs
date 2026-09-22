using System;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class ChangedObjectsCandidateTests
    {
        [Fact]
        public void LastUpdateSelectsContentCandidatesAndUnknownTimesStayConservative()
        {
            DateTime baseline = DateTime.Parse("2026-09-21T10:00:00");
            Assert.False(KbVersionService.IsContentCandidate(baseline, baseline));
            Assert.True(KbVersionService.IsContentCandidate(baseline, baseline.AddSeconds(1)));
            Assert.True(KbVersionService.IsContentCandidate(DateTime.MinValue, DateTime.MinValue));
        }
    }
}
