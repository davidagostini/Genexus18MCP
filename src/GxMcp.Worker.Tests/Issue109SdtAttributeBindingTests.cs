using System.Text;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Parsers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class Issue109SdtAttributeBindingTests
    {
        public class FakeSdtLevel
        {
            public string Name { get; set; }
            public bool IsLeafItem { get; set; }
            public bool IsCollection { get; set; }
            public string Type { get; set; }
            public int? Length { get; set; }
            public int? Decimals { get; set; }
            public object AttributeBasedOn { get; set; }
            public object DomainBasedOn { get; set; }
            public System.Collections.Generic.List<FakeSdtLevel> Items { get; set; } = new System.Collections.Generic.List<FakeSdtLevel>();
        }

        public class FakeAttr
        {
            public string Name { get; set; }
        }

        [Fact]
        public void SdtDslParser_SerializeLevel_EmitsAttributePrefix()
        {
            var parser = new SdtDslParser();
            var item = new FakeSdtLevel
            {
                Name = "CardapioID",
                IsLeafItem = true,
                Type = "NUMERIC",
                Length = 10,
                AttributeBasedOn = new FakeAttr { Name = "CardapioID" }
            };

            var sb = new StringBuilder();
            var mi = typeof(SdtDslParser).GetMethod("SerializeLevel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            mi.Invoke(parser, new object[] { item, sb, 0 });

            string output = sb.ToString().Trim();
            Assert.Equal("CardapioID : Attribute:CardapioID", output);
        }

        [Fact]
        public void SdtDslParser_SerializeLevel_CollectionAttribute_EmitsCollectionMarker()
        {
            var parser = new SdtDslParser();
            var item = new FakeSdtLevel
            {
                Name = "Tags",
                IsLeafItem = true,
                IsCollection = true,
                Type = "VARCHAR",
                AttributeBasedOn = new FakeAttr { Name = "TagDescricao" }
            };

            var sb = new StringBuilder();
            var mi = typeof(SdtDslParser).GetMethod("SerializeLevel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            mi.Invoke(parser, new object[] { item, sb, 0 });

            string output = sb.ToString().Trim();
            Assert.Equal("Tags : Attribute:TagDescricao Collection", output);
        }

        [Fact]
        public void SDTService_MapLevelToResult_PopulatesBasedOnAttribute()
        {
            var item = new FakeSdtLevel
            {
                Name = "CardapioAceitaTroca",
                IsLeafItem = true,
                Type = "BOOLEAN",
                AttributeBasedOn = new FakeAttr { Name = "CardapioAceitaTroca" }
            };

            var sdtSvc = new SDTService(null);
            var mi = typeof(SDTService).GetMethod("MapLevelToResult", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var res = mi.Invoke(sdtSvc, new object[] { item, null }) as JObject;

            Assert.NotNull(res);
            Assert.Equal("CardapioAceitaTroca", res["name"]?.ToString());
            Assert.Equal("CardapioAceitaTroca", res["basedOnAttribute"]?.ToString());
            Assert.Null(res["basedOnDomain"]);
        }
    }
}
