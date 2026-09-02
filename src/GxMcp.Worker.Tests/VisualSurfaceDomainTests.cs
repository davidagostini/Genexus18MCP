using System;
using System.Drawing;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class VisualSurfaceDomainTests
    {
        [Fact]
        public void WebFormVisualSurface_ProjectsVisualTree_PreservesUntouchedControls()
        {
            var domain = new VisualSurfaceDomain();
            var adapter = domain.GetAdapter("WebPanel", "WebForm");

            Assert.NotNull(adapter);
            Assert.Equal("WebForm", adapter.SurfaceKind);

            string baselineXml = @"<GxMultiForm><BODY><TABLE id=""MainTable""><TR><TD><BUTTON id=""BtnSubmit"" Caption=""Submit"" /></TD><TD><TEXTBLOCK id=""TxtTitle"" Caption=""Hello"" /></TD></TR></TABLE></BODY></GxMultiForm>";
            string incomingXml = @"<GxMultiForm><BODY><TABLE id=""MainTable""><TR><TD><BUTTON id=""BtnSubmit"" Caption=""Submit Now"" /></TD></TR></TABLE></BODY></GxMultiForm>";

            var mutation = adapter.Mutate(baselineXml, incomingXml);

            Assert.True(mutation.Success);
            Assert.NotNull(mutation.MergedXml);

            // Verify untouched TxtTitle control is preserved from baselineXml
            Assert.Contains("TxtTitle", mutation.MergedXml);
            Assert.Contains("Submit Now", mutation.MergedXml);
        }

        [Fact]
        public void ReportVisualSurface_DiffPlan_PreservesUntouchedPrintBlocksAndColors()
        {
            var domain = new VisualSurfaceDomain();
            var adapter = domain.GetAdapter("Procedure", "Report");

            Assert.NotNull(adapter);
            Assert.Equal("ReportLayout", adapter.SurfaceKind);

            string baselineXml = @"<Report><PrintBlock id=""Header""><ReportRectangle id=""Box1"" BackColor=""Color [A=255, R=144, G=238, B=144]"" /></PrintBlock><PrintBlock id=""Footer""><ReportLine id=""Line1"" ForeColor=""#0000FF"" /></PrintBlock></Report>";
            string incomingXml = @"<Report><PrintBlock id=""Header""><ReportRectangle id=""Box1"" BackColor=""#90ee90"" /></PrintBlock></Report>";

            var mutation = adapter.Mutate(baselineXml, incomingXml);

            Assert.True(mutation.Success);
            Assert.NotNull(mutation.MergedXml);

            // Verify untouched PrintBlock Footer and Line1 are preserved
            Assert.Contains("Footer", mutation.MergedXml);
            Assert.Contains("Line1", mutation.MergedXml);
            Assert.True(mutation.ColorsEquivalent("Color [A=255, R=144, G=238, B=144]", "#90ee90"));
        }

        [Fact]
        public void VisualSurface_NormalizesColorTokens_AcrossDotNetAndGeneXusFormats()
        {
            var domain = new VisualSurfaceDomain();
            var adapter = domain.GetAdapter("Procedure", "Report");

            Assert.True(adapter.ColorsEquivalent("Color [A=255, R=255, G=0, B=0]", "255; 0; 0|"));
            Assert.True(adapter.ColorsEquivalent("#FF0000", "rgb(255, 0, 0)"));
            Assert.False(adapter.ColorsEquivalent("#FF0000", "#00FF00"));
        }
    }
}
