using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Sdk;

namespace GxMcp.Gateway.Tests
{
    [Trait("Category", "LiveE2E")]
    public sealed class OpenIssueLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness _harness;

        public OpenIssueLiveTests(LiveGatewayHarness harness)
        {
            _harness = harness;
        }

        public Task InitializeAsync() => _harness.InitializeAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveKbFact]
        public async Task CompileCheckDryRun_UsesPublishedTargetAndCallerControls()
        {
            var list = await _harness.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "Procedure",
                ["limit"] = 1
            }, timeoutMs: 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(list),
                "Procedure listing failed: " + _harness.DiagnosticsSummary());

            var listPayload = LiveGatewayHarness.ParseToolPayload(list);
            var entries = listPayload?["results"] as JArray ?? listPayload?["items"] as JArray;
            string? name = entries?.OfType<JObject>()
                .Select(item => item["name"]?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(name))
                throw SkipException.ForSkip("The live KB has no Procedure object to exercise compile_check.");
            string procedureName = name!;

            var response = await _harness.CallToolAsync("genexus_lifecycle", new JObject
            {
                ["action"] = "build",
                ["mode"] = "compile_check",
                ["target"] = "Procedure:" + procedureName,
                ["callers"] = false,
                ["callerCap"] = 1,
                ["buildPlanCap"] = 20,
                ["dryRun"] = true
            }, timeoutMs: 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(response),
                "compile_check dry-run failed: " + _harness.DiagnosticsSummary());

            var payload = LiveGatewayHarness.ParseToolPayload(response);
            var preview = payload?["result"]?["preview"] as JObject
                ?? payload?["preview"] as JObject;
            Assert.NotNull(preview);
            Assert.Equal("CompileCheck", preview!["action"]?.ToString());
            Assert.Equal("none", preview["includeCallees"]?.ToString());
            Assert.False(preview["callers"]?.ToObject<bool>());
            Assert.Equal(1, preview["callerCap"]?.ToObject<int>());
            Assert.Contains("Procedure:" + procedureName,
                preview["wouldBuild"]?.ToObject<string[]>() ?? Array.Empty<string>());
        }

        [LiveKbFact]
        public async Task ObjectDryRunsAndMissingReadsStayBoundedOnSupportedSdk()
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 10);
            foreach (var type in new[] { "Procedure", "SDT" })
            {
                var response = await _harness.CallToolAsync("genexus_create", new JObject
                {
                    ["action"] = "object",
                    ["type"] = type,
                    ["name"] = "McpIssue218" + type + suffix,
                    ["dryRun"] = true
                }, timeoutMs: 120_000);
                Assert.False(LiveGatewayHarness.IsToolError(response),
                    type + " dry-run failed: " + _harness.DiagnosticsSummary());
                var payload = LiveGatewayHarness.ParseToolPayload(response);
                Assert.Equal("DryRun", payload?["code"]?.ToString());
                Assert.False(payload?["result"]?["persisted"]?.ToObject<bool>() ?? true);
            }

            string missing = "McpIssue218Missing" + suffix;
            var read = await _harness.CallToolAsync("genexus_read", new JObject
            {
                ["name"] = missing
            }, timeoutMs: 120_000);
            var readPayload = LiveGatewayHarness.ParseToolPayload(read);
            Assert.True(LiveGatewayHarness.IsToolError(read));
            Assert.NotEqual("Internal", readPayload?["error"]?["code"]?.ToString());
            Assert.Contains("ObjectNotFound", readPayload?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty);

            var deletePreview = await _harness.CallToolAsync("genexus_delete_object", new JObject
            {
                ["name"] = missing,
                ["dryRun"] = true
            }, timeoutMs: 120_000);
            var deletePayload = LiveGatewayHarness.ParseToolPayload(deletePreview);
            Assert.True(LiveGatewayHarness.IsToolError(deletePreview));
            Assert.Contains("ObjectNotFound", deletePayload?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty);

            var whoami = await _harness.CallToolAsync("genexus_whoami", new JObject());
            string alias = LiveGatewayHarness.ParseToolPayload(whoami)?["kbAlias"]?.ToString() ?? "kbteste";
            var environments = await _harness.CallToolAsync("genexus_kb", new JObject
            {
                ["action"] = "list_environments",
                ["kb"] = alias
            }, timeoutMs: 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(environments),
                "Explicit-KB environment listing failed: " + _harness.DiagnosticsSummary());
        }

        [LiveKbFact]
        public async Task DoctorReturnsFreshGatewayTelemetryOnRepeatedCalls()
        {
            var first = LiveGatewayHarness.ParseToolPayload(
                await _harness.CallToolAsync("genexus_doctor", new JObject()));
            await Task.Delay(1100);
            var second = LiveGatewayHarness.ParseToolPayload(
                await _harness.CallToolAsync("genexus_doctor", new JObject()));
            var firstResult = first?["result"] as JObject;
            var secondResult = second?["result"] as JObject;

            Assert.NotNull(firstResult?["checkedAt"]);
            Assert.NotNull(secondResult?["checkedAt"]);
            DateTime firstCheckedAt = firstResult!["checkedAt"]!.Value<DateTime>();
            DateTime secondCheckedAt = secondResult!["checkedAt"]!.Value<DateTime>();
            Assert.True(secondCheckedAt > firstCheckedAt,
                $"Doctor checkedAt did not advance: first={firstCheckedAt:o}, second={secondCheckedAt:o}");
            Assert.Equal("gateway", secondResult["telemetry"]?["source"]?.ToString());
            Assert.True((secondResult["telemetry"]?["totalToolCalls"]?.ToObject<int>() ?? 0) > 0);
        }

        [LiveKbFact]
        public async Task ReadBlob_OverwriteTrue_ReplacesExistingFileAndReportsHash()
        {
            var list = await _harness.CallToolAsync("genexus_list_objects", new JObject
            {
                ["typeFilter"] = "File",
                ["limit"] = 20
            }, timeoutMs: 120_000);
            Assert.False(LiveGatewayHarness.IsToolError(list),
                "File listing failed: " + _harness.DiagnosticsSummary());

            var listPayload = LiveGatewayHarness.ParseToolPayload(list);
            var entries = listPayload?["results"] as JArray ?? listPayload?["items"] as JArray;
            string? name = entries?.OfType<JObject>()
                .Select(item => item["name"]?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(name))
                throw SkipException.ForSkip("The live KB has no File object to exercise WikiBlob export.");

            string fileName = name!;

            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-read-blob-" + Guid.NewGuid().ToString("N"));
            string outputPath = Path.Combine(tempDir, "blob.bin");
            byte[] sentinel = { 0x73, 0x65, 0x6e, 0x74, 0x69, 0x6e, 0x65, 0x6c };
            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllBytes(outputPath, sentinel);

                var response = await _harness.CallToolAsync("genexus_io", new JObject
                {
                    ["action"] = "read_blob",
                    ["name"] = fileName,
                    ["type"] = "File",
                    ["part"] = "WikiBlob",
                    ["outputPath"] = outputPath,
                    ["overwrite"] = true
                }, timeoutMs: 120_000);
                var payload = LiveGatewayHarness.ParseToolPayload(response);
                if (LiveGatewayHarness.IsToolError(response))
                {
                    string code = payload?["error"]?["code"]?.ToString()
                        ?? payload?["code"]?.ToString()
                        ?? string.Empty;
                    if (string.Equals(code, "BlobUnavailable", StringComparison.OrdinalIgnoreCase))
                        throw SkipException.ForSkip("The selected File does not expose readable WikiBlob content.");
                    Assert.Fail("read_blob overwrite=true failed: " + (payload?.ToString(Newtonsoft.Json.Formatting.None) ?? "<null>"));
                }

                Assert.NotNull(payload);
                var result = payload!["result"] as JObject ?? payload;
                byte[] written = File.ReadAllBytes(outputPath);
                Assert.False(sentinel.SequenceEqual(written), "overwrite=true left the sentinel file untouched.");
                Assert.Equal(written.LongLength, result["bytes"]?.ToObject<long>());
                Assert.Equal(Sha256(written), result["sha256"]?.ToString());

                string beforeRefusal = Sha256(written);
                var refusal = await _harness.CallToolAsync("genexus_io", new JObject
                {
                    ["action"] = "read_blob",
                    ["name"] = fileName,
                    ["type"] = "File",
                    ["part"] = "WikiBlob",
                    ["outputPath"] = outputPath,
                    ["overwrite"] = false
                }, timeoutMs: 120_000);
                var refusalPayload = LiveGatewayHarness.ParseToolPayload(refusal);
                Assert.True(LiveGatewayHarness.IsToolError(refusal),
                    "overwrite=false must reject an existing destination.");
                Assert.Equal("FileAlreadyExists", refusalPayload?["error"]?["code"]?.ToString()
                    ?? refusalPayload?["code"]?.ToString());
                Assert.Equal(beforeRefusal, Sha256(File.ReadAllBytes(outputPath)));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // The harness cleanup remains authoritative; do not hide test evidence.
                }
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(bytes))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }
}
