using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Bug fix: genexus_refactor action=RenameObject dropped the `type` disambiguator
    // on the way to the worker, so two same-named objects across types (e.g. a Table
    // and a WebPanel both named "FavoritosUsuPrg") could never be told apart. These
    // tests pin that ConvertRefactorToolCall now forwards `type` into the payload.
    public class RefactorRenameObjectRouterTests
    {
        [Fact]
        public void RenameObject_Forwards_Type_Into_Payload()
        {
            var router = new OperationsRouter();
            var args = new JObject
            {
                ["action"] = "RenameObject",
                ["target"] = "FavoritosUsuPrg",
                ["newName"] = "FavoritosUsuPrgPanel",
                ["type"] = "WebPanel"
            };
            var routed = router.ConvertToolCall("genexus_refactor", args);
            Assert.NotNull(routed);
            var jo = JObject.FromObject(routed!);
            Assert.Equal("Refactor", jo["module"]!.ToString());
            Assert.Equal("RenameObject", jo["action"]!.ToString());
            var payload = JObject.Parse(jo["payload"]!.ToString());
            Assert.Equal("FavoritosUsuPrg", payload["oldName"]!.ToString());
            Assert.Equal("FavoritosUsuPrgPanel", payload["newName"]!.ToString());
            Assert.Equal("WebPanel", payload["type"]!.ToString());
        }

        [Fact]
        public void RenameAttribute_Payload_Has_No_Type_When_Not_Provided()
        {
            var router = new OperationsRouter();
            var args = new JObject
            {
                ["action"] = "RenameAttribute",
                ["target"] = "CustomerId",
                ["newName"] = "ClientId"
            };
            var routed = router.ConvertToolCall("genexus_refactor", args);
            var jo = JObject.FromObject(routed!);
            Assert.Equal("RenameAttribute", jo["action"]!.ToString());
            var payload = JObject.Parse(jo["payload"]!.ToString());
            Assert.Equal("CustomerId", payload["oldName"]!.ToString());
            Assert.True(payload["type"] == null || payload["type"]!.Type == JTokenType.Null);
        }

        [Fact]
        public void ExtractSubroutine_Routes_Properly_With_Payload()
        {
            var router = new OperationsRouter();
            var args = new JObject
            {
                ["action"] = "ExtractSubroutine",
                ["target"] = "MyProcedure",
                ["code"] = "&Total = 0\r\n&Count = 0",
                ["subroutineName"] = "ResetCounters",
                ["dryRun"] = true
            };
            var routed = router.ConvertToolCall("genexus_refactor", args);
            Assert.NotNull(routed);
            var jo = JObject.FromObject(routed!);
            Assert.Equal("Refactor", jo["module"]!.ToString());
            Assert.Equal("ExtractSubroutine", jo["action"]!.ToString());
            Assert.Equal("MyProcedure", jo["target"]!.ToString());
            Assert.True(jo["dryRun"]!.Value<bool>());
            var payload = JObject.Parse(jo["payload"]!.ToString());
            Assert.Equal("&Total = 0\r\n&Count = 0", payload["code"]!.ToString());
            Assert.Equal("ResetCounters", payload["subroutineName"]!.ToString());
        }
    }
}
