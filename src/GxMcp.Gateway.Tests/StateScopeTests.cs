using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class StateScopeTests
    {
        [Fact]
        public void ProcessScopeIdIsOpaqueAndStableForTheProcess()
        {
            var first = StateScope.ProcessScopeId;
            var second = StateScope.ProcessScopeId;

            Assert.Equal(first, second);
            Assert.Equal(32, first.ToString().Length);
            Assert.True(first.ToString().All(Uri.IsHexDigit));
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), first.ToString());
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar.ToString(), first.ToString());
        }

        [Fact]
        public void InvalidScopeIdsCannotBecomeDirectories()
        {
            Assert.Throws<ArgumentException>(() => StateScopeId.Parse(".."));
            Assert.Throws<ArgumentException>(() => StateScopeId.Parse("../other"));
            Assert.Throws<ArgumentException>(() => StateScopeId.Parse(new string('a', 31)));
            Assert.Throws<ArgumentException>(() => StateScopeId.Parse(new string('a', 33)));
        }

        [Fact]
        public void ScopeCreatesPrivateOperationalDirectoriesWithoutKbIdentity()
        {
            string baseDirectory = CreateTempDirectory();
            try
            {
                using var scope = StateScope.Create(baseDirectory);

                Assert.Equal(Path.Combine(baseDirectory, scope.Id.ToString()), scope.RootDirectory);
                Assert.True(Directory.Exists(scope.RootDirectory));
                Assert.True(Directory.Exists(scope.JournalDirectory));
                Assert.True(Directory.Exists(scope.RecoveryDirectory));
                Assert.True(Directory.Exists(scope.JobsDirectory));
                Assert.True(Directory.Exists(scope.LogsDirectory));
                Assert.DoesNotContain("knowledge-base", scope.RootDirectory, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DeleteDirectory(baseDirectory);
            }
        }

        [Fact]
        public void CleanupRemovesOnlyTheOwnedScope()
        {
            string baseDirectory = CreateTempDirectory();
            try
            {
                var first = StateScope.Create(baseDirectory, StateScopeId.Parse(new string('a', 32)));
                using var second = StateScope.Create(baseDirectory, StateScopeId.Parse(new string('b', 32)));
                string firstRoot = first.RootDirectory;
                string secondRoot = second.RootDirectory;

                first.Dispose();

                Assert.False(Directory.Exists(firstRoot));
                Assert.True(Directory.Exists(secondRoot));
            }
            finally
            {
                DeleteDirectory(baseDirectory);
            }
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gx-state-scope-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of test artifacts.
            }
        }
    }
}
