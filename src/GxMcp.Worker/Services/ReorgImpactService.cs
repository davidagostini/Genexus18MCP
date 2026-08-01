using System;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using System.IO;
using GenexusServices = Artech.Genexus.Common.Services;
using GenexusCommands = Artech.Genexus.Common.Commands;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_db action=reorg_impact — reorg / DDL impact preview (P1 #5). Read-only.
    ///
    /// Primary (cheap, default): <c>IModelInformationService</c> timestamps →
    /// <c>reorgLikelyNeeded</c> = a table changed after the last reorg. No specification runs.
    ///
    /// Deep (opt-in <c>deep=true</c>): <c>ISpecifierService.ImpactDatabase(model, options)</c>
    /// returns the authoritative <c>AnalysisResult</c> enum
    /// ({NoReorgNeeded, ReorganizationNeeded, NoReorgButShowImpact, ErrorInSomeTable, Exception}).
    /// This RUNS SPECIFICATION and is build-heavy — it is never the default and the envelope
    /// warns the caller. For the actual DDL SQL use genexus_db action=sql_ddl.
    ///
    /// Both services are non-<c>IGxService</c> → resolved via <see cref="SdkServiceLocator"/>.
    /// </summary>
    public class ReorgImpactService
    {
        private readonly KbService _kb;

        public ReorgImpactService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(JObject args)
        {
            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            var info = SdkServiceLocator.ConstructOrResolve<GenexusServices.IModelInformationService>(
                () => new Artech.Packages.Genexus.BL.Services.ModelInformationService());
            if (info == null)
                return McpResponse.Err(
                    code: "ModelInformationServiceUnavailable",
                    message: "The GeneXus SDK's IModelInformationService is not registered in this worker session.",
                    hint: "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            DateTime lastTbl = SafeDate(() => info.GetLastModifiedTableTimestamp(model));
            DateTime lastReorg = SafeDate(() => info.GetLastReorgTimestamp(model));
            bool likely = lastTbl != default && lastTbl > lastReorg;

            bool preview = string.Equals(args?["action"]?.ToString(), "reorg_preview", StringComparison.OrdinalIgnoreCase);
            var result = new JObject
            {
                ["lastModifiedTable"] = Iso(lastTbl),
                ["lastReorg"] = Iso(lastReorg),
                ["reorgLikelyNeeded"] = likely,
                ["source"] = "sdk:IModelInformationService",
                ["hint"] = preview
                    ? "Pass deep=true to run non-mutating Impact Analysis and capture the exact generated reorganization SQL artifact."
                    : "Cheap heuristic (timestamps). For the authoritative signal pass deep=true (runs specification, build-heavy)."
            };

            bool deep = args?["deep"]?.ToObject<bool?>() ?? false;
            DateTime impactStartedUtc = DateTime.UtcNow;
            if (deep)
            {
                var spec = SdkServiceLocator.ConstructOrResolve<GenexusServices.ISpecifierService>(
                    () => new Artech.Packages.Specifier.Services.SpecifierService());
                if (spec == null)
                {
                    result["deepError"] = "ISpecifierService unavailable";
                }
                else
                {
                    try
                    {
                        // ImpactAnalysis | CreateAnalysis: analyse the DB impact without executing a reorg.
                        var options = GenexusCommands.BuildOptions.ImpactAnalysis | GenexusCommands.BuildOptions.CreateAnalysis;
                        var analysis = spec.ImpactDatabase(model, options);
                        result["deepAnalysis"] = analysis.ToString();
                        result["reorgNeeded"] = string.Equals(analysis.ToString(), "ReorganizationNeeded", StringComparison.OrdinalIgnoreCase);
                        result["deepNote"] = "Ran ISpecifierService.ImpactDatabase (specification). AnalysisResult enum reported.";
                    }
                    catch (Exception ex)
                    {
                        result["deepError"] = ex.Message;
                    }
                }
            }

            if (preview)
            {
                bool currentRun;
                string artifact = ReorgSqlPreview.FindLatestArtifact(_kb.GetKbPath(), impactStartedUtc, out currentRun);
                if (!string.IsNullOrEmpty(artifact))
                {
                    string sql = File.ReadAllText(artifact);
                    bool exact = deep && currentRun;
                    JObject plan = ReorgSqlPreview.Parse(sql, exact);
                    plan["artifact"] = artifact;
                    plan["artifactFromCurrentImpact"] = currentRun;
                    plan["source"] = exact ? "sdk:ImpactDatabase generated artifact" : "existing generated artifact";
                    plan["beforeDdl"] = JValue.CreateNull();
                    plan["proposedDdl"] = plan["ddl"].DeepClone();
                    if (!exact)
                        plan["warning"] = "The SQL artifact was not generated by this preview run; it is shown as cached evidence and is not labelled effective DDL.";
                    result["preview"] = plan;
                }
                else
                {
                    result["preview"] = new JObject
                    {
                        ["ddlEffective"] = false,
                        ["ddl"] = new JArray(),
                        ["affectedTables"] = new JArray(),
                        ["affectedColumns"] = new JArray(),
                        ["indexes"] = new JArray(),
                        ["destructiveConversions"] = new JArray(),
                        ["warning"] = deep
                            ? "Impact Analysis completed but no reorganization SQL artifact was found below the KB path; exact DDL is unavailable."
                            : "No cached reorganization SQL artifact was found. Re-run with deep=true for non-mutating Impact Analysis."
                    };
                }
                result["nonMutating"] = true;
                result["logicalPhysicalDivergence"] = likely;
            }

            return McpResponse.Ok(code: preview ? "ReorgPreviewRetrieved" : "ReorgImpactRetrieved", result: result);
        }

        private static DateTime SafeDate(Func<DateTime> f) { try { return f(); } catch { return default; } }
        private static JToken Iso(DateTime d) => d == default ? (JToken)JValue.CreateNull() : d.ToUniversalTime().ToString("o");
    }
}
