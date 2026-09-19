using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // issue #117 — Domain assignment on Attributes/Domains.
    // Setting Domain / DomainBasedOn / BasedOn routes to DomainBasedOn rather than
    // a scalar string in the property bag.
    public class DomainPropertyMappingTests
    {
        [Theory]
        [InlineData("Domain", true)]
        [InlineData("domain", true)]
        [InlineData("DomainBasedOn", true)]
        [InlineData("domainbasedon", true)]
        [InlineData("BasedOn", true)]
        [InlineData("basedon", true)]
        [InlineData("DomainDefinition", true)]
        [InlineData("Description", false)]
        [InlineData("Type", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsDomainPropertyName_RecognizesAliases(string name, bool expected)
        {
            Assert.Equal(expected, PropertyService.IsDomainPropertyName(name));
        }
    }
}
