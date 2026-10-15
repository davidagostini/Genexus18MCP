using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Issue #499: `genexus_wire` declares itself read-only (`readOnlyHint: true`,
    // "Mutating actions: none") but did two things the contract does not admit:
    // it wrote `.gxwire_cache.bin` into the directory it was asked to analyse,
    // and it walked a native Knowledge Base's folder - where there is no object
    // source on disk to walk - and reported the generated output it found as a
    // graph.
    public class GxwireCacheLocationTests
    {
        [Fact]
        public void Index_LivesOutsideTheAnalysedDirectory()
        {
            using var dir = new TempDir();

            var cachePath = GxwireCacheLocation.Resolve(dir.Path);

            Assert.False(
                cachePath.StartsWith(dir.Path, StringComparison.OrdinalIgnoreCase),
                "the index must not be written into the directory being analysed");
            Assert.True(Directory.Exists(Path.GetDirectoryName(cachePath)));
        }

        [Fact]
        public void Index_DoesNotUseTheInTreeDotfileName()
        {
            using var dir = new TempDir();

            // The analysed directory must not receive a .gxwire_cache.bin.
            Assert.NotEqual(
                Path.Combine(dir.Path, ".gxwire_cache.bin"),
                GxwireCacheLocation.Resolve(dir.Path));
        }

        [Fact]
        public void SameDirectory_AlwaysResolvesToTheSameIndex()
        {
            using var a = new TempDir();

            // Twice, and once with a trailing separator: the key must not depend
            // on how the caller spelled the path.
            Assert.Equal(GxwireCacheLocation.Resolve(a.Path), GxwireCacheLocation.Resolve(a.Path));
            Assert.Equal(
                GxwireCacheLocation.Resolve(a.Path),
                GxwireCacheLocation.Resolve(a.Path + Path.DirectorySeparatorChar));
        }

        [Fact]
        public void DifferentDirectories_NeverShareAnIndex()
        {
            using var a = new TempDir();
            using var b = new TempDir();

            Assert.NotEqual(GxwireCacheLocation.Resolve(a.Path), GxwireCacheLocation.Resolve(b.Path));
        }

        [Fact]
        public void Key_IsCaseAndSeparatorInsensitiveOnWindows()
        {
            // Windows paths are case-insensitive and accept either separator, so
            // two spellings of one directory must not produce two indexes.
            Assert.Equal(
                GxwireCacheLocation.StableKey(@"C:\KBs\KBTeste"),
                GxwireCacheLocation.StableKey(@"c:/kbs/kbteste"));
        }

        [Fact]
        public void Key_IsFilesystemSafe()
        {
            // The analysed path is never spelled out, so a Knowledge Base whose
            // directory carries a character a file name cannot hold still works.
            using var dir = new TempDir();

            var name = Path.GetFileNameWithoutExtension(GxwireCacheLocation.Resolve(dir.Path));

            Assert.Equal(32, name.Length);
            foreach (char c in name)
            {
                Assert.True(
                    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    "the cache file name must be plain lowercase hex: " + name);
            }
        }

        [Fact]
        public void Index_ResolvesEvenForADirectoryThatDoesNotExist()
        {
            // The caller falls back to gxwire's own in-tree default when this
            // throws, which would silently lose read-only-ness.
            var resolved = GxwireCacheLocation.Resolve(@"Z:\definitely\not\here");

            Assert.False(string.IsNullOrWhiteSpace(resolved));
        }
    }

    public class NativeKnowledgeBaseEvidenceTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RemoteDatabaseMetadata_IsRefusedUnlessThereIsObjectSource(bool hasSource)
        {
            using var dir = NativeKb("SyntheticKb");
            File.WriteAllText(Path.Combine(dir.Path, "knowledgebase.connection"), "synthetic");
            foreach (var metadata in new[] { "CategoryViewConfig.xml", "IAR_OBJS.XML", "nav_objs.xml" })
                File.WriteAllText(Path.Combine(dir.Path, metadata), "<metadata/>");
            Directory.CreateDirectory(Path.Combine(dir.Path, "src"));
            File.WriteAllText(Path.Combine(dir.Path, "src", "Generated.txt"), "// generated");
            if (hasSource) File.WriteAllText(Path.Combine(dir.Path, "Sample.xml"), "<Procedure/>");

            Assert.Equal(hasSource, NativeKnowledgeBaseEvidence.HasSourceText(dir.Path));
            Assert.Equal(hasSource, NativeKnowledgeBaseEvidence.Describe(dir.Path) == null);
        }

        [Fact]
        public void NativeKb_IsRefusedAndSaysWhatWasFound()
        {
            using var dir = NativeKb("KBTeste");

            var evidence = NativeKnowledgeBaseEvidence.Describe(dir.Path);

            Assert.NotNull(evidence);
            Assert.Contains((JToken)"GXLOCK", evidence);
        }

        [Fact]
        public void NativeKb_DatabaseAloneIsDecisive()
        {
            // An XML/text export is a directory of object text and never carries
            // the KB's store, so the database settles it even with source files
            // sitting right there.
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "GX_KB_MyKb.mdf"), "");
            File.WriteAllText(Path.Combine(dir.Path, "MyProc.txt"), "// source");

            var evidence = NativeKnowledgeBaseEvidence.Describe(dir.Path);

            Assert.NotNull(evidence);
            Assert.Contains(evidence!, t => t.ToString().Contains("Knowledge Base database"));
        }

        [Fact]
        public void NativeKb_WorkspaceFileAloneNeedsTheNoSourceTextCondition()
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "MyKb.gxw"), "");

            Assert.NotNull(NativeKnowledgeBaseEvidence.Describe(dir.Path));
        }

        [Fact]
        public void GeneXusMetadataXml_DoesNotDisguiseANativeKb()
        {
            // The real shape of C:\KBs\KBTeste: the root carries
            // CategoryViewConfig.xml, iar_objs.xml and nav_objs.xml, which match
            // the extensions gxwire indexes. A guard that treated any .xml as
            // object source would let this KB through and index gigabytes of
            // generated output, which is the original bug.
            using var dir = NativeKb("KBTeste");
            foreach (var metadata in new[] { "CategoryViewConfig.xml", "iar_objs.xml", "nav_objs.xml" })
            {
                File.WriteAllText(Path.Combine(dir.Path, metadata), "<metadata/>");
            }
            File.WriteAllText(Path.Combine(dir.Path, "GX_KB_KBTeste.mdf"), "");

            Assert.NotNull(NativeKnowledgeBaseEvidence.Describe(dir.Path));
        }

        [Theory]
        [InlineData("MyProc.txt")]
        [InlineData("MyAtt.gxd")]
        [InlineData("MyForm.xml")]
        [InlineData("MyTrn.trn")]
        [InlineData("MyForm.wp")]
        [InlineData("MySdp.sdp")]
        [InlineData("MyProc.gx")]
        public void AnExportCarryingSourceText_IsNotRefused(string sourceFile)
        {
            // A GeneXus XML/text export of the same Knowledge Base is a supported
            // input for gxwire, and such an export can carry the KB's own files.
            // Refusing it would break the one case that works today.
            using var dir = NativeKb("KBExport");
            File.WriteAllText(Path.Combine(dir.Path, sourceFile), "// source");

            Assert.Null(NativeKnowledgeBaseEvidence.Describe(dir.Path));
        }

        [Fact]
        public void PlainDirectoryWithNoMarkers_IsNotRefused()
        {
            using var dir = new TempDir();

            Assert.Null(NativeKnowledgeBaseEvidence.Describe(dir.Path));
        }

        [Fact]
        public void PlainDirectoryOfSourceFiles_IsNotRefused()
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "MyProc.txt"), "// source");

            Assert.Null(NativeKnowledgeBaseEvidence.Describe(dir.Path));
        }

        [Fact]
        public void MissingDirectory_IsNotRefused()
        {
            // Detection is a safety net. If it cannot inspect the directory it
            // must stay out of the way rather than block a call it knows nothing
            // about.
            Assert.Null(NativeKnowledgeBaseEvidence.Describe(@"Z:\definitely\not\here"));
        }

        [Fact]
        public void NullAndEmpty_AreNotRefused()
        {
            Assert.Null(NativeKnowledgeBaseEvidence.Describe(null));
            Assert.Null(NativeKnowledgeBaseEvidence.Describe(""));
            Assert.Null(NativeKnowledgeBaseEvidence.Describe("   "));
        }

        [Fact]
        public void Evidence_ExplainsWhyItRefused()
        {
            using var dir = NativeKb("KBTeste");

            var evidence = NativeKnowledgeBaseEvidence.Describe(dir.Path);

            // The caller renders this to the user as the reason, so it has to say
            // what was missing and not merely that something was.
            Assert.NotNull(evidence);
            Assert.Contains(evidence!, t => t.ToString().Contains("no GeneXus source files"));
        }

        [Fact]
        public void ASoftMarkerPlusSourceText_IsTreatedAsAnExport()
        {
            // The failure mode being prevented from the other side: refusing an
            // export would break the one case where gxwire works.
            using var dir = NativeKb("KBExport");
            File.WriteAllText(Path.Combine(dir.Path, "MyProc.txt"), "// source");

            Assert.Null(NativeKnowledgeBaseEvidence.Describe(dir.Path));
        }

        private static TempDir NativeKb(string name)
        {
            var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "GXLOCK"), "");
            File.WriteAllText(Path.Combine(dir.Path, name + ".gxw"), "");
            return dir;
        }
    }

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "gxwire-499-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
