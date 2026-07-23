using System;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GxServerSyncServiceTests
    {
        private static string NewTmpKb()
        {
            string p = Path.Combine(Path.GetTempPath(), "gxmcp_gxsrv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(p);
            return p;
        }

        [Fact]
        public void Status_NoMetadata_ReturnsConnectedFalseWithHint()
        {
            string kb = NewTmpKb();
            try
            {
                var jo = JObject.Parse(GxServerSyncService.StatusEnvelope(kb, "MyKb"));
                Assert.Equal("ok", (string)jo["status"]);
                Assert.False((bool)jo["result"]!["connected"]);
                Assert.Equal("MyKb", (string)jo["result"]!["kbAlias"]);
                Assert.Contains("not connected", ((string)jo["result"]!["hint"]) ?? string.Empty);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Status_WithRepositoryGxs_ReturnsConnectedTrue()
        {
            string kb = NewTmpKb();
            try
            {
                string repoDir = Path.Combine(kb, "Repository");
                Directory.CreateDirectory(repoDir);
                File.WriteAllText(Path.Combine(repoDir, "Repository.gxs"), "<gxserver/>");

                var jo = JObject.Parse(GxServerSyncService.StatusEnvelope(kb, "MyKb"));
                Assert.Equal("ok", (string)jo["status"]);
                Assert.True((bool)jo["result"]!["connected"]);
                Assert.NotNull(jo["result"]!["detectedVia"]);
                Assert.Contains("Repository.gxs", (string)jo["result"]!["detectedVia"]);
                Assert.Contains("parsing pending", (string)jo["result"]!["note"]);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Pending_NoMetadata_ReturnsConnectedFalse()
        {
            string kb = NewTmpKb();
            try
            {
                var jo = JObject.Parse(GxServerSyncService.PendingEnvelope(kb));
                Assert.False((bool)jo["result"]!["connected"]);
                Assert.Null(jo["result"]!["objects"]);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void History_WithDotGxState_ReturnsEmptyHistoryArray()
        {
            string kb = NewTmpKb();
            try
            {
                string dot = Path.Combine(kb, ".gx");
                Directory.CreateDirectory(dot);
                File.WriteAllText(Path.Combine(dot, "gxserver-state.xml"), "<state/>");

                var jo = JObject.Parse(GxServerSyncService.HistoryEnvelope(kb, 25));
                Assert.True((bool)jo["result"]!["connected"]);
                Assert.NotNull(jo["result"]!["history"]);
                Assert.Equal(JTokenType.Array, jo["result"]!["history"].Type);
                Assert.Equal(25, (int)jo["result"]!["limit"]);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Ignored_NoMetadata_ReturnsConnectedFalse()
        {
            string kb = NewTmpKb();
            try
            {
                var jo = JObject.Parse(GxServerSyncService.IgnoredEnvelope(kb));
                Assert.Equal("GxServerIgnoredRetrieved", (string)jo["code"]);
                Assert.False((bool)jo["result"]!["connected"]);
                Assert.Null(jo["result"]!["commitIgnored"]);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Ignored_WithDotGxState_ReturnsEmptyIgnoredArrays()
        {
            string kb = NewTmpKb();
            try
            {
                string dot = Path.Combine(kb, ".gx");
                Directory.CreateDirectory(dot);
                File.WriteAllText(Path.Combine(dot, "gxserver-state.xml"), "<state/>");

                var jo = JObject.Parse(GxServerSyncService.IgnoredEnvelope(kb));
                Assert.True((bool)jo["result"]!["connected"]);
                Assert.Equal(JTokenType.Array, jo["result"]!["commitIgnored"].Type);
                Assert.Equal(JTokenType.Array, jo["result"]!["updateIgnored"].Type);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Run_IgnoredAction_IsAcceptedNotBadAction()
        {
            // action=ignored must route through the read path, not fall into BadAction.
            var svc = new GxServerSyncService(null);
            var jo = JObject.Parse(svc.Run(new JObject { ["action"] = "ignored" }));
            Assert.NotEqual("BadAction", (string)jo["error"]?["code"]);
            Assert.Equal("GxServerIgnoredRetrieved", (string)jo["code"]);
        }

        [Fact]
        public void Run_BadAction_ReturnsGracefulError()
        {
            var svc = new GxServerSyncService(null);
            var jo = JObject.Parse(svc.Run(new JObject { ["action"] = "bogus" }));
            Assert.Equal("error", (string)jo["status"]);
            Assert.Equal("BadAction", (string)jo["error"]?["code"]);
            Assert.Contains("bogus", (string)jo["error"]?["message"]);
        }
    }
}
