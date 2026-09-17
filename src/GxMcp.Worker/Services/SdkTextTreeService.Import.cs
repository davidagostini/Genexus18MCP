using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class SdkTextTreeService
    {
        private string Import(string target, JObject args, CancellationToken ct)
            => RunNativeImport(target, args ?? new JObject(), ct);

        private Dictionary<string, string> TryReadPartsForRollback(string type, string name, IEnumerable<string> parts)
        {
            try
            {
                string raw = _objectService.ReadObjectSourceParts(type + ":" + name, parts, type);
                JObject response = ParseEnvelope(raw);
                JObject values = response["parts"] as JObject;
                if (values == null) return null;
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in values.Properties())
                {
                    string value = property.Value.Type == JTokenType.String
                        ? property.Value.ToString()
                        : (property.Value as JObject)?["source"]?.ToString();
                    if (value != null) result[property.Name] = value;
                }
                return result;
            }
            catch { return null; }
        }

        internal static string ValidateRollbackPrecondition(bool dryRun, bool rollbackOnFailure, int partCount, bool snapshotComplete)
        {
            if (dryRun || !rollbackOnFailure || partCount <= 1 || snapshotComplete) return null;
            return "RollbackSnapshotUnavailable";
        }

        private JObject RestorePartsAfterFailure(string type, string name, Dictionary<string, string> priorParts, JArray partResults)
        {
            var errors = new JArray();
            int attempted = 0;
            int restored = 0;
            bool missingPriorPart = false;
            if (priorParts != null)
            {
                foreach (JObject written in partResults.OfType<JObject>().Where(item =>
                    string.Equals(item["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!priorParts.ContainsKey(written["part"]?.ToString())) missingPriorPart = true;
                }
                foreach (KeyValuePair<string, string> prior in priorParts)
                {
                    bool wasWritten = partResults.OfType<JObject>().Any(item =>
                        string.Equals(item["part"]?.ToString(), prior.Key, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item["status"]?.ToString(), "ok", StringComparison.OrdinalIgnoreCase));
                    if (!wasWritten) continue;
                    attempted++;
                    JObject response = ParseEnvelope(_objectService.ImportObjectPartText(name, prior.Key, prior.Value, type, false, true));
                    if (IsSuccess(response)) restored++;
                    else errors.Add(new JObject { ["part"] = prior.Key, ["message"] = response["error"]?.ToString() ?? response["message"]?.ToString() ?? "Rollback write failed." });
                }
            }
            return new JObject
            {
                ["attempted"] = attempted,
                ["restored"] = restored,
                ["reconciliationRequired"] = priorParts == null || missingPriorPart || errors.Count > 0,
                ["errors"] = errors
            };
        }

        private void ImportVisualCompanions(
            string root,
            JObject args,
            bool dryRun,
            bool forceSave,
            bool stopOnError,
            Dictionary<string, NativeImportItem> importedByStem,
            JArray results,
            ref int succeeded,
            ref int failed,
            CancellationToken ct)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.*.xml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (ct.IsCancellationRequested) return;
                string filename = Path.GetFileName(file) ?? string.Empty;
                bool report = filename.EndsWith(".report.xml", StringComparison.OrdinalIgnoreCase);
                bool web = filename.EndsWith(".web.xml", StringComparison.OrdinalIgnoreCase);
                if (!report && !web) continue;
                string stem = filename.Substring(0, filename.Length - (report ? ".report.xml" : ".web.xml").Length);
                NativeImportItem item = importedByStem.FirstOrDefault(pair =>
                    string.Equals(pair.Key, stem, StringComparison.OrdinalIgnoreCase)
                    || pair.Key.EndsWith("__" + stem, StringComparison.OrdinalIgnoreCase)).Value;
                if (item == null) continue;

                string content;
                try { content = File.ReadAllText(file); }
                catch (Exception ex)
                {
                    failed++;
                    results.Add(new JObject { ["file"] = MakeRelative(root, file), ["status"] = "error", ["message"] = ex.Message });
                    if (stopOnError) return;
                    continue;
                }
                string part = report ? "Layout" : "WebForm";
                JObject response = ParseEnvelope(_objectService.ImportObjectPartText(item.Name, part, content, item.Type, dryRun, forceSave));
                if (IsSuccess(response))
                {
                    succeeded++;
                    results.Add(new JObject { ["name"] = item.Name, ["type"] = item.Type, ["part"] = part, ["file"] = MakeRelative(root, file), ["status"] = "ok", ["companion"] = true });
                }
                else
                {
                    failed++;
                    results.Add(BuildImportError(item.Name, item.Type, file, response));
                    if (stopOnError) return;
                }
            }
        }

    }
}
