using System;
using System.IO;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    [Collection("Gateway route state")]
    public sealed class Issue215ReloadContractTests
    {
        [Fact]
        public async Task ForcedHardReload_IsRejectedBeforeWorkerReset()
        {
            using var fixture = RouteFixture.Create();
            string sessionId = "issue215-" + Guid.NewGuid().ToString("N");
            Program.SetSessionSelectedKb(sessionId, "orders", @"C:\KB\Orders");

            try
            {
                var response = await Program.ProcessMcpRequest(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "issue215-force-hard",
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "genexus_worker_reload",
                        ["arguments"] = new JObject
                        {
                            ["force"] = true,
                            ["mode"] = "hard",
                            ["sourceDir"] = @"C:\Build\Worker"
                        }
                    }
                }, sessionId);

                var payload = ExtractPayload(response!);
                Assert.True(response!["result"]?["isError"]?.Value<bool>());
                Assert.Equal("ReloadForceHardUnsupported", payload["error"]?["code"]?.ToString());
                Assert.Equal("none", payload["error"]?["scope"]?.ToString());
                Assert.False(payload["error"]?["sourceDirApplied"]?.Value<bool>());
                Assert.Equal("active", Program.GetSessionLeaseState(sessionId));
            }
            finally
            {
                Program.ClearSessionSelectedKb(sessionId);
            }
        }

        [Fact]
        public void ForceReloadHelp_DeclaresGlobalScopeAndSafeHardPath()
        {
            string help = ToolHelpCatalog.Get("genexus_worker_reload") ?? string.Empty;

            Assert.Contains("all open Workers", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ReloadForceHardUnsupported", help, StringComparison.Ordinal);
            Assert.Contains("alias", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mode=hard", help, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject ExtractPayload(JObject response)
        {
            return JObject.Parse(response["result"]!["content"]![0]!["text"]!.ToString());
        }

        private sealed class RouteFixture : IDisposable
        {
            private readonly IDisposable _state;
            private readonly string _directory;

            private RouteFixture(IDisposable state, string directory)
            {
                _state = state;
                _directory = directory;
            }

            internal static RouteFixture Create()
            {
                string directory = Path.Combine(Path.GetTempPath(), "gxmcp-issue215-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string configPath = Path.Combine(directory, "config.json");
                File.WriteAllText(configPath, "{}");
                var config = new Configuration
                {
                    Environment = new EnvironmentConfig
                    {
                        ResolutionPolicy = "strict",
                        KBs =
                        {
                            new KbEntry { Alias = "orders", Path = "C:/KB/Orders" },
                            new KbEntry { Alias = "customer", Path = "C:/KB/Customer" }
                        }
                    }
                };
                return new RouteFixture(Program.ConfigureRouteStateForTest(config, configPath), directory);
            }

            public void Dispose()
            {
                Program.ResetWorkerLifecycleForTest();
                _state.Dispose();
                try { Directory.Delete(_directory, true); } catch { }
            }
        }
    }
}
