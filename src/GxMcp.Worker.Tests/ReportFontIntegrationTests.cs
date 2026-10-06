using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Artech.Genexus.Common.Parts.Layout;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReportFontIntegrationTests
    {
        private static bool Set(object control, string property, string raw)
            => (bool)typeof(ReportLayoutHelper).GetMethod("TrySetProperty", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { control, control.GetType(), property, raw });

        [Theory]
        [InlineData("Font", "McpMissingFamily_435436")]
        [InlineData("FontName", "McpMissingFamily_435436")]
        [InlineData("Font", "McpMissingFamily_435436, 12pt")]
        [InlineData("Font", "[Font: Name=McpMissingFamily_435436, Size=12, Units=3]")]
        public void BothMutationPathsRejectUninstalledFamiliesWithoutChangingSdkFont(string property, string raw)
        {
            var control = new FontControl();
            using var original = new Font("Arial", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
            control.Font = original;
            Assert.False(Set(control, property, raw));
            Assert.Same(original, control.Font);
            Assert.Null(ReportLayoutHelper.ComposeFont(original, property, raw));
            Assert.False(FontHelper.TryParse(raw, out _));
        }

        [Theory]
        [InlineData("Font", "Arial")]
        [InlineData("FontName", "Arial")]
        [InlineData("FontSize", "13")]
        [InlineData("Font", "[Font: Name=Arial, Size=13]")]
        [InlineData("Font", "Arial, 13pt")]
        public void CompositionPreservesUnspecifiedSdkAttributes(string property, string raw)
        {
            var control = new FontControl();
            using var original = new Font("Tahoma", 9f, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Pixel, 2, true);
            control.Font = original;
            Assert.True(Set(control, property, raw));
            using var actual = control.Font;
            Assert.Equal(original.Style, actual.Style);
            Assert.Equal(original.GdiCharSet, actual.GdiCharSet);
            Assert.Equal(original.GdiVerticalFont, actual.GdiVerticalFont);
            Assert.Equal(raw.EndsWith("pt") ? GraphicsUnit.Point : GraphicsUnit.Pixel, actual.Unit);
            Assert.Equal(property == "FontName" || raw == "Arial" ? 9f : 13f, actual.Size);
            Assert.Equal(property == "FontSize" ? "Tahoma" : "Arial", actual.Name);
        }

        [Fact]
        public void SdkProjectionCarriesAuthoritativeStyleAndUnitsAndFullAddSpecVerifies()
        {
            // SDK constructors require a bootstrapped architecture service; inspect actual metadata,
            // then exercise the same CLR property contract through the compatibility helper.
            Assert.Equal(typeof(Font), typeof(ReportLabel).GetProperty("Font").PropertyType);
            Assert.Null(typeof(ReportLabel).GetProperty("FontName"));
            Assert.Null(typeof(ReportLabel).GetProperty("FontSize"));
            var control = new FontControl();
            Assert.True(Set(control, "Font", "Arial, 12pt, style=Bold, Italic"));
            using var font = control.Font;
            // Exercise the production reflection projection over the verified SDK property contract.
            var band = new ProjectionBand { Items = new[] { control } };
            var xml = (string)typeof(ReportLayoutHelper).GetMethod("GenerateVisualXmlFromBands", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { new[] { band }, null });
            var projected = XDocument.Parse(xml).Descendants("Control").Single();
            Assert.Contains("Style=Bold|Italic", (string)projected.Attribute("Font"));
            Assert.True(LayoutService.ReportFontMatches(projected, "font", "Arial, 12pt, style=Bold, Italic"));
            Assert.False(LayoutService.ReportFontMatches(projected, "font", "Arial, 12pt, style=Regular"));
            Assert.False(LayoutService.ReportFontMatches(projected, "font", "Arial, 12px, style=Bold, Italic"));
        }

        public sealed class ProjectionBand
        {
            public string Name => "header";
            public FontControl[] Items { get; set; }
        }

        public sealed class FontControl
        {
            public string Name => "label";
            public Font Font { get; set; }
        }

        [Theory]
        [InlineData("[Font: Name=Arial, Size=12, Units=3]", "Arial, 12pt, style=Bold")]
        [InlineData("[Font: Name=McpMissingFamily_435436, Size=12, Units=3]", "[Font: Name=McpMissingFamily_435436, Size=12, Units=3]")]
        [InlineData("[Font: Name=Arial, Size=12, Units=999]", "[Font: Name=Arial, Size=12, Units=999]")]
        [InlineData("[Font: Name=Arial, Size=12, Style=999]", "[Font: Name=Arial, Size=12, Style=999]")]
        [InlineData("[Font: Name=Arial, Size=12, Style=Bold, Italic]", "[Font: Name=Arial, Size=12, Style=Bold, Italic]")]
        public void SetPropertyVerificationFailsClosedEvenForIdenticalUnparseableStrings(string actual, string expected)
        {
            Assert.False((bool)typeof(LayoutService).GetMethod("IsPersistedValueMatch", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { "Font", expected, actual }));
        }

        [Theory]
        [InlineData("Arial, 12pt, style=Bold")]
        [InlineData("[Font: Name=Arial, Size=12, Units=3, Style=Bold]")]
        public void AddControlFullSpecRequiresStyleConfirmation(string expected)
        {
            var missingStyle = XElement.Parse("<Control FontName='Arial' FontSize='12' Font='[Font: Name=Arial, Size=12, Units=3]'/>");
            Assert.False(LayoutService.ReportFontMatches(missingStyle, "font", expected));
        }

        [Theory]
        [InlineData("LayoutService.ReportControls.cs", "AddReportControl")]
        [InlineData("LayoutService.cs", "SetProperty")]
        public void PublicFontPathsValidateBeforeSave(string file, string method)
        {
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", file);
            var body = SourceAssert.MethodBody(source, "public string " + method + "(");
            Assert.Contains("InvalidReportFont", body);
            Assert.True(body.IndexOf("InvalidReportFont", StringComparison.Ordinal) < body.IndexOf("PersistVisualXml(", StringComparison.Ordinal));
        }

        [Fact]
        public void ReportSetPropertyCannotRestoreAfterReadBackFailure()
        {
            var source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "LayoutService.cs");
            var set = SourceAssert.MethodBody(source, "public string SetProperty(");
            Assert.Contains("contextResult.Surface != VisualSurface.Report &&", set);
            Assert.Contains("return ReportMutationFailure", set);
        }

        [Theory]
        [InlineData("Arial, 12pt, style=Bold")]
        [InlineData("[Font: Name=Arial, Size=12, Units=3, Style=Bold]")]
        public void FullAddControlFontSpecificationIsVerifiedByContent(string expected)
        {
            var readBack = XElement.Parse("<Control FontName='Arial' FontSize='12' Font='[Font: Name=Arial, Size=12, Units=3, Style=Bold]'/>");
            Assert.True(LayoutService.ReportFontMatches(readBack, "font", expected));
        }
    }
}
