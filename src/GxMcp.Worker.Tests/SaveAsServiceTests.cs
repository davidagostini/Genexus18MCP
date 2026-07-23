using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Wave 3 — IDE Save As parity. Tests the SaveAsService orchestration via
    /// an in-memory IObjectCloner so no live KB / SDK is required.
    /// </summary>
    public class SaveAsServiceTests
    {
        private sealed class FakeCloner : SaveAsService.IObjectCloner
        {
            public Dictionary<string, SaveAsService.SourceDescriptor> Sources { get; }
                = new Dictionary<string, SaveAsService.SourceDescriptor>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Existing { get; }
                = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, SaveAsService.PatternInstanceDescriptor> Instances { get; }
                = new Dictionary<string, SaveAsService.PatternInstanceDescriptor>(StringComparer.OrdinalIgnoreCase);

            public List<(string type, string name)> Creates { get; } = new List<(string, string)>();
            public List<(string source, string target, string part)> Clones { get; }
                = new List<(string, string, string)>();
            public List<(string name, string pattern)> Applies { get; } = new List<(string, string)>();

            public string FailOnPart { get; set; }
            public bool FailOnCreate { get; set; }

            public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter)
            {
                SaveAsService.SourceDescriptor d;
                return Sources.TryGetValue(name, out d) ? d : null;
            }

            public bool TargetExists(string newName) => Existing.Contains(newName);

            public string CreateObject(string type, string newName)
            {
                Creates.Add((type, newName));
                if (FailOnCreate)
                    return "{\"status\":\"Error\",\"error\":\"create blew up\"}";
                Existing.Add(newName);
                return "{\"status\":\"Success\"}";
            }

            public string ClonePart(string sourceName, string newName, string partName, string typeFilter)
            {
                Clones.Add((sourceName, newName, partName));
                if (FailOnPart != null &&
                    string.Equals(FailOnPart, partName, StringComparison.OrdinalIgnoreCase))
                    return "{\"status\":\"Error\",\"error\":\"part write blew up\"}";
                return "{\"status\":\"Success\"}";
            }

            public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName)
            {
                SaveAsService.PatternInstanceDescriptor d;
                return Instances.TryGetValue(sourceName, out d) ? d : null;
            }

            public string ApplyWwpPattern(string newName, SaveAsService.PatternInstanceDescriptor sourceInstance)
            {
                Applies.Add((newName, sourceInstance?.PatternKey));
                return "{\"status\":\"Success\"}";
            }
        }

        private static FakeCloner ClonerWith(string sourceName, string type, params string[] parts)
        {
            var c = new FakeCloner();
            c.Sources[sourceName] = new SaveAsService.SourceDescriptor
            {
                Name = sourceName,
                Type = type,
                Parts = parts
            };
            return c;
        }

        [Fact]
        public void HappyPath_SinglePart_ClonesAllPartsAndReturnsNewName()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source", "Rules", "Variables");
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcACopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("ProcA", json["result"]?["sourceName"]?.ToString());
            Assert.Equal("ProcACopy", json["result"]?["created"]?["name"]?.ToString());
            Assert.Equal("Procedure", json["result"]?["created"]?["type"]?.ToString());
            var partsCloned = (JArray)json["result"]?["created"]?["partsCloned"];
            Assert.NotNull(partsCloned);
            Assert.Equal(3, partsCloned.Count);
            Assert.Equal("Source", partsCloned[0].ToString());
            Assert.Null(json["result"]?["patternInstance"]);
            Assert.Single(cloner.Creates);
            Assert.Equal(3, cloner.Clones.Count);
        }

        [Fact]
        public void TargetExists_ReturnsTargetExistsCodeWithHint()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source");
            cloner.Existing.Add("ProcACopy");
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcACopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("TargetExists", json["error"]?["code"]?.ToString());
            Assert.Contains("genexus_delete_object", json["error"]?["hint"]?.ToString() ?? "");
            Assert.Empty(cloner.Creates);
            Assert.Empty(cloner.Clones);
        }

        [Fact]
        public void TargetExists_WithOverwriteTrue_StillRefusesButHintMentionsFutureRevision()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source");
            cloner.Existing.Add("ProcACopy");
            var svc = new SaveAsService(cloner);

            var args = new JObject
            {
                ["name"] = "ProcA",
                ["newName"] = "ProcACopy",
                ["overwrite"] = true
            };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("TargetExists", json["error"]?["code"]?.ToString());
            Assert.Contains("reserved", json["error"]?["hint"]?.ToString() ?? "");
        }

        [Fact]
        public void SourceMissing_ReturnsNotFound()
        {
            var cloner = new FakeCloner(); // no sources registered
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "Nope", ["newName"] = "NopeCopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("NotFound", json["error"]?["code"]?.ToString());
            Assert.Contains("Nope", json["error"]?["message"]?.ToString() ?? "");
        }

        [Fact]
        public void IncludePatternInstance_OnNonPatternObject_OmitsPatternBlockNoError()
        {
            var cloner = ClonerWith("WebPanelA", "WebPanel", "Source", "WebForm");
            // No WWP instance registered → FindWwpInstance returns null.
            var svc = new SaveAsService(cloner);

            var args = new JObject
            {
                ["name"] = "WebPanelA",
                ["newName"] = "WebPanelACopy",
                ["includePatternInstance"] = true
            };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Null(json["result"]?["patternInstance"]);
            Assert.Empty(cloner.Applies);
        }

        [Fact]
        public void IncludePatternInstance_OnWwpHost_ClonesPatternAndReturnsBlock()
        {
            var cloner = ClonerWith("CustomerH", "Transaction", "Structure", "Rules");
            cloner.Instances["CustomerH"] = new SaveAsService.PatternInstanceDescriptor
            {
                PatternKey = "WorkWithPlus",
                HostName = "CustomerH"
            };
            var svc = new SaveAsService(cloner);

            var args = new JObject
            {
                ["name"] = "CustomerH",
                ["newName"] = "CustomerHCopy",
                ["includePatternInstance"] = true
            };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.True(json["result"]?["patternInstance"]?["applied"]?.ToObject<bool>() ?? false);
            Assert.Equal("WorkWithPlus", json["result"]?["patternInstance"]?["pattern"]?.ToString());
            Assert.Single(cloner.Applies);
            Assert.Equal("CustomerHCopy", cloner.Applies[0].name);
        }

        [Fact]
        public void DryRun_ReturnsPlanAndNeverCallsCloner()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source", "Rules");
            var svc = new SaveAsService(cloner);

            var args = new JObject
            {
                ["name"] = "ProcA",
                ["newName"] = "ProcACopy",
                ["dryRun"] = true
            };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("DryRun", json["code"]?.ToString());
            Assert.Equal("Procedure", json["result"]?["plan"]?["createType"]?.ToString());
            Assert.Equal("ProcACopy", json["result"]?["plan"]?["newName"]?.ToString());
            var parts = (JArray)json["result"]?["plan"]?["partsToClone"];
            Assert.NotNull(parts);
            Assert.Equal(2, parts.Count);

            // Critically: dispatcher / cloner never called.
            Assert.Empty(cloner.Creates);
            Assert.Empty(cloner.Clones);
            Assert.Empty(cloner.Applies);
        }

        // issue #45: a single inapplicable/failing part (e.g. "Layout" on a Procedure) must NOT
        // abort the clone before the important parts (Variables) are copied. The failing part is
        // skipped and reported under created.partsSkipped; the clone still succeeds.
        [Fact]
        public void PartFailure_NonFatal_SkipsPartAndClonesTheRest()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source", "Layout", "Variables");
            cloner.FailOnPart = "Layout";
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcACopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("ok", json["status"]?.ToString());
            var partsCloned = (JArray)json["result"]?["created"]?["partsCloned"];
            Assert.Contains(partsCloned, t => t.ToString() == "Source");
            Assert.Contains(partsCloned, t => t.ToString() == "Variables");
            Assert.DoesNotContain(partsCloned, t => t.ToString() == "Layout");
            var skipped = (JArray)json["result"]?["created"]?["partsSkipped"];
            Assert.NotNull(skipped);
            Assert.Contains(skipped, t => t["part"]?.ToString() == "Layout");
        }

        // When NOTHING clones (every part fails), the clone is a genuine failure with an undo hint.
        [Fact]
        public void AllPartsFail_ReturnsPartialFailureWithUndoHint()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source");
            cloner.FailOnPart = "Source";
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcACopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("PartialFailure", json["error"]?["code"]?.ToString());
            Assert.Contains("genexus_delete_object", json["error"]?["hint"]?.ToString() ?? "");
        }

        [Fact]
        public void SameSourceAndNewName_RejectedAsUsageError()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source");
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcA" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
        }
    }
}
