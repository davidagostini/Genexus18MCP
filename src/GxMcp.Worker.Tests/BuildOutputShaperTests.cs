using System;
using System.IO;
using System.Linq;
using System.Text;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// v2.6.6 Stream C FR#22 — MSBuild output shaping. The shaper caps the
    /// envelope to head + tail so a 200 KB build log never blows the JSON-RPC
    /// frame, while the full content is pinned to disk for retrieval.
    /// </summary>
    public class BuildOutputShaperTests
    {
        [Fact]
        public void Shape_LargeInput_ReturnsHeadTailHintAndCountsDroppedLines()
        {
            // Arrange — build a payload comfortably larger than HeadBytes + TailBytes
            // so the elided middle is non-empty and contains a known newline count.
            int headBytes = BuildOutputShaper.HeadBytes;
            int tailBytes = BuildOutputShaper.TailBytes;
            var sb = new StringBuilder();
            // Head padding (no newlines so dropped_lines comes solely from the middle).
            sb.Append('H', headBytes);
            // Middle slice — 500 single-character lines = 500 newlines.
            for (int i = 0; i < 500; i++) sb.Append("M\n");
            // Tail padding to push the total over the cap.
            sb.Append('T', tailBytes + 16);
            string full = sb.ToString();

            // Act
            var shaped = BuildOutputShaper.Shape(full, totalLines: 5000, fullLogPath: @"C:\logs\build-abc.log");

            // Assert
            Assert.Equal(headBytes, shaped.head.Length);
            Assert.Equal(tailBytes, shaped.tail.Length);
            Assert.Equal(500, shaped.dropped_lines);
            Assert.Equal(5000, shaped.total_lines);
            Assert.Equal(@"C:\logs\build-abc.log", shaped.full_log_path);
            Assert.Contains("build-abc.log", shaped.hint);
        }

        [Fact]
        public void Shape_SmallInput_ReturnsFullContentInHeadAndEmptyTail()
        {
            // Arrange — payload under the cap stays whole; nothing gets elided.
            string full = "line1\nline2\nline3\n";

            // Act
            var shaped = BuildOutputShaper.Shape(full, totalLines: 3, fullLogPath: "log.txt");

            // Assert
            Assert.Equal(full, shaped.head);
            Assert.Equal(string.Empty, shaped.tail);
            Assert.Equal(0, shaped.dropped_lines);
            Assert.Equal(3, shaped.total_lines);
            Assert.Equal("log.txt", shaped.full_log_path);
        }

        [Fact]
        public void Shape_BoundaryInput_AtExactCap_StillFitsInHead()
        {
            // Arrange — exactly HeadBytes + TailBytes characters should NOT be elided
            // (the implementation uses `<=` so the boundary is inclusive).
            int capBytes = BuildOutputShaper.HeadBytes + BuildOutputShaper.TailBytes;
            string full = new string('x', capBytes);

            // Act
            var shaped = BuildOutputShaper.Shape(full, totalLines: 1, fullLogPath: "log.txt");

            // Assert
            Assert.Equal(full, shaped.head);
            Assert.Equal(string.Empty, shaped.tail);
            Assert.Equal(0, shaped.dropped_lines);
        }

        [Fact]
        public void Shape_NullInput_ReturnsEmptyHeadAndTail()
        {
            // Arrange + Act
            var shaped = BuildOutputShaper.Shape(null, totalLines: 0, fullLogPath: "log.txt");

            // Assert
            Assert.Equal(string.Empty, shaped.head);
            Assert.Equal(string.Empty, shaped.tail);
            Assert.Equal(0, shaped.dropped_lines);
            Assert.Equal("log.txt", shaped.full_log_path);
        }

        [Fact]
        public void Shape_EmptyInput_ReturnsEmptyHeadAndTail()
        {
            // Arrange + Act
            var shaped = BuildOutputShaper.Shape(string.Empty, totalLines: 0, fullLogPath: null);

            // Assert
            Assert.Equal(string.Empty, shaped.head);
            Assert.Equal(string.Empty, shaped.tail);
            Assert.Equal(0, shaped.dropped_lines);
            // Null full_log_path is allowed; hint surfaces the placeholder.
            Assert.Null(shaped.full_log_path);
            Assert.Contains("<unavailable>", shaped.hint);
        }

        [Fact]
        public void Shape_FullLogPath_PassedThroughUnchanged()
        {
            // Arrange
            string path = @"D:\some\weird path\with spaces\build-xyz.log";

            // Act
            var shapedSmall = BuildOutputShaper.Shape("tiny", 1, path);
            var shapedLarge = BuildOutputShaper.Shape(new string('z', BuildOutputShaper.HeadBytes + BuildOutputShaper.TailBytes + 100), 999, path);

            // Assert
            Assert.Equal(path, shapedSmall.full_log_path);
            Assert.Equal(path, shapedLarge.full_log_path);
            Assert.Contains(path, shapedSmall.hint);
        }

        // Log retention: only build-*.log files past the retain count are deleted
        // (newest kept); anything else in the dir and missing dirs are untouched.
        [Fact]
        public void SweepOldBuildLogs_KeepsNewestDeletesRest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GxLogSweep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                DateTime baseTime = DateTime.UtcNow.AddHours(-5);
                for (int i = 0; i < 5; i++)
                {
                    string p = Path.Combine(dir, "build-task" + i + ".log");
                    File.WriteAllText(p, "log" + i);
                    File.SetLastWriteTimeUtc(p, baseTime.AddHours(i));
                }
                File.WriteAllText(Path.Combine(dir, "keepme.txt"), "not-a-build-log");
                File.WriteAllText(Path.Combine(dir, "other.log"), "not-matching-prefix");

                int deleted = BuildOutputShaper.SweepOldBuildLogs(dir, retainCount: 2);

                Assert.Equal(3, deleted);
                Assert.True(File.Exists(Path.Combine(dir, "build-task4.log")));
                Assert.True(File.Exists(Path.Combine(dir, "build-task3.log")));
                Assert.False(File.Exists(Path.Combine(dir, "build-task2.log")));
                Assert.True(File.Exists(Path.Combine(dir, "keepme.txt")));
                Assert.True(File.Exists(Path.Combine(dir, "other.log")));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void SweepOldBuildLogs_MissingDirAndDisabledAreNoOps()
        {
            Assert.Equal(0, BuildOutputShaper.SweepOldBuildLogs(
                Path.Combine(Path.GetTempPath(), "GxNoSuchDir_" + Guid.NewGuid().ToString("N")), 2));
            Assert.Equal(0, BuildOutputShaper.SweepOldBuildLogs(null, 2));
            string dir = Path.Combine(Path.GetTempPath(), "GxLogSweep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "build-x.log"), "x");
                Assert.Equal(0, BuildOutputShaper.SweepOldBuildLogs(dir, 0));
                Assert.True(File.Exists(Path.Combine(dir, "build-x.log")));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void ResolveBuildLogRetainCount_DefaultsAndDisables()
        {
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_LOG_RETAIN_COUNT", null);
                Assert.Equal(50, BuildOutputShaper.ResolveBuildLogRetainCount());

                Environment.SetEnvironmentVariable("GXMCP_BUILD_LOG_RETAIN_COUNT", "7");
                Assert.Equal(7, BuildOutputShaper.ResolveBuildLogRetainCount());

                Environment.SetEnvironmentVariable("GXMCP_BUILD_LOG_RETAIN_COUNT", "0");
                Assert.Equal(int.MaxValue, BuildOutputShaper.ResolveBuildLogRetainCount());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_LOG_RETAIN_COUNT", null);
            }
        }
    }
}
