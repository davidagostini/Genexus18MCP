using System;
using System.IO;
using System.IO.Compression;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TransferImportTests
    {
        [Fact]
        public void SilentImportOptions_DefaultsToLosslessOverwrite()
        {
            Assert.Equal("Overwrite", TransferService.ResolveImportThemeBehavior(new JObject()));
            Assert.Equal("UseFromExport", TransferService.ResolveImportClassConflicts(new JObject()));

            // ImportOptions properties are backed by the GeneXus property
            // context; reading them in this isolated net48 test attempts to
            // load the IDE configuration and throws. The effective values are
            // verified by the real-KB import test, while these assertions keep
            // the silent-options factory covered without requiring a KB.
            Assert.NotNull(TransferService.SilentImportOptions(new JObject()));
        }

        [Fact]
        public void SilentImportOptions_AllowsExplicitIncrementalThemeIntegration()
        {
            Assert.Equal(
                "IncrementalIntegration",
                TransferService.ResolveImportThemeBehavior(
                    JObject.Parse("{\"themeImportBehavior\":\"IncrementalIntegration\"}")));
        }

        [Fact]
        public void SilentImportOptions_RejectsUnknownExplicitValues()
        {
            Assert.Throws<ArgumentException>(() =>
                TransferService.SilentImportOptions(
                    JObject.Parse("{\"classConflicts\":\"IgnoreEverything\"}")));
        }

        [Fact]
        public void ReadExportWebForms_UsesRawSourceFromXpz()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-transfer-" + Guid.NewGuid().ToString("N") + ".xpz");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("export.xml").Open()))
                {
                    writer.Write("<ExportFile><Objects>"
                        + "<Object name=\"SampleWebPanel\"><Part><Source><![CDATA["
                        + "<GxMultiForm><gxAttribute GxWidth=\"30chr\" GxHeight=\"1row\" />"
                        + "</GxMultiForm>]]></Source></Part></Object>"
                        + "</Objects></ExportFile>");
                }

                var forms = TransferService.ReadExportWebForms(path);
                Assert.True(forms.ContainsKey("SampleWebPanel"));
                Assert.Contains("GxWidth=\"30chr\"", forms["SampleWebPanel"]);
                Assert.Contains("GxHeight=\"1row\"", forms["SampleWebPanel"]);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void ReadExportWebForms_HandlesNamespacesAndXmlDeclaration()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-transfer-ns-" + Guid.NewGuid().ToString("N") + ".xpz");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("namespaced.xml").Open()))
                {
                    writer.Write("<?xml version=\"1.0\"?><gx:ExportFile xmlns:gx=\"urn:test\"><gx:Object name=\"NamespacedPanel\"><gx:Part><gx:Source><![CDATA[<?xml version=\"1.0\"?><gx:GxMultiForm xmlns:gx=\"urn:test\"><gx:gxAttribute GxWidth=\"30chr\" /></gx:GxMultiForm>]]></gx:Source></gx:Part></gx:Object></gx:ExportFile>");
                }

                var forms = TransferService.ReadExportWebForms(path);
                Assert.True(forms.ContainsKey("NamespacedPanel"));
                Assert.Contains("GxWidth=\"30chr\"", forms["NamespacedPanel"]);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
