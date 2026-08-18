using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ObjectServiceDeleteTests
    {
        [Theory]
        [InlineData("TemporaryDomain", "TemporaryDomain")]
        [InlineData(" Root Module/TemporaryDomain ", "TemporaryDomain")]
        [InlineData("Operations/TemporaryDomain", "Operations.TemporaryDomain")]
        [InlineData("Operations\\TemporaryDomain", "Operations.TemporaryDomain")]
        public void NormalizeDomainLookupName_accepts_sdk_and_explorer_names(string input, string expected)
        {
            Assert.Equal(expected, ObjectService.NormalizeDomainLookupName(input));
        }

        [Fact]
        public void Delete_without_open_kb_returns_canonical_non_persisted_error()
        {
            var service = new ObjectService(new KbService(new IndexCacheService()), new BuildService());

            string json = service.DeleteObject("TemporaryDomain", "Domain", confirm: true, dryRun: true);

            Assert.Contains("KbNotOpen", json);
        }
    }
}
