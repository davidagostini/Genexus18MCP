using System.Reflection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class WhoamiSharingModeTests
    {
        [Theory]
        [InlineData("isolated", "stdio-isolated", "stdio-isolated")]
        [InlineData("isolated", "http-shared", "stdio-isolated")]
        [InlineData("shared-host", "stdio-isolated", "shared-host")]
        public void RunningHealth_UsesWorkerSharingVocabularyIndependentOfGatewayMode(
            string sharingMode, string gatewayMode, string attachmentMode)
        {
            var config = new Configuration
            {
                GatewayMode = gatewayMode,
                Server = new ServerConfig { WorkerSharingMode = sharingMode }
            };
            using var state = Program.ConfigureRouteStateForTest(config, "whoami-sharing-test.json");
            var pool = (WorkerPool)typeof(Program)
                .GetField("_workerPool", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
            var kb = new KbHandle("sharing-test", "C:/fake/whoami-sharing");
            var worker = new WorkerProcess(config, kb);
            worker.HandleWorkerRpcResponseForTest(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/worker/sdk_ready\"}");
            pool.RegisterForTest(kb, worker: worker);

            var health = (JObject)typeof(Program)
                .GetMethod("BuildHonestWorkerHealth", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[]
                {
                    new JObject { ["kb"] = new JObject { ["active"] = kb.NormalizedAlias } },
                    false
                })!;
            var block = (JObject)typeof(Program)
                .GetMethod("BuildWorkerBlock", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!;

            Assert.Equal("running", health["status"]?.ToString());
            Assert.Equal(sharingMode, health["sharingMode"]?.ToString());
            Assert.Equal(sharingMode, block["sharingMode"]?.ToString());
            Assert.Equal(attachmentMode, block["diagnostics"]?["mode"]?.ToString());
            Assert.Equal(sharingMode == "shared-host", health.ContainsKey("attachmentId"));
        }
    }
}
