using System;
using System.IO;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AtomicFilePromoterTests
    {
        [Fact]
        public void Promote_OverwriteTrue_ReplacesExistingDestination()
        {
            string tempDir = CreateTempDirectory();
            try
            {
                string destination = Path.Combine(tempDir, "blob.bin");
                string staged = Path.Combine(tempDir, "blob.tmp");
                File.WriteAllText(destination, "old");
                File.WriteAllText(staged, "new");

                var result = AtomicFilePromoter.Promote(staged, destination, overwrite: true);

                Assert.Equal(AtomicFilePromotionResult.Replaced, result);
                Assert.Equal("new", File.ReadAllText(destination));
                Assert.False(File.Exists(staged));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void Promote_OverwriteFalse_WhenDestinationExists_PreservesBothFiles()
        {
            string tempDir = CreateTempDirectory();
            try
            {
                string destination = Path.Combine(tempDir, "blob.bin");
                string staged = Path.Combine(tempDir, "blob.tmp");
                File.WriteAllText(destination, "old");
                File.WriteAllText(staged, "new");

                var result = AtomicFilePromoter.Promote(staged, destination, overwrite: false);

                Assert.Equal(AtomicFilePromotionResult.DestinationExists, result);
                Assert.Equal("old", File.ReadAllText(destination));
                Assert.Equal("new", File.ReadAllText(staged));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void Promote_WhenDestinationIsMissing_MovesStagedFile()
        {
            string tempDir = CreateTempDirectory();
            try
            {
                string destination = Path.Combine(tempDir, "blob.bin");
                string staged = Path.Combine(tempDir, "blob.tmp");
                File.WriteAllText(staged, "new");

                var result = AtomicFilePromoter.Promote(staged, destination, overwrite: true);

                Assert.Equal(AtomicFilePromotionResult.Created, result);
                Assert.Equal("new", File.ReadAllText(destination));
                Assert.False(File.Exists(staged));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-promoter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Do not hide the assertion failure with best-effort test cleanup.
            }
        }
    }
}
