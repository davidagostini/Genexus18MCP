using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public sealed class ObjectTextFileSystemParityTests
    {
        [Fact]
        public void Selector_all_honors_ignore_and_keeps_type_disambiguation()
        {
            var service = BuildService(
                new SearchIndex.IndexEntry { Name = "Alpha", Type = "Procedure" },
                new SearchIndex.IndexEntry { Name = "Beta", Type = "Procedure" },
                new SearchIndex.IndexEntry { Name = "Alpha", Type = "Transaction" });

            bool selected = service.TrySelectEntries(
                null,
                new JObject
                {
                    ["targets"] = new JArray("[all]"),
                    ["ignore"] = new JArray("Procedure:Beta")
                },
                allowAll: true,
                out List<SearchIndex.IndexEntry> entries,
                out string error);

            Assert.True(selected, error);
            Assert.Null(error);
            Assert.Equal(2, entries.Count);
            Assert.DoesNotContain(entries, item => item.Type == "Procedure" && item.Name == "Beta");
            Assert.Contains(entries, item => item.Type == "Procedure" && item.Name == "Alpha");
            Assert.Contains(entries, item => item.Type == "Transaction" && item.Name == "Alpha");
        }

        [Fact]
        public void Module_selector_expands_children_only_when_requested()
        {
            var service = BuildService(
                new SearchIndex.IndexEntry
                {
                    Name = "Sales",
                    Type = "Module",
                    Module = "Root Module",
                    ParentPath = "Root Module",
                    ParentFolderPath = "Root Module"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "Order",
                    Type = "Transaction",
                    Module = "Sales",
                    ParentPath = "Sales",
                    ParentFolderPath = "Root Module/Sales",
                    Path = "Sales/Order"
                },
                new SearchIndex.IndexEntry
                {
                    Name = "Outside",
                    Type = "Procedure",
                    Module = "Other",
                    ParentPath = "Other",
                    ParentFolderPath = "Root Module/Other",
                    Path = "Other/Outside"
                });

            bool selected = service.TrySelectEntries(
                "Module:Sales",
                new JObject { ["includeChildren"] = true },
                allowAll: true,
                out List<SearchIndex.IndexEntry> entries,
                out string error,
                includeChildren: true);

            Assert.True(selected, error);
            Assert.Single(entries);
            Assert.Equal("Order", entries[0].Name);

            selected = service.TrySelectEntries(
                "Module:Sales",
                new JObject { ["includeChildren"] = false },
                allowAll: true,
                out entries,
                out error,
                includeChildren: false);

            Assert.True(selected, error);
            Assert.Empty(entries);
        }

        [Fact]
        public void Selection_service_owns_index_selector_expansion()
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Sales", Type = "Module", Module = "Root Module", Path = "Sales" },
                new SearchIndex.IndexEntry { Name = "Order", Type = "Transaction", Module = "Sales", Path = "Sales/Order" }
            });
            cache.MarkIndexComplete(2);

            var service = new TextTreeSelectionService(cache);
            Assert.True(service.TrySelectEntries(
                "Module:Sales",
                new JObject(),
                allowAll: true,
                out List<SearchIndex.IndexEntry> selected,
                out string error,
                includeChildren: true));
            Assert.Null(error);
            Assert.Contains(selected, entry => entry.Name == "Order");
        }

        [Fact]
        public void ValidateInMemory_stopOnError_reports_remaining_files()
        {
            string root = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(root, "a.web.xml"), "<layout>");
                File.WriteAllText(Path.Combine(root, "b.gx"), "Procedure B\n{\n}\n");

                JObject response = JObject.Parse(new ObjectTextService(null, null).Execute(
                    "validate_text_in_memory",
                    null,
                    new JObject { ["inputPath"] = root, ["stopOnError"] = true },
                    default(System.Threading.CancellationToken)));

                Assert.Equal("partial", response["status"]?.ToString());
                Assert.Equal(1, response["result"]?["filesChecked"]?.ToObject<int>());
                Assert.Equal(1, response["result"]?["remaining"]?.ToObject<int>());
                Assert.True(response["result"]?["stoppedOnError"]?.ToObject<bool>());
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void ListInMemory_honors_includeChildren_skip_and_limit()
        {
            string root = NewTempDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                File.WriteAllText(Path.Combine(root, "a.gx"), "Procedure A\n{\n}\n");
                File.WriteAllText(Path.Combine(root, "nested", "b.gx"), "Procedure B\n{\n}\n");

                var service = new ObjectTextService(null, null);
                JObject shallow = JObject.Parse(service.Execute(
                    "list_text_in_memory",
                    null,
                    new JObject { ["inputPath"] = root, ["includeChildren"] = false },
                    default(System.Threading.CancellationToken)));
                Assert.Equal(1, shallow["result"]?["filesChecked"]?.ToObject<int>());
                Assert.Equal(1, shallow["result"]?["filesReturned"]?.ToObject<int>());

                JObject window = JObject.Parse(service.Execute(
                    "list_text_in_memory",
                    null,
                    new JObject { ["inputPath"] = root, ["skip"] = 1, ["limit"] = 1 },
                    default(System.Threading.CancellationToken)));
                Assert.Equal(2, window["result"]?["filesAvailable"]?.ToObject<int>());
                Assert.Equal(1, window["result"]?["skipped"]?.ToObject<int>());
                Assert.Equal(1, window["result"]?["filesReturned"]?.ToObject<int>());
                Assert.Equal("nested/b.gx", window["result"]?["results"]?[0]?["file"]?.ToString().Replace('\\', '/'));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void ValidateInMemory_checks_native_manifest_hash_and_file_reference()
        {
            string root = NewTempDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "src"));
                string document = "Procedure Hello\n{\nmsg(\"hello\");\n}\n";
                File.WriteAllText(Path.Combine(root, "src", "Hello.gx"), document);
                File.WriteAllText(Path.Combine(root, "_gxmcp-sdk-text-manifest.json"), new JObject
                {
                    ["kind"] = "GeneXusSdkTextTree",
                    ["schemaVersion"] = 1,
                    ["objects"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "Hello",
                            ["type"] = "Procedure",
                            ["file"] = "src/Hello.gx",
                            ["sha256"] = "0000000000000000000000000000000000000000000000000000000000000000",
                            ["bytes"] = 1
                        }
                    }
                }.ToString());

                JObject response = JObject.Parse(new ObjectTextService(null, null).Execute(
                    "validate_text_in_memory",
                    null,
                    new JObject { ["inputPath"] = root },
                    default(System.Threading.CancellationToken)));

                Assert.Equal("partial", response["status"]?.ToString());
                Assert.False(response["result"]?["valid"]?.ToObject<bool>());
                Assert.Contains(
                    response["result"]?["results"] ?? new JArray(),
                    token => token["kind"]?.ToString() == "Manifest" && token["valid"]?.ToObject<bool>() == false);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void Native_module_metadata_is_deterministic_and_toml_escaped()
        {
            string document = SdkTextTreeService.BuildModuleMetadataDocument(
                "Operations",
                "module\\identifier\"1");

            Assert.Contains("ModuleIdentifier = \"module\\\\identifier\\\"1\"", document);
            Assert.Contains("Name = \"Operations\"", document);
            Assert.Contains("IsDefault = \"False\"", document);
        }

        [Fact]
        public void Legacy_manifest_and_gxtext_are_validated_without_a_kb()
        {
            string root = NewTempDirectory();
            try
            {
                string file = "00000_Procedure__Hello.gxtext";
                File.WriteAllText(Path.Combine(root, file), "Procedure Hello\n{\nmsg(\"hello\");\n}\n");
                File.WriteAllText(Path.Combine(root, ObjectTextService.ManifestFileName), new JObject
                {
                    ["kind"] = "GeneXusObjectText",
                    ["schemaVersion"] = 1,
                    ["objects"] = new JArray
                    {
                        new JObject { ["name"] = "Hello", ["type"] = "Procedure", ["file"] = file }
                    }
                }.ToString());

                JObject response = JObject.Parse(new ObjectTextService(null, null).Execute(
                    "validate_text_in_memory",
                    null,
                    new JObject { ["inputPath"] = root },
                    default(System.Threading.CancellationToken)));

                Assert.Equal("ok", response["status"]?.ToString());
                Assert.True(response["result"]?["valid"]?.ToObject<bool>());
                Assert.Equal(2, response["result"]?["filesChecked"]?.ToObject<int>());
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void Native_all_parts_document_round_trips_sections()
        {
            string document = SdkTextTreeService.BuildObjectDocumentParts(
                "Procedure",
                "Hello",
                new Dictionary<string, string>
                {
                    ["Source"] = "msg(\"hello\");",
                    ["Rules"] = "parm(in:&Name);"
                },
                "\t");

            Assert.True(SdkTextTreeService.TryParseObjectDocumentParts(
                document,
                out string type,
                out string name,
                out Dictionary<string, string> parts,
                out string error));
            Assert.Null(error);
            Assert.Equal("Procedure", type);
            Assert.Equal("Hello", name);
            Assert.Equal("msg(\"hello\");", parts["Source"].Trim());
            Assert.Equal("parm(in:&Name);", parts["Rules"].Trim());
        }

        [Fact]
        public void Table_projection_uses_the_native_tables_namespace()
        {
            Assert.Equal(
                "src/#tables/Customer.gx",
                SdkTextTreeService.BuildTableProjectionRelativePath("src", "Customer"));
            Assert.Equal(
                "ref/#tables/Customer.gx",
                SdkTextTreeService.BuildTableProjectionRelativePath("ref", "Customer"));
        }

        [Fact]
        public void All_selector_is_a_full_selection_only_without_other_filters()
        {
            Assert.True(SdkTextTreeService.IsFullSelection(null, new JObject
            {
                ["targets"] = new JArray("[all]")
            }));
            Assert.False(SdkTextTreeService.IsFullSelection(null, new JObject
            {
                ["targets"] = new JArray("[all]"),
                ["type"] = "Procedure"
            }));
        }

        [Fact]
        public void Mutable_multi_part_import_requires_a_complete_rollback_snapshot()
        {
            Assert.Equal(
                "RollbackSnapshotUnavailable",
                SdkTextTreeService.ValidateRollbackPrecondition(false, true, 2, false));
            Assert.Null(SdkTextTreeService.ValidateRollbackPrecondition(false, true, 2, true));
            Assert.Null(SdkTextTreeService.ValidateRollbackPrecondition(true, true, 2, false));
            Assert.Null(SdkTextTreeService.ValidateRollbackPrecondition(false, false, 2, false));
        }

        private static ObjectTextService BuildService(params SearchIndex.IndexEntry[] entries)
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(entries);
            cache.MarkIndexComplete(entries.Length);
            return new ObjectTextService(null, cache);
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "gxmcp-text-parity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
            }
        }
    }
}
