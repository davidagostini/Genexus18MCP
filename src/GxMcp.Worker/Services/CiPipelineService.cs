using System;
using Artech.Architecture.Common.Objects;
#if HAS_TEAMDEV_CI
using Artech.Architecture.Common.Services.TeamDevData.Client;
using Ci = GeneXus.TeamDevClient.Architecture.BL.Services;
#endif
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_gxserver action=pipeline_* — CI pipelines over the SDK's
    /// <c>IContinuousIntegrationService</c> (P1 #4). Extends the GXserver surface.
    ///
    /// Actions: pipeline_list (read) · pipeline_runs (read; needs project) · pipeline_run
    /// (DESTRUCTIVE — triggers a build; confirm=true) · pipeline_abort (DESTRUCTIVE) ·
    /// pipeline_output (read; project + buildId).
    ///
    /// Needs a GXserver-linked KB: <c>TeamDevelopmentData(model)</c> carries the link; when
    /// the KB isn't linked the SDK calls throw and we surface <c>{connected:false}</c>,
    /// mirroring GxServerSyncService. <c>IContinuousIntegrationService</c> implements
    /// <c>IGxService</c> → resolved via <see cref="SdkServiceResolver"/>.
    /// </summary>
    public class CiPipelineService
    {
        private readonly KbService _kb;

        public CiPipelineService(KbService kb)
        {
            _kb = kb;
        }

        public string Run(string action, JObject args)
        {
#if !HAS_TEAMDEV_CI
            return McpResponse.Err(
                code: "ContinuousIntegrationServiceUnavailable",
                message: "Continuous Integration pipelines are not supported in this GeneXus version.",
                hint: "CI pipeline actions require GeneXus 17+ with GXserver CI support.");
#else
            action = (action ?? "").Trim().ToLowerInvariant();

            switch (action)
            {
                case "pipeline_list":
                case "pipeline_runs":
                case "pipeline_output":
                case "pipeline_run":
                case "pipeline_abort":
                    break;
                default:
                    return McpResponse.Err("BadAction", "Unknown pipeline action '" + action + "'.",
                        "Valid: pipeline_list|pipeline_runs|pipeline_output|pipeline_run|pipeline_abort.");
            }

            // Fail-fast: a static precondition must not depend on KB state.
            if ((action == "pipeline_run" || action == "pipeline_abort")
                && !(args?["confirm"]?.ToObject<bool?>() ?? false))
                return McpResponse.Err("ConfirmRequired",
                    action == "pipeline_run"
                        ? "pipeline_run triggers a build; pass confirm=true."
                        : "pipeline_abort cancels a running build; pass confirm=true.",
                    "Set confirm=true.");

            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            var svc = SdkServiceResolver.Resolve<Ci.IContinuousIntegrationService>();
            if (svc == null)
                return McpResponse.Err(
                    code: "ContinuousIntegrationServiceUnavailable",
                    message: "The GeneXus SDK's IContinuousIntegrationService is not registered in this worker session.",
                    hint: "The Team Development client package may not be loaded. Restart the worker (genexus_worker_reload mode=hard) and retry.");

            TeamDevelopmentData data;
            try { data = new TeamDevelopmentData(model); }
            catch (Exception ex)
            {
                return NotConnected(ex.Message);
            }

            string project = args?["project"]?.ToString();
            try
            {
                switch (action)
                {
                    case "pipeline_list":
                        return McpResponse.Ok(code: "PipelinesRetrieved", result: new JObject
                        {
                            ["pipelines"] = Safe(() => svc.GetPipelines(data)),
                            ["source"] = "sdk:IContinuousIntegrationService.GetPipelines"
                        });

                    case "pipeline_runs":
                        if (string.IsNullOrWhiteSpace(project)) return NeedProject();
                        return McpResponse.Ok(code: "PipelineRunsRetrieved", result: new JObject
                        {
                            ["project"] = project,
                            ["runs"] = Safe(() => svc.GetPipelineRuns(data, project)),
                            ["source"] = "sdk:IContinuousIntegrationService.GetPipelineRuns"
                        });

                    case "pipeline_output":
                    {
                        if (string.IsNullOrWhiteSpace(project)) return NeedProject();
                        if (args?["buildId"] == null)
                            return McpResponse.Err("BadArgs", "pipeline_output requires buildId.", "Pass buildId=<int> (see pipeline_runs).");
                        int buildId = args["buildId"].ToObject<int?>() ?? 0;
                        return McpResponse.Ok(code: "PipelineRunOutputRetrieved", result: new JObject
                        {
                            ["project"] = project,
                            ["buildId"] = buildId,
                            ["output"] = svc.GetPipelineRunOutput(data, project, buildId),
                            ["source"] = "sdk:IContinuousIntegrationService.GetPipelineRunOutput"
                        });
                    }

                    case "pipeline_run":
                    {
                        if (string.IsNullOrWhiteSpace(project)) return NeedProject();
                        if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                            return McpResponse.Err("ConfirmRequired", "pipeline_run triggers a build; pass confirm=true.", "Set confirm=true to run the pipeline.");
                        bool rebuild = args?["rebuild"]?.ToObject<bool?>() ?? false;
                        bool runTests = args?["runTests"]?.ToObject<bool?>() ?? false;
                        try { svc.RunPipeline(data, project, rebuild, runTests); }
                        catch (Exception ex)
                        {
                            return McpResponse.Err(
                                code: "PipelineRunFailed",
                                message: ex.Message,
                                hint: "The build trigger call failed. Verify the project name and GXserver session, then retry.");
                        }
                        return McpResponse.Ok(code: "PipelineRunTriggered", result: new JObject { ["project"] = project, ["rebuild"] = rebuild, ["runTests"] = runTests });
                    }

                    case "pipeline_abort":
                    {
                        if (string.IsNullOrWhiteSpace(project)) return NeedProject();
                        if (!(args?["confirm"]?.ToObject<bool?>() ?? false))
                            return McpResponse.Err("ConfirmRequired", "pipeline_abort cancels a running build; pass confirm=true.", "Set confirm=true to abort.");
                        try { svc.AbortRunPipeline(data, project); }
                        catch (Exception ex)
                        {
                            return McpResponse.Err(
                                code: "PipelineAbortFailed",
                                message: ex.Message,
                                hint: "The build cancel call failed. Verify the project name and GXserver session, then retry.");
                        }
                        return McpResponse.Ok(code: "PipelineAborted", result: new JObject { ["project"] = project });
                    }

                    default:
                        return McpResponse.Err("BadAction", "Unknown pipeline action '" + action + "'.",
                            "Valid: pipeline_list|pipeline_runs|pipeline_output|pipeline_run|pipeline_abort.");
                }
            }
            catch (Exception ex)
            {
                // A KB that isn't GXserver-linked (or an unauthenticated session) surfaces here.
                return NotConnected(ex.Message);
            }
#endif
        }

#if HAS_TEAMDEV_CI
        private static string NeedProject()
            => McpResponse.Err("BadArgs", "This pipeline action requires project.", "Pass project=<pipeline/project name> (see pipeline_list).");

        private static string NotConnected(string detail)
            => McpResponse.Ok(code: "PipelineNotConnected", result: new JObject
            {
                ["connected"] = false,
                ["detail"] = detail,
                ["hint"] = "This KB is not linked to a GXserver, or the CI session needs credentials (GXMCP_TEAMDEV_USER/PASSWORD)."
            });

        // Best-effort serialization of the SDK's typed CI data objects (public props).
        private static JToken Safe(Func<object> f)
        {
            try { var v = f(); return v == null ? JValue.CreateNull() : JToken.FromObject(v); }
            catch (Exception ex) { return new JObject { ["_serializeError"] = ex.Message }; }
        }
#endif
    }
}
