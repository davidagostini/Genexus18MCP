using System;
using Xunit;
using GxMcp.Worker.Structure;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class PartSerializerRegistryTests
    {
        [Fact]
        public void Registry_FindsRegisteredSerializers_ByPartName()
        {
            var sourceSerializer = PartSerializerRegistry.Find("Source");
            var rulesSerializer = PartSerializerRegistry.Find("Rules");
            var webFormSerializer = PartSerializerRegistry.Find("WebForm");
            var varsSerializer = PartSerializerRegistry.Find("Variables");

            Assert.NotNull(sourceSerializer);
            Assert.NotNull(rulesSerializer);
            Assert.NotNull(webFormSerializer);
            Assert.NotNull(varsSerializer);
        }

        [Fact]
        public void QueryGrammar_ExtractsPrefixesAndTerms_Accurately()
        {
            string query = @"type:Procedure parent:Billing name:""ProcessInvoices"" description:monthly urgent";
            var criteria = QueryGrammar.Parse(query);

            Assert.Equal("Procedure", criteria.TypeFilter);
            Assert.Equal("Billing", criteria.ParentFilter);
            Assert.Equal("ProcessInvoices", criteria.NameFilter);
            Assert.Equal("monthly", criteria.DescriptionFilter);
            Assert.Contains("urgent", criteria.FreeTerms);
        }

        [Fact]
        public void QueryGrammar_NormalizesTypeAliases()
        {
            Assert.Equal("Procedure", QueryGrammar.NormalizeType("prc"));
            Assert.Equal("Transaction", QueryGrammar.NormalizeType("trn"));
            Assert.Equal("WebPanel", QueryGrammar.NormalizeType("wp"));
            Assert.Equal("DesignSystem", QueryGrammar.NormalizeType("dso"));
            Assert.Equal("DataSelector", QueryGrammar.NormalizeType("ds"));
        }
    }
}
