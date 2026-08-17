using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ConfigurationParsingTests
    {
        [Fact]
        public void ParseConfig_LegacyKbPath_MigratesToKbsAndDefaultKb()
        {
            // issue #28 item 6: legacy KBPath is migrated ONLY when it points at a real KB
            // (a dir containing a .gxw / KnowledgeBase.Connection). Point KBPath at a temp
            // dir carrying a .gxw sentinel so the migration path is exercised.
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string kbDir = Path.Combine(tempDir, "LegacyDemo");
            Directory.CreateDirectory(kbDir);
            File.WriteAllText(Path.Combine(kbDir, "LegacyDemo.gxw"), "");
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                var json = "{ \"Environment\": { \"KBPath\": " + System.Text.Json.JsonSerializer.Serialize(kbDir) + " } }";
                File.WriteAllText(configPath, json);

                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Environment);
                Assert.NotNull(cfg.Environment!.KBs);
                var single = Assert.Single(cfg.Environment.KBs);
                Assert.Equal("legacydemo", single.Alias);
                Assert.Equal(kbDir, single.Path);
                Assert.Equal("legacydemo", cfg.Environment.DefaultKb);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_PlaceholderKbPath_IsNotMigrated()
        {
            // issue #28 item 6: the shipped fallback config carries a placeholder KBPath
            // (an empty scaffold with no .gxw / KnowledgeBase.Connection). It must NOT be
            // migrated into a phantom DefaultKb that auto-opens alongside the real KB.
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string placeholderDir = Path.Combine(tempDir, "YourKB"); // exists but no .gxw
            Directory.CreateDirectory(placeholderDir);
            string missingDir = Path.Combine(tempDir, "DoesNotExist");
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                foreach (var kbPath in new[] { placeholderDir, missingDir })
                {
                    var json = "{ \"Environment\": { \"KBPath\": " + System.Text.Json.JsonSerializer.Serialize(kbPath) + " } }";
                    File.WriteAllText(configPath, json);

                    var cfg = ParseConfig(configPath);

                    Assert.NotNull(cfg.Environment);
                    // No KBs synthesised, no phantom DefaultKb.
                    Assert.True(cfg.Environment!.KBs == null || cfg.Environment.KBs.Count == 0,
                        $"Expected no migrated KBs for placeholder '{kbPath}'");
                    Assert.True(string.IsNullOrEmpty(cfg.Environment.DefaultKb),
                        $"Expected no DefaultKb for placeholder '{kbPath}'");
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_AppliesEnvOverrides_AndPromotesActiveKb()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            string? oldPort = Environment.GetEnvironmentVariable("GX_MCP_PORT");
            string? oldStdio = Environment.GetEnvironmentVariable("GX_MCP_STDIO");
            try
            {
                var json = @"{
  ""Server"": {
    ""HttpPort"": 5000,
    ""McpStdio"": true
  },
  ""Environment"": {
    ""DefaultKb"": """",
    ""ActiveKb"": ""from_cli"",
    ""KBs"": {
      ""from_cli"": ""C:/KBs/FromCli""
    }
  }
}";
                File.WriteAllText(configPath, json);

                Environment.SetEnvironmentVariable("GX_MCP_PORT", "7711");
                Environment.SetEnvironmentVariable("GX_MCP_STDIO", "false");
                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Server);
                Assert.Equal(7711, cfg.Server!.HttpPort);
                Assert.False(cfg.Server.McpStdio);

                Assert.NotNull(cfg.Environment);
                Assert.Equal("from_cli", cfg.Environment!.DefaultKb);
                var single = Assert.Single(cfg.Environment.KBs);
                Assert.Equal("from_cli", single.Alias);
                Assert.Equal("C:/KBs/FromCli", single.Path);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GX_MCP_PORT", oldPort);
                Environment.SetEnvironmentVariable("GX_MCP_STDIO", oldStdio);
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void ParseConfig_SingleDeclaredKb_WithoutDefault_AutoPromotesToDefaultKb()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gxmcp-gw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string configPath = Path.Combine(tempDir, "config.json");
            try
            {
                var json = @"{
  ""Environment"": {
    ""KBs"": {
      ""mykb"": ""C:/KBs/MyKb""
    }
  }
}";
                File.WriteAllText(configPath, json);
                var cfg = ParseConfig(configPath);

                Assert.NotNull(cfg.Environment);
                Assert.Equal("mykb", cfg.Environment!.DefaultKb);
                var single = Assert.Single(cfg.Environment.KBs);
                Assert.Equal("mykb", single.Alias);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static Configuration ParseConfig(string path)
        {
            var method = typeof(Configuration).GetMethod("ParseConfig", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            try
            {
                var cfg = method!.Invoke(null, new object[] { path }) as Configuration;
                Assert.NotNull(cfg);
                return cfg!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }
    }
}
