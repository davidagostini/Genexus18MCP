using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class FileContentReadContractTests
    {
        [Fact]
        public void ReadFileContent_RequiresExternalOrInlineDestination()
        {
            var service = new ObjectService(null, null);

            var response = JObject.Parse(service.ReadFileContent("ConfigFile", "File"));

            Assert.Equal("error", (string)response["status"]);
            Assert.Equal("FileContentDestinationRequired", (string)response["error"]["code"]);
        }

        [Fact]
        public void ReadFileContent_DoesNotTreatMetadataAsBinaryByDefault()
        {
            var service = new ObjectService(null, null);

            var response = JObject.Parse(service.ReadFileContent(
                "ConfigFile", "File", outputPath: null, maxBytes: 1024, includeBase64: true));

            Assert.Equal("error", (string)response["status"]);
            Assert.NotEqual("FileContentRead", (string)response["code"]);
            Assert.DoesNotContain("<Properties", response.ToString());
        }
    }
}
