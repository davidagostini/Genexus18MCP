using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// DDL preview now delegates to non-mutating Impact Analysis. Without an open
    /// KB it must fail explicitly instead of returning a misleading empty stub.
    /// </summary>
    public class ReorgPreviewTests
    {
        [Fact]
        public void ReorgPreview_WithoutKbReturnsCanonicalError()
        {
            var svc = new BuildService();
            string json = svc.ReorgPreview("MyTrn");
            var jo = JObject.Parse(json);
            Assert.Equal("error", jo["status"]?.ToString());
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }
    }
}
