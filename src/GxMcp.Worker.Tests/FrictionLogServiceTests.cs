using System;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class FrictionLogServiceTests
    {
        [Fact]
        public void AppendCore_WritesJsonlEntry()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                var json = JObject.Parse(FrictionLogService.AppendCore(tmpKb, "genexus_edit", "patch failed", "warn"));

                Assert.Equal("ok", (string)json["status"]!);
                string path = (string)json["result"]!["path"]!;
                Assert.True(File.Exists(path));
                var lines = File.ReadAllLines(path);
                Assert.Single(lines);
                var entry = JObject.Parse(lines[0]);
                Assert.Equal("genexus_edit", (string)entry["tool"]!);
                Assert.Equal("patch failed", (string)entry["message"]!);
                Assert.Equal("warn", (string)entry["severity"]!);
                Assert.NotNull(entry["atUtc"]);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void TailCore_ReturnsLastN_InChronologicalOrder()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                for (int i = 0; i < 5; i++)
                {
                    FrictionLogService.AppendCore(tmpKb, "tool" + i, "msg" + i, "info");
                }
                var json = JObject.Parse(FrictionLogService.TailCore(tmpKb, 3));

                Assert.Equal("ok", (string)json["status"]!);
                var entries = (JArray)json["result"]!["entries"]!;
                Assert.Equal(3, entries.Count);
                // Chronological: last 3 should be tool2, tool3, tool4
                Assert.Equal("tool2", (string)((JObject)entries[0])["tool"]!);
                Assert.Equal("tool4", (string)((JObject)entries[2])["tool"]!);
                Assert.Equal(5, (int)json["result"]!["total"]!);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void TailCore_NoFile_ReturnsEmptyArray()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                var json = JObject.Parse(FrictionLogService.TailCore(tmpKb, 10));

                Assert.Equal("ok", (string)json["status"]!);
                Assert.Empty((JArray)json["result"]!["entries"]!);
                Assert.Equal(0, (int)json["result"]!["total"]!);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void AppendCore_MissingMessage_ReturnsError()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpKb);
                var json = JObject.Parse(FrictionLogService.AppendCore(tmpKb, "tool", "", "info"));
                Assert.Equal("error", (string)json["status"]!);
                Assert.Equal("MissingMessage", (string)json["error"]!["code"]!);
            }
            finally
            {
                try { Directory.Delete(tmpKb, recursive: true); } catch { }
            }
        }

        [Fact]
        public void RotateFrictionLog_KeepsNewestLinesOnly()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp_fric_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "friction.jsonl");
                File.WriteAllLines(path, new[] { "l0", "l1", "l2", "l3", "l4" });

                Assert.Equal(0, FrictionLogService.RotateFrictionLog(path, 10));
                Assert.Equal(5, File.ReadAllLines(path).Length);

                Assert.Equal(2, FrictionLogService.RotateFrictionLog(path, 2));
                Assert.Equal(new[] { "l3", "l4" }, File.ReadAllLines(path));

                Assert.Equal(0, FrictionLogService.RotateFrictionLog(
                    Path.Combine(dir, "missing.jsonl"), 2));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ResolveFrictionLogMaxLines_DefaultsAndDisables()
        {
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_FRICTION_LOG_MAX_LINES", null);
                Assert.Equal(5000, FrictionLogService.ResolveFrictionLogMaxLines());

                Environment.SetEnvironmentVariable("GXMCP_FRICTION_LOG_MAX_LINES", "100");
                Assert.Equal(100, FrictionLogService.ResolveFrictionLogMaxLines());

                Environment.SetEnvironmentVariable("GXMCP_FRICTION_LOG_MAX_LINES", "0");
                Assert.Equal(int.MaxValue, FrictionLogService.ResolveFrictionLogMaxLines());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_FRICTION_LOG_MAX_LINES", null);
            }
        }
    }
}
