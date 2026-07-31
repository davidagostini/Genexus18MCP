using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // issue #57 — Nullable / ALLOWNULL writes on Transaction attribute occurrences.
    // The SDK surface is TableAttribute.IsNullableValue (False=0/True=1/Compatible=2) and
    // the IDE's ALLOWNULL/Nullable names must map onto it; the generic string setter can't
    // ("Yes" is the converter's display string, not an enum member name).
    public class NullablePropertyMappingTests
    {
        [Theory]
        [InlineData("ALLOWNULL", true)]
        [InlineData("allownull", true)]
        [InlineData("Nullable", true)]
        [InlineData("IsNullable", true)]
        [InlineData("ISNULLABLE", true)]
        [InlineData("Description", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsNullablePropertyName_RecognizesAliases(string name, bool expected)
        {
            Assert.Equal(expected, PropertyService.IsNullablePropertyName(name));
        }

        [Theory]
        [InlineData("Yes", 1)]
        [InlineData("TRUE", 1)]
        [InlineData("1", 1)]
        [InlineData("No", 0)]
        [InlineData("false", 0)]
        [InlineData("0", 0)]
        [InlineData("Managed", 2)]
        [InlineData("Compatible", 2)]
        [InlineData("2", 2)]
        [InlineData("anything-else", 0)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        public void ParseIsNullableValue_MapsForms(string raw, int expected)
        {
            Assert.Equal(expected, PropertyService.ParseIsNullableValue(raw));
        }
    }
}
