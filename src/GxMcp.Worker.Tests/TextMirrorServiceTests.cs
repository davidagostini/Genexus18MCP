using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public sealed class TextMirrorServiceTests
    {
        [Fact]
        public void Start_and_status_are_idempotent_without_touching_the_sdk()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-text-mirror-" + Guid.NewGuid().ToString("N"));
            try
            {
                var mirror = new TextMirrorService(null, null, null);
                JObject started = JObject.Parse(mirror.Run("start", new JObject
                {
                    ["outputPath"] = root,
                    ["intervalMs"] = 60000,
                    ["initialSync"] = false
                }));
                Assert.Equal("ok", started["status"]?.ToString());
                Assert.True(started["result"]?["running"]?.ToObject<bool>());

                JObject status = JObject.Parse(mirror.Run("status", new JObject()));
                Assert.True(status["result"]?["running"]?.ToObject<bool>());
                Assert.Equal(root, status["result"]?["root"]?.ToString());

                mirror.NotifyObjectChanged("Customer", "Transaction", DateTime.UtcNow);
                status = JObject.Parse(mirror.Run("status", new JObject()));
                Assert.Equal(1, status["result"]?["pendingChanges"]?.ToObject<int>());
                mirror.NotifyObjectDeleted("Customer", "Transaction", DateTime.UtcNow);
                status = JObject.Parse(mirror.Run("status", new JObject()));
                Assert.Equal(1, status["result"]?["pendingChanges"]?.ToObject<int>());
                Assert.Equal(1, status["result"]?["pendingDeletions"]?.ToObject<int>());

                JObject stopped = JObject.Parse(mirror.Run("stop", new JObject()));
                Assert.Equal("ok", stopped["status"]?.ToString());
                Assert.False(stopped["result"]?["running"]?.ToObject<bool>());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Deleted_identity_removes_object_companions_and_updates_manifest_atomically()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-text-mirror-delete-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(root, "src", "Customer.gx");
            string companion = Path.Combine(root, "src", "Customer.web.xml");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(source));
                File.WriteAllText(source, "Procedure Customer\n{\n}\n");
                File.WriteAllText(companion, "<layout />");
                File.WriteAllText(Path.Combine(root, SdkTextTreeService.ManifestFileName), new JObject
                {
                    ["kind"] = "GeneXusSdkTextTree",
                    ["objects"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "Customer",
                            ["type"] = "Procedure",
                            ["file"] = "src/Customer.gx",
                            ["companions"] = new JArray(new JObject { ["file"] = "src/Customer.web.xml" })
                        }
                    },
                    ["metadata"] = new JArray { new JObject { ["file"] = "src/module.toml" } }
                }.ToString());

                JObject summary = TextMirrorService.RemoveMirroredObjectsForTest(
                    root,
                    new[] { Tuple.Create("Customer", "Procedure") },
                    out string error);

                Assert.Null(error);
                Assert.Equal(1, summary["removedObjects"]?.ToObject<int>());
                Assert.Equal(2, summary["removedFiles"]?.ToObject<int>());
                Assert.False(File.Exists(source));
                Assert.False(File.Exists(companion));
                JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, SdkTextTreeService.ManifestFileName)));
                Assert.Empty(manifest["objects"] as JArray ?? new JArray());
                Assert.Single(manifest["metadata"] as JArray ?? new JArray());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
