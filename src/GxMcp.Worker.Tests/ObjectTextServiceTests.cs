using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class ObjectTextServiceTests
    {
        [Fact]
        public void ValidateTextInMemory_accepts_a_valid_native_text_tree_without_a_kb()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-text-memory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            try
            {
                File.WriteAllText(Path.Combine(root, "src", "Customer.gx"),
                    "Procedure Customer\n{\n\t#Properties\n\t\tName = \"Customer\"\n\t#End\n}");

                var service = new ObjectTextService(null, null);
                JObject response = JObject.Parse(service.Execute(
                    "validate_text_in_memory",
                    null,
                    new JObject { ["inputPath"] = root },
                    CancellationToken.None));

                Assert.Equal("ok", response["status"]?.ToString());
                Assert.Equal("ObjectTextMemoryValidationCompleted", response["code"]?.ToString());
                Assert.True(response["result"]?["valid"]?.ToObject<bool>());
                Assert.Equal(1, response["result"]?["filesChecked"]?.ToObject<int>());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ValidateTextInMemory_reports_invalid_companion_xml_without_a_kb()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-text-memory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            try
            {
                File.WriteAllText(Path.Combine(root, "src", "Customer.gx"), "Procedure Customer\n{\n}");
                File.WriteAllText(Path.Combine(root, "src", "Customer.web.xml"), "<layout>");

                var service = new ObjectTextService(null, null);
                JObject response = JObject.Parse(service.Execute(
                    "validate_text_in_memory",
                    null,
                    new JObject { ["inputPath"] = root },
                    CancellationToken.None));

                Assert.Equal("partial", response["status"]?.ToString());
                Assert.Equal("ObjectTextMemoryValidationPartial", response["code"]?.ToString());
                Assert.False(response["result"]?["valid"]?.ToObject<bool>());
                Assert.Equal(1, response["result"]?["invalidFiles"]?.ToObject<int>());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
        [Fact]
        public void ListTextInMemory_returns_deterministic_native_tree_entries()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-object-text-list-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "src"));
                Directory.CreateDirectory(Path.Combine(root, "ref", "GeneXus"));
                File.WriteAllText(Path.Combine(root, "src", "Procedure__Hello.gx"), "Procedure Hello\n{\n}\n");
                File.WriteAllText(Path.Combine(root, "ref", "GeneXus", "module.toml"), "name = \"GeneXus\"\n");

                var service = new ObjectTextService(null, null);
                JObject response = JObject.Parse(service.Execute(
                    "list_text_in_memory",
                    null,
                    new JObject { ["inputPath"] = root },
                    CancellationToken.None));

                Assert.Equal("ok", response["status"]?.ToString());
                Assert.Equal("ObjectTextMemoryListCompleted", response["code"]?.ToString());
                Assert.Equal(2, response["result"]?["filesChecked"]?.ToObject<int>());
                Assert.Equal(1, response["result"]?["objectFiles"]?.ToObject<int>());
                Assert.Equal(1, response["result"]?["metadataFiles"]?.ToObject<int>());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void SdkTextTreeDocument_round_trips_type_name_and_source()
        {
            string document = SdkTextTreeService.BuildObjectDocument(
                "Procedure",
                "Hello",
                "msg(\"hello\");\n");

            Assert.True(SdkTextTreeService.TryParseObjectDocument(
                document,
                out string type,
                out string name,
                out string source,
                out string error));
            Assert.Null(error);
            Assert.Equal("Procedure", type);
            Assert.Equal("Hello", name);
            Assert.Equal("msg(\"hello\");", source.Trim());
        }
    }
}
