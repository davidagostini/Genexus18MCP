using System;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The projected PatternInstance write needs the parent of its host before it can snapshot anything. The instance XML
    /// names that parent (its transaction attribute), and the WorkWithPlus naming convention is only the fallback: an
    /// instance whose host was not named after its parent is found through the XML or not at all.
    /// </summary>
    public class PatternProjectionParentResolutionTests
    {
        [Fact]
        public void TheInstanceXmlNamesTheParentEvenWhenTheHostDoesNotFollowTheNamingConvention()
        {
            string xml = "<Instance transaction=\"" + Guid.Empty + "-SampleParent\" />";

            var withXml = WwpProjectionHelper.ParentNameCandidates("SampleHostWithAnotherName", xml);
            var withoutXml = WwpProjectionHelper.ParentNameCandidates("SampleHostWithAnotherName", null);

            Assert.Equal(new[] { "SampleParent" }, withXml);
            Assert.Empty(withoutXml);
        }

        [Fact]
        public void TheNamingConventionStaysAsTheFallbackBehindTheNameInTheXml()
        {
            string xml = "<Instance transaction=\"" + Guid.Empty + "-SampleParent\" />";

            var candidates = WwpProjectionHelper.ParentNameCandidates("WorkWithPlusOldParent", xml);

            Assert.Equal(new[] { "SampleParent", "OldParent" }, candidates);
        }

        [Fact]
        public void TheProjectedWriteResolvesTheParentWithTheInstanceXml()
        {
            string src = RepoSource.WithoutComments(
                RepoSource.Read("src", "GxMcp.Worker", "Services", "WriteService.PatternProjection.cs"));

            Assert.Contains("WwpProjectionHelper.ResolveHostParent(host, _objectService, requestedXml)", src);
            Assert.DoesNotContain("WwpProjectionHelper.ResolveHostParent(host, _objectService);", src);
        }
    }
}
