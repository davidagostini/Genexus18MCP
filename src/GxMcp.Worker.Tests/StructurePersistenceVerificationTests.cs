using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class StructurePersistenceVerificationTests
    {
        [Fact]
        public void CompareStructureNames_DetectsUnexpectedPersistedItems()
        {
            var diff = StructureService.CompareStructureNames(
                new[] { "CustomerId" },
                new[] { "CustomerId", "StaleAttribute" });

            Assert.Empty((Newtonsoft.Json.Linq.JArray)diff["missing"]);
            Assert.Equal("StaleAttribute", ((Newtonsoft.Json.Linq.JArray)diff["unexpected"])[0].ToString());
        }

        [Fact]
        public void CompareStructureNames_EmptyRequestedSetMustRemainEmpty()
        {
            var diff = StructureService.CompareStructureNames(
                new string[0],
                new[] { "StillThere" });

            Assert.Empty((Newtonsoft.Json.Linq.JArray)diff["missing"]);
            Assert.Equal("StillThere", ((Newtonsoft.Json.Linq.JArray)diff["unexpected"])[0].ToString());
        }

        [Fact]
        public void CompareStructureNames_DetectsMissingRequestedItems()
        {
            var diff = StructureService.CompareStructureNames(
                new[] { "CustomerId", "CustomerName" },
                new[] { "CustomerId" });

            Assert.Equal("CustomerName", ((Newtonsoft.Json.Linq.JArray)diff["missing"])[0].ToString());
            Assert.Empty((Newtonsoft.Json.Linq.JArray)diff["unexpected"]);
        }
    }
}
