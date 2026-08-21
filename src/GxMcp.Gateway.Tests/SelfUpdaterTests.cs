using System;
using System.IO;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Self-updater (corporate fixed-path Squirrel-style update). The file-swap and
    // download paths mutate the real install dir / network, so they aren't unit
    // tested here; we cover the pure decision pieces: staged-payload validation
    // (what makes a downloaded tree safe to apply) and version comparison (which
    // gates both staging and apply).
    public class SelfUpdaterTests
    {
        [Fact]
        public void StagedPayloadValid_RequiresBothBinaries()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-staged-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "worker"));

                Assert.False(SelfUpdater.StagedPayloadValid(root), "empty tree is not valid");

                File.WriteAllText(Path.Combine(root, "GxMcp.Gateway.exe"), "x");
                Assert.False(SelfUpdater.StagedPayloadValid(root), "gateway alone is not valid (worker missing)");

                File.WriteAllText(Path.Combine(root, "worker", "GxMcp.Worker.exe"), "x");
                Assert.True(SelfUpdater.StagedPayloadValid(root), "both binaries present => valid");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void StagedPayloadValid_FalseForMissingDir()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-staged-missing-" + Guid.NewGuid().ToString("N"));
            Assert.False(SelfUpdater.StagedPayloadValid(root));
        }

        [Theory]
        [InlineData("2.8.4", "2.8.3", 1)]
        [InlineData("v2.9.0", "2.8.9", 1)]
        [InlineData("2.8.3", "2.8.3", 0)]
        [InlineData("2.8.2", "2.8.3", -1)]
        public void CompareSemver_GatesStageAndApply(string a, string b, int expectedSign)
        {
            int r = UpdateNotifier.CompareSemver(a, b);
            Assert.Equal(expectedSign, Math.Sign(r));
        }

        // End-to-end swap against a temp "install" dir: stage a newer build and
        // confirm ApplyStagedUpdate replaces the live files (gateway + worker +
        // sibling files), bumps version.txt, backs the old ones up, and clears
        // .staged. This exercises the real move/backup ordering — the only part not
        // covered is rename-self of the *running* exe (here it's an inert stub).
        [Fact]
        public void ApplyStagedUpdate_SwapsFiles_BumpsVersion_ClearsStaging()
        {
            string install = Path.Combine(Path.GetTempPath(), "gxmcp-apply-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(install, "worker"));
                File.WriteAllText(Path.Combine(install, "GxMcp.Gateway.exe"), "OLD-GW");
                File.WriteAllText(Path.Combine(install, "worker", "GxMcp.Worker.exe"), "OLD-WK");
                File.WriteAllText(Path.Combine(install, "tool_definitions.json"), "OLD-TOOLS");
                File.WriteAllText(Path.Combine(install, "version.txt"), "v2.8.3");

                string staged = Path.Combine(install, ".staged");
                Directory.CreateDirectory(Path.Combine(staged, "worker"));
                File.WriteAllText(Path.Combine(staged, "GxMcp.Gateway.exe"), "NEW-GW");
                File.WriteAllText(Path.Combine(staged, "worker", "GxMcp.Worker.exe"), "NEW-WK");
                File.WriteAllText(Path.Combine(staged, "tool_definitions.json"), "NEW-TOOLS");
                File.WriteAllText(Path.Combine(staged, "staged.json"), "{\"version\":\"2.8.4\"}");

                bool applied = SelfUpdater.ApplyStagedUpdate(install);

                Assert.True(applied, "apply should succeed");
                Assert.Equal("NEW-GW", File.ReadAllText(Path.Combine(install, "GxMcp.Gateway.exe")));
                Assert.Equal("NEW-WK", File.ReadAllText(Path.Combine(install, "worker", "GxMcp.Worker.exe")));
                Assert.Equal("NEW-TOOLS", File.ReadAllText(Path.Combine(install, "tool_definitions.json")));
                Assert.Equal("v2.8.4", File.ReadAllText(Path.Combine(install, "version.txt")));
                Assert.False(Directory.Exists(staged), ".staged should be cleared after apply");
                // The previous binaries are kept as .old-* backups.
                Assert.Contains(Directory.GetFiles(install), f => f.Contains(".old-"));
            }
            finally
            {
                try { Directory.Delete(install, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ApplyStagedUpdate_NoOp_WhenStagedNotNewer()
        {
            string install = Path.Combine(Path.GetTempPath(), "gxmcp-apply-noop-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(install, "worker"));
                File.WriteAllText(Path.Combine(install, "GxMcp.Gateway.exe"), "LIVE-GW");
                File.WriteAllText(Path.Combine(install, "worker", "GxMcp.Worker.exe"), "LIVE-WK");
                File.WriteAllText(Path.Combine(install, "version.txt"), "v2.8.4");

                string staged = Path.Combine(install, ".staged");
                Directory.CreateDirectory(Path.Combine(staged, "worker"));
                File.WriteAllText(Path.Combine(staged, "GxMcp.Gateway.exe"), "STALE-GW");
                File.WriteAllText(Path.Combine(staged, "worker", "GxMcp.Worker.exe"), "STALE-WK");
                File.WriteAllText(Path.Combine(staged, "staged.json"), "{\"version\":\"2.8.3\"}");

                bool applied = SelfUpdater.ApplyStagedUpdate(install);

                Assert.False(applied, "older staged version must not be applied");
                Assert.Equal("LIVE-GW", File.ReadAllText(Path.Combine(install, "GxMcp.Gateway.exe")));
                Assert.False(Directory.Exists(staged), "stale staged dir should be discarded");
            }
            finally
            {
                try { Directory.Delete(install, recursive: true); } catch { }
            }
        }

        // C4 regression: if moving the staged file in fails AFTER the live binary was
        // renamed to its .old-* backup, ReplaceFile must restore the backup (otherwise
        // the install is left with a MISSING binary) and rethrow.
        [Fact]
        public void ReplaceFile_RestoresBackup_WhenMoveOfNewFileFails()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp-replace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string dst = Path.Combine(dir, "GxMcp.Gateway.exe");
                File.WriteAllText(dst, "OLD");
                // src deliberately does not exist => File.Move(src, dst) throws after
                // dst has already been moved to its .old-* backup.
                string src = Path.Combine(dir, "staged", "GxMcp.Gateway.exe");

                Assert.Throws<FileNotFoundException>(() => SelfUpdater.ReplaceFile(src, dst, "20990101000000"));

                Assert.True(File.Exists(dst), "dst must be restored, never left missing");
                Assert.Equal("OLD", File.ReadAllText(dst));
                Assert.Empty(Directory.GetFiles(dir, "*.old-*"));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
