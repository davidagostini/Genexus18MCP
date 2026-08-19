using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class KbCatalogPersistenceTests
    {
        [Fact]
        public void UpsertKbCatalogEntry_PreservesCliMapShapeAndExistingEntries()
        {
            var environment = JObject.Parse(@"{
  'KBs': {
    'main': 'C:/KB/Main',
    'legacy': 'C:/KB/Legacy'
  }
}".Replace('\'', '"'));

            bool added = Program.UpsertKbCatalogEntry(environment, "scratch", "C:/KB/Scratch");

            Assert.True(added);
            var catalog = Assert.IsType<JObject>(environment["KBs"]);
            Assert.Equal("C:/KB/Main", catalog["main"]?.ToString());
            Assert.Equal("C:/KB/Legacy", catalog["legacy"]?.ToString());
            Assert.Equal("C:/KB/Scratch", catalog["scratch"]?.ToString());
        }

        [Fact]
        public void UpsertKbCatalogEntry_PreservesGatewayArrayShapeAndExistingEntries()
        {
            var environment = JObject.Parse(@"{
  'KBs': [
    { 'Alias': 'main', 'Path': 'C:/KB/Main' },
    { 'alias': 'legacy', 'path': 'C:/KB/Legacy' }
  ]
}".Replace('\'', '"'));

            bool added = Program.UpsertKbCatalogEntry(environment, "scratch", "C:/KB/Scratch");

            Assert.True(added);
            var catalog = Assert.IsType<JArray>(environment["KBs"]);
            Assert.Equal(3, catalog.Count);
            Assert.Equal("main", catalog[0]?["Alias"]?.ToString());
            Assert.Equal("legacy", catalog[1]?["alias"]?.ToString());
            Assert.Equal("scratch", catalog[2]?["Alias"]?.ToString());
            Assert.Equal("C:/KB/Scratch", catalog[2]?["Path"]?.ToString());
        }

        [Fact]
        public void UpsertKbCatalogEntry_DoesNotOverwriteExistingAlias()
        {
            var environment = JObject.Parse(@"{ 'KBs': { 'main': 'C:/KB/Main' } }".Replace('\'', '"'));

            bool added = Program.UpsertKbCatalogEntry(environment, "MAIN", "C:/KB/Other");

            Assert.False(added);
            var catalog = Assert.IsType<JObject>(environment["KBs"]);
            Assert.Equal("C:/KB/Main", catalog["main"]?.ToString());
        }
    }
}
