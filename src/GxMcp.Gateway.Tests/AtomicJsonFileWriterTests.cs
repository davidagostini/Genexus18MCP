using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class AtomicJsonFileWriterTests
    {
        [Fact]
        public void Write_PreservesUnknownJsonFieldsAndPublishesCompleteDocument()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp-atomic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            try
            {
                File.WriteAllText(path, "{\"Unknown\":{\"keep\":true},\"Environment\":{\"DefaultKb\":\"old\",\"ActiveKb\":\"old\"}}");

                AtomicJsonFileWriter.Write(path, "{\"Unknown\":{\"keep\":true},\"Environment\":{\"DefaultKb\":\"new\",\"ActiveKb\":\"new\"}}");

                var result = JObject.Parse(File.ReadAllText(path));
                Assert.Equal("new", result["Environment"]?["DefaultKb"]?.ToString());
                Assert.True((bool)result["Unknown"]!["keep"]!);
                Assert.Empty(Directory.GetFiles(dir, "config.json.*.tmp"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Write_WhenStagingFails_PreservesPreviousFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp-atomic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            try
            {
                const string original = "{\"Environment\":{\"DefaultKb\":\"old\",\"ActiveKb\":\"old\"}}";
                File.WriteAllText(path, original);

                Assert.Throws<IOException>(() => AtomicJsonFileWriter.Write(
                    path,
                    "{\"Environment\":{\"DefaultKb\":\"new\",\"ActiveKb\":\"new\"}}",
                    (_, _) => throw new IOException("simulated interruption")));

                Assert.Equal(original, File.ReadAllText(path));
                Assert.Empty(Directory.GetFiles(dir, "config.json.*.tmp"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async System.Threading.Tasks.Task Write_UsesUniqueSiblingTempsForConcurrentWriters()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp-atomic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            try
            {
                File.WriteAllText(path, "{\"Environment\":{\"DefaultKb\":\"old\",\"ActiveKb\":\"old\"}}");
                var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
                var writers = new System.Threading.Tasks.Task[12];
                for (int i = 0; i < writers.Length; i++)
                {
                    string alias = "kb" + i;
                    writers[i] = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { AtomicJsonFileWriter.Write(path, $"{{\"Environment\":{{\"DefaultKb\":\"{alias}\",\"ActiveKb\":\"{alias}\"}}}}"); }
                        catch (Exception ex) { errors.Add(ex); }
                    });
                }
                await System.Threading.Tasks.Task.WhenAll(writers);

                Assert.Empty(errors);
                var result = JObject.Parse(File.ReadAllText(path));
                Assert.Equal(result["Environment"]?["DefaultKb"]?.ToString(), result["Environment"]?["ActiveKb"]?.ToString());
                Assert.Empty(Directory.GetFiles(dir, "config.json.*.tmp"));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
