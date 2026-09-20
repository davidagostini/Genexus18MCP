using System.Collections.Generic;
using System.Text;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public static class DryRunPlanBuilder
    {
        public static PlanResponse Build(string targetName, string beforeXml, string afterXml)
        {
            return Build(targetName, beforeXml, afterXml, null);
        }

        public static PlanResponse Build(string targetName, string beforeXml, string afterXml, KbValidationService validator)
        {
            var plan = new PlanResponse();
            plan.TouchedObjects.Add(new TouchedObject {
                Type = DetectType(beforeXml),
                Name = targetName,
                Op = "modify"
            });
            plan.XmlDiff = UnifiedDiff(beforeXml, afterXml);

            if (validator != null)
            {
                var broken = validator.AnalyzeImpact(targetName, afterXml);
                if (broken != null && broken.Count > 0)
                    plan.BrokenRefs.AddRange(broken);
                if (!validator.IsImpactAnalysisAvailable())
                {
                    plan.Warnings.Add(new PlanWarning
                    {
                        Code = "impactAnalysisUnavailable",
                        Message = "Impact analysis was not run because the active object index is not loaded; brokenRefs may be incomplete.",
                        Path = "/plan/brokenRefs"
                    });
                }
            }
            else
            {
                plan.Warnings.Add(new PlanWarning
                {
                    Code = "impactAnalysisUnavailable",
                    Message = "Impact analysis was not run; brokenRefs may be incomplete.",
                    Path = "/plan/brokenRefs"
                });
            }

            return plan;
        }

        public static JObject BuildEnvelope(string target, string beforeXml, string afterXml, string mode, KbValidationService validator = null)
        {
            return new JObject {
                ["isError"] = false,
                ["meta"] = new JObject {
                    ["dryRun"] = true,
                    ["tool"] = "genexus_edit",
                    ["mode"] = mode,
                    ["schemaVersion"] = "mcp-axi/2"
                },
                ["plan"] = Build(target, beforeXml, afterXml, validator).ToJson()
            };
        }

        private static string DetectType(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return "";
            int open = xml.IndexOf('<');
            if (open < 0) return "";
            // Skip XML declaration <? ... ?>
            if (open + 1 < xml.Length && xml[open + 1] == '?')
            {
                int next = xml.IndexOf('<', open + 1);
                if (next < 0) return "";
                open = next;
            }
            int space = xml.IndexOfAny(new[] { ' ', '>', '\n', '\r', '\t' }, open + 1);
            if (space < 0) return "";
            return xml.Substring(open + 1, space - open - 1);
        }

        // Naive line-based diff — placeholder. Good-enough for v2.0.0; replace with
        // Myers/DiffPlex when agent workflows demand readable diffs.
        private static string UnifiedDiff(string a, string b)
        {
            var aLines = (a ?? "").Replace("\r\n", "\n").Split('\n');
            var bLines = (b ?? "").Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            sb.Append("--- before\n+++ after\n@@\n");
            foreach (var line in aLines) sb.Append("-").Append(line).Append("\n");
            foreach (var line in bLines) sb.Append("+").Append(line).Append("\n");
            return sb.ToString();
        }
    }
}
