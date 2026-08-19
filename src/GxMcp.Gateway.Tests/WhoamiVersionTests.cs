using System;
using System.IO;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Tests that read or mutate Program._lastKnownIndexState (a process-wide static mirror)
    // must not run in parallel with each other, or one test's transient status value leaks
    // into another's assertions. Membership in this collection serializes them.
    [CollectionDefinition("IndexStateMirror", DisableParallelization = true)]
    public sealed class IndexStateMirrorCollection { }

    [Collection("IndexStateMirror")]
    public class WhoamiVersionTests
    {
        [Fact]
        public void DetectGeneXusVersion_ReadsVersionTxt()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx-version-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "version.txt"), "18.0.4\nOther line");
                string? detected = Program.DetectGeneXusVersion(tmp);
                Assert.Equal("18.0.4", detected);
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void DetectGeneXusVersion_ReturnsNullWhenMissing()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx-version-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Assert.Null(Program.DetectGeneXusVersion(tmp));
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void DetectGeneXusVersion_ReturnsNullForNullOrEmptyPath()
        {
            Assert.Null(Program.DetectGeneXusVersion(null));
            Assert.Null(Program.DetectGeneXusVersion(""));
            Assert.Null(Program.DetectGeneXusVersion("   "));
        }

        [Fact]
        public void DetectGeneXusVersion_AcceptsVersionWithCapitalV()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gx-version-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "Version.txt"), "18.1.0");
                Assert.Equal("18.1.0", Program.DetectGeneXusVersion(tmp));
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void BuildWhoamiPayload_ShapeIsStable_WhenNoConfig()
        {
            var payload = Program.BuildWhoamiPayload();
            Assert.NotNull(payload["connected"]);
            Assert.NotNull(payload["kb"]);
            Assert.NotNull(payload["geneXus"]);
            Assert.NotNull(payload["config"]);
            Assert.NotNull(payload["mcp"]);
            Assert.NotNull(payload["mcp"]?["serverVersion"]);
            Assert.NotNull(payload["mcp"]?["protocolVersion"]);
            Assert.NotNull(payload["geneXus"]?["supportedMajor"]);
            Assert.Equal("18", payload["geneXus"]?["supportedMajor"]?.ToString());
        }

        [Fact]
        public void Whoami_ExposesMultiKbSelectionContext()
        {
            var payload = Program.BuildWhoamiPayload();
            var kb = Assert.IsType<Newtonsoft.Json.Linq.JObject>(payload["kb"]);

            Assert.NotNull(kb["active"]);
            Assert.NotNull(kb["openKbs"]);
            Assert.NotNull(kb["knownKbs"]);
            Assert.NotNull(kb["declaredKbs"]);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Array, kb["openKbs"]!.Type);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Array, kb["knownKbs"]!.Type);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Array, kb["declaredKbs"]!.Type);
        }

        [Fact]
        public void Whoami_Exposes_the_session_selected_alias_separately()
        {
            const string sessionId = "whoami-session-test";
            Program.SetSessionSelectedKb(sessionId, "orders");
            try
            {
                var payload = Program.BuildWhoamiPayload(false, sessionId);
                var kb = Assert.IsType<Newtonsoft.Json.Linq.JObject>(payload["kb"]);

                Assert.Equal("orders", kb["selected"]?.ToString());
            }
            finally
            {
                Program.ClearSessionSelectedKb(sessionId);
            }
        }

        // v2.3.8 Task 1.2: whoami surfaces index readiness so the agent can know
        // whether it should call `lifecycle action=index` before relying on
        // `search_source` / `analyze` results.
        [Fact]
        public void Whoami_IncludesIndexBlock()
        {
            var payload = Program.BuildWhoamiPayload();
            var index = payload["index"] as Newtonsoft.Json.Linq.JObject;
            Assert.NotNull(index);

            var status = index!["status"]?.ToString();
            Assert.Contains(status, new[] { "Cold", "Reindexing", "Ready" });

            var totalObjects = index["totalObjects"];
            Assert.NotNull(totalObjects);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Integer, totalObjects!.Type);

            // Optional fields must at least be present as JSON keys (may be null).
            Assert.True(index.ContainsKey("lastIndexedAt"));
            Assert.True(index.ContainsKey("progress"));
            Assert.True(index.ContainsKey("etaMs"));
        }

        // v2.3.8 Task 1.2: whoami must reflect worker-reported index state when one is
        // available. Unit tests don't spin up a real WorkerPool, so we exercise the
        // gateway-side fallback path via UpdateLastKnownIndexState: the live fetch in
        // BuildWhoamiPayloadAsync gracefully falls back to this cached snapshot when
        // the worker is unreachable, which is the same code-path that serves stale
        // reads after a worker outage. End-to-end coverage with a real worker
        // round-trip is tracked under Task 8.1 (end-to-end smoke test).
        [Fact]
        public async System.Threading.Tasks.Task Whoami_IndexBlock_ReflectsWorkerState()
        {
            var lastIndexed = new DateTime(2026, 5, 15, 10, 30, 0, DateTimeKind.Utc);
            Program.UpdateLastKnownIndexState("Ready", 42, lastIndexed, null, null);

            var payload = await Program.BuildWhoamiPayloadAsync();
            var index = payload["index"] as Newtonsoft.Json.Linq.JObject;
            Assert.NotNull(index);
            Assert.Equal("Ready", index!["status"]?.ToString());
            Assert.Equal(42, index["totalObjects"]?.ToObject<int>());
            Assert.Equal(lastIndexed.ToString("o"), index["lastIndexedAt"]?.ToString());

            // Reset so we don't pollute other tests that rely on default Cold/0.
            Program.UpdateLastKnownIndexState("Cold", 0, null, null, null);
        }
    }
}
