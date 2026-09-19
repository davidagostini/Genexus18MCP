using System;
using System.IO;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Unit tests for the WritePipeline helper class.
    /// Tests use a temp directory as a fake KB root (no SDK needed).
    /// </summary>
    public class WritePipelineTests
    {
        private static string NewKbDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp_wp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ----- AdvisoryLockCheck -----------------------------------------------

        [Fact]
        public void AdvisoryLockCheck_NoLockFile_ReturnsNull()
        {
            string kb = NewKbDir();
            try
            {
                var result = WritePipeline.AdvisoryLockCheck(kb, "Invoice", "Source", "agent-A", false);
                Assert.Null(result);
            }
            finally { Directory.Delete(kb, true); }
        }

        [Fact]
        public void AdvisoryLockCheck_SameOwner_ReturnsNull()
        {
            string kb = NewKbDir();
            try
            {
                // Acquire lock as agent-A
                MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Source", "agent-A", 300);

                var result = WritePipeline.AdvisoryLockCheck(kb, "Invoice", "Source", "agent-A", false);
                Assert.Null(result); // same owner — no conflict
            }
            finally { Directory.Delete(kb, true); }
        }

        [Fact]
        public void AdvisoryLockCheck_DifferentOwner_ReturnsError()
        {
            string kb = NewKbDir();
            try
            {
                // Acquire lock as agent-A
                MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Source", "agent-A", 300);

                var result = WritePipeline.AdvisoryLockCheck(kb, "Invoice", "Source", "agent-B", false);
                Assert.NotNull(result);
                Assert.Equal("Error", (string)result["status"]);
                Assert.Equal("TargetLockedByOtherAgent", (string)result["code"]);
                Assert.Equal("agent-A", (string)result["lockHolder"]);
            }
            finally { Directory.Delete(kb, true); }
        }

        [Fact]
        public void AdvisoryLockCheck_DifferentOwner_Force_ReturnsNull()
        {
            string kb = NewKbDir();
            try
            {
                MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Source", "agent-A", 300);

                var result = WritePipeline.AdvisoryLockCheck(kb, "Invoice", "Source", "agent-B", force: true);
                Assert.Null(result); // force=true bypasses lock
            }
            finally { Directory.Delete(kb, true); }
        }

        [Fact]
        public void AdvisoryLockCheck_ExpiredLock_ReturnsNull()
        {
            string kb = NewKbDir();
            try
            {
                // Write an already-expired lock file manually
                string locksDir = Path.Combine(kb, ".gx", "locks");
                Directory.CreateDirectory(locksDir);
                string lockPath = Path.Combine(locksDir, "Invoice__Source.lock");
                var expiredEntry = new JObject
                {
                    ["ownerId"] = "agent-A",
                    ["atUtc"] = DateTime.UtcNow.AddSeconds(-600).ToString("o"),
                    ["ttlSec"] = 300,
                    ["target"] = "Invoice",
                    ["part"] = "Source"
                };
                File.WriteAllText(lockPath, expiredEntry.ToString(Newtonsoft.Json.Formatting.None));

                var result = WritePipeline.AdvisoryLockCheck(kb, "Invoice", "Source", "agent-B", false);
                Assert.Null(result); // expired — not blocking
            }
            finally { Directory.Delete(kb, true); }
        }

        [Fact]
        public void AdvisoryLockCheck_NullKbPath_ReturnsNull()
        {
            var result = WritePipeline.AdvisoryLockCheck(null, "Invoice", "Source", "agent-B", false);
            Assert.Null(result);
        }

        [Fact]
        public void AdvisoryLockCheck_NullOwnerId_ReturnsNull()
        {
            string kb = NewKbDir();
            try
            {
                MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Source", "agent-A", 300);
                var result = WritePipeline.AdvisoryLockCheck(kb, "Invoice", "Source", null, false);
                Assert.Null(result); // no ownerId → check skipped
            }
            finally { Directory.Delete(kb, true); }
        }

        [Fact]
        public void WriteContext_RestoresPreviousOwnerAndForceState()
        {
            Assert.Null(WritePipeline.CurrentOwnerId);
            Assert.False(WritePipeline.CurrentForce);
            using (WritePipeline.UseWriteContext("gateway-a", true))
            {
                Assert.Equal("gateway-a", WritePipeline.CurrentOwnerId);
                Assert.True(WritePipeline.CurrentForce);
                using (WritePipeline.UseWriteContext("gateway-b", false))
                {
                    Assert.Equal("gateway-b", WritePipeline.CurrentOwnerId);
                    Assert.False(WritePipeline.CurrentForce);
                }
                Assert.Equal("gateway-a", WritePipeline.CurrentOwnerId);
                Assert.True(WritePipeline.CurrentForce);
            }
            Assert.Null(WritePipeline.CurrentOwnerId);
            Assert.False(WritePipeline.CurrentForce);
        }

        [Fact]
        public void AdvisoryLockCheck_CorruptLock_FailsClosed()
        {
            string kb = NewKbDir();
            try
            {
                string locksDir = Path.Combine(kb, ".gx", "locks");
                Directory.CreateDirectory(locksDir);
                File.WriteAllText(Path.Combine(locksDir, "Invoice__Source.lock"), "{not-json");

                var result = WritePipeline.AdvisoryLockCheck(kb, "Invoice", "Source", "gateway-a", false);
                Assert.NotNull(result);
                Assert.Equal("LockCheckFailed", (string)result["code"]);
            }
            finally { Directory.Delete(kb, true); }
        }

        [Fact]
        public void HasActiveOwnedLock_DistinguishesOwnerAndExpiry()
        {
            string kb = NewKbDir();
            try
            {
                MultiAgentLockService.DispatchCore(kb, "acquire", "Invoice", "Source", "agent-A", 300);

                Assert.True(WritePipeline.HasActiveOwnedLock(kb, "Invoice", "Source", "agent-A"));
                Assert.False(WritePipeline.HasActiveOwnedLock(kb, "Invoice", "Source", "agent-B"));
            }
            finally { Directory.Delete(kb, true); }
        }

        // ----- NoteWrite -------------------------------------------------------

        [Fact]
        public void NoteWrite_UpdatesWriteTracker()
        {
            // NoteWrite delegates to WriteService.NotePerTargetWrite which updates
            // the static _lastWriteAtUtc map. We can verify via WasTargetWrittenSince.
            string target = "NoteWriteTest_" + Guid.NewGuid().ToString("N");
            var before = DateTime.UtcNow.AddSeconds(-1);
            WritePipeline.NoteWrite(target);
            Assert.True(WriteService.WasTargetWrittenSince(target, before));
        }
    }
}
