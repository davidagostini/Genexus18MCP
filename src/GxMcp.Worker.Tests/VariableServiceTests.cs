using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class VariableServiceTests
    {
        [Theory]
        [InlineData("Character(40)", "Character", 40, null)]
        [InlineData("Numeric(10,2)", "Numeric", 10, 2)]
        [InlineData("Numeric(8.0)", "Numeric", 8, 0)]
        [InlineData("VarChar(120)", "VarChar", 120, null)]
        [InlineData("Boolean", "Boolean", null, null)]
        [InlineData("Date", "Date", null, null)]
        [InlineData("DateTime", "DateTime", null, null)]
        [InlineData("GUID", "GUID", null, null)]
        public void ResolveType_Primitives_ResolvesCorrectly(string input, string expectedType, int? expectedLen, int? expectedDec)
        {
            var svc = new VariableService(objectService: null, writeService: null);
            var res = svc.ResolveType(input);

            Assert.True(res.Recognized);
            Assert.Equal(expectedType, res.CanonicalType);
            Assert.Equal(expectedLen, res.Length);
            Assert.Equal(expectedDec, res.Decimals);
        }

        [Theory]
        [InlineData("&CustomerId", "CustomerId")]
        [InlineData("SdtInvoice", "SdtInvoice")]
        [InlineData("SdtCustomer.Item", "SdtCustomer.Item")]
        public void ResolveType_DomainAndSdtReferences_ResolvesAsDomainReference(string input, string expectedDomain)
        {
            var svc = new VariableService(objectService: null, writeService: null);
            var res = svc.ResolveType(input);

            Assert.True(res.Recognized);
            Assert.Equal("DomainReference", res.CanonicalType);
            Assert.Equal(expectedDomain, res.DomainName);
        }

        [Fact]
        public void ResolveType_UnknownMalformed_ReturnsSuggestion()
        {
            var svc = new VariableService(objectService: null, writeService: null);
            var res = svc.ResolveType("Numerik(10)");

            Assert.False(res.Recognized);
            Assert.Equal("Numeric", res.Suggestion);
            Assert.NotNull(res.AcceptedList);
        }

        [Fact]
        public void VariableService_NullWriteService_ReturnsServiceUnavailable()
        {
            var svc = new VariableService(objectService: null, writeService: null);
            var resp = JObject.Parse(svc.AddVariable("Foo", "CustomerId", "Numeric(10)"));

            Assert.Equal("error", (string)resp["status"]);
            Assert.Equal("ServiceUnavailable", (string)resp["error"]?["code"]);
        }
    }
}
