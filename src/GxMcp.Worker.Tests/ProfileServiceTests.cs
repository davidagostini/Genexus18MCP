using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using GxMcp.Worker.Utils;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Defensive XML parser for a GeneXus runtime profile. Tests verify the
    // schema-recognised happy paths plus the graceful "unknown schema" path
    // so a future GX-version change doesn't crash the tool.
    public class ProfileServiceTests
    {
        private static ProfileService Service() =>
            new ProfileService(new UserFilePathPolicy(() => Path.GetTempPath()));

        private static string WriteFixture(string xml)
        {
            string path = Path.Combine(Path.GetTempPath(), "gxprofile_" + System.Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(path, xml);
            return path;
        }

        [Fact]
        public void Run_MissingAction_ReturnsInvalidActionEnvelope()
        {
            var svc = Service();
            var obj = JObject.Parse(svc.Run(new JObject { ["path"] = "x" }));
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("InvalidAction", obj["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Run_FileNotFound_ReturnsFileNotFoundEnvelope()
        {
            var svc = Service();
            var obj = JObject.Parse(svc.Run(new JObject
            {
                ["action"] = "analyze",
                ["path"] = Path.Combine(Path.GetTempPath(), "does-not-exist", "nope.xml")
            }));
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("FileNotFound", obj["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Analyze_KnownShape_AggregatesByName()
        {
            string xml =
                "<ProfileResult>" +
                "  <Sample name='Foo' totalTime='100' callCount='2' />" +
                "  <Sample name='Foo' totalTime='50'  callCount='1' />" +
                "  <Sample name='Bar' totalTime='30'  callCount='1' />" +
                "</ProfileResult>";
            string path = WriteFixture(xml);
            try
            {
            var svc = Service();
                var obj = JObject.Parse(svc.Run(new JObject
                {
                    ["action"] = "analyze",
                    ["path"] = path
                }));
                Assert.Equal("ok", obj["status"]?.ToString());
                Assert.Equal(180.0, obj["result"]?["totalSampleMs"]?.Value<double>());
                var byObj = (JArray)obj["result"]?["byObject"];
                Assert.Equal(2, byObj.Count);

                var foo = byObj.OfType<JObject>().Single(o => o["name"]?.ToString() == "Foo");
                Assert.Equal(150.0, foo["totalMs"]?.Value<double>());
                Assert.Equal(3, foo["callCount"]?.Value<int>());

                // Sorted by totalMs desc — Foo (150) before Bar (30).
                Assert.Equal("Foo", byObj[0]["name"]?.ToString());
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Analyze_UnknownSchema_GracefulWithParserWarnings()
        {
            string xml = "<root><doc><nothing-to-time /></doc></root>";
            string path = WriteFixture(xml);
            try
            {
                var svc = Service();
                var obj = JObject.Parse(svc.Run(new JObject
                {
                    ["action"] = "analyze",
                    ["path"] = path
                }));
                Assert.Equal("ok", obj["status"]?.ToString());
                Assert.Equal(0.0, obj["result"]?["totalSampleMs"]?.Value<double>());
                Assert.NotNull(obj["result"]?["parserWarnings"]);
                var warns = (JArray)obj["result"]?["parserWarnings"];
                Assert.Contains(warns, w => w.ToString().Contains("no elements with name+timing"));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Hotspots_RespectsTopCap()
        {
            string xml = "<r>" +
                string.Join("", Enumerable.Range(1, 60).Select(i =>
                    $"<S name='Obj{i}' totalTime='{i * 10}' />")) +
                "</r>";
            string path = WriteFixture(xml);
            try
            {
                var svc = Service();
                var obj = JObject.Parse(svc.Run(new JObject
                {
                    ["action"] = "hotspots",
                    ["path"] = path,
                    ["top"] = 100   // requested 100, capped at 50 by service
                }));
                Assert.Equal("ok", obj["status"]?.ToString());
                Assert.Equal(50, obj["result"]?["top"]?.Value<int>());
                Assert.Equal(50, ((JArray)obj["result"]?["hotspots"]).Count);
                // Top entry = highest totalTime.
                Assert.Equal("Obj60", ((JArray)obj["result"]?["hotspots"])[0]["name"]?.ToString());
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Correlate_TargetSubstring_FiltersMatches()
        {
            string xml =
                "<r>" +
                "  <S name='OrderRead'   totalTime='80' />" +
                "  <S name='OrderWrite'  totalTime='40' />" +
                "  <S name='SomethingElse' totalTime='10' />" +
                "</r>";
            string path = WriteFixture(xml);
            try
            {
                var svc = Service();
                var obj = JObject.Parse(svc.Run(new JObject
                {
                    ["action"] = "correlate",
                    ["path"] = path,
                    ["target"] = "Order"
                }));
                Assert.Equal("ok", obj["status"]?.ToString());
                Assert.Equal("Order", obj["target"]?.ToString());
                var matches = (JArray)obj["result"]?["matches"];
                Assert.Equal(2, matches.Count);
                Assert.All(matches, m =>
                    Assert.Contains("Order", m["name"]?.ToString()));
            }
            finally { File.Delete(path); }
        }
    }
}
