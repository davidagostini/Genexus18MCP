using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ModernHttpSessionIsolationTests
    {
        [Fact]
        public async System.Threading.Tasks.Task Sessionless_request_does_not_consume_shared_http_selection()
        {
            const string sharedModernId = "http-modern";
            Program.SetSessionSelectedKb(sharedModernId, "other-client-kb");
            try
            {
                var request = JObject.Parse(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"genexus_whoami\",\"arguments\":{}}}");

                var response = await Program.ProcessMcpRequest(
                    request,
                    sharedModernId,
                    sessionContextEnabled: false);

                var result = Assert.IsType<JObject>(response!["result"]);
                var payload = JObject.Parse(result["content"]![0]!["text"]!.ToString());
                var selected = payload["kb"]?["selected"];
                Assert.True(selected == null || selected.Type == JTokenType.Null);
            }
            finally
            {
                Program.ClearSessionSelectedKb(sharedModernId);
            }
        }
    }
}
