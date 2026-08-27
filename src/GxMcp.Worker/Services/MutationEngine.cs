using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public enum MutationMode
    {
        Xml,
        Patch,
        SemanticOps,
        JsonPatch,
        BulkWrite,
        AtomicCreate
    }

    public sealed class MutationRequest
    {
        public string Target { get; set; }
        public string Part { get; set; }
        public MutationMode Mode { get; set; } = MutationMode.Xml;
        public string Content { get; set; }
        public string Payload { get; set; }
        public JArray SemanticOps { get; set; }
        public JArray JsonPatch { get; set; }
        public string Find { get; set; }
        public string Replace { get; set; }
        public bool DryRun { get; set; }
        public string ExpectedVersion { get; set; }
        public bool AutoDeclareVariables { get; set; }
        public bool RollbackOnFailure { get; set; } = true;
        public JObject RawArgs { get; set; }
        public JArray Targets { get; set; }
    }

    public sealed class MutationResult
    {
        public bool Success { get; set; }
        public string ResponseJson { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public JObject Plan { get; set; }

        public static MutationResult FromJson(string json)
        {
            var res = new MutationResult { ResponseJson = json };
            try
            {
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var obj = JObject.Parse(json);
                    string status = obj["status"]?.ToString();
                    res.Success = string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
                    if (!res.Success)
                    {
                        res.ErrorCode = obj["code"]?.ToString() ?? obj["error"]?.ToString();
                        res.ErrorMessage = obj["message"]?.ToString() ?? obj["details"]?.ToString();
                    }
                    res.Plan = obj["plan"] as JObject;
                }
            }
            catch
            {
                res.Success = false;
            }
            return res;
        }
    }

    /// <summary>
    /// Deep Authoritative Mutation Engine for GeneXus KB objects.
    /// Encapsulates preflight validation, concurrency checks, in-memory patch execution,
    /// DryRun previews, SDK COM persistence, snapshot store, and automated rollback guards.
    /// </summary>
    public sealed class MutationEngine
    {
        private readonly WriteService _writeService;
        private readonly PatchService _patchService;
        private readonly ObjectService _objectService;

        public MutationEngine(WriteService writeService, PatchService patchService, ObjectService objectService)
        {
            _writeService = writeService ?? throw new ArgumentNullException(nameof(writeService));
            _patchService = patchService;
            _objectService = objectService;
        }

        public string Mutate(string mode, string target, JObject args, string payload = null)
        {
            if (args == null) args = new JObject();

            if (string.Equals(mode, "patch", StringComparison.OrdinalIgnoreCase))
            {
                if (_patchService == null)
                    return Models.McpResponse.Err(code: "PatchServiceUnavailable", message: "Patch service is not available.");

                string validateMode = args["validate"]?.ToString();
                bool dryRunArg = args["dryRun"]?.ToObject<bool?>() ?? false;
                bool validateOnly = string.Equals(validateMode, "only", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(validateMode, "validate-only", StringComparison.OrdinalIgnoreCase);

                return _patchService.ApplyPatch(
                    target,
                    args["part"]?.ToString(),
                    args["operation"]?.ToString(),
                    payload,
                    args["context"]?.ToString(),
                    args["expectedCount"]?.ToObject<int?>() ?? 1,
                    args["type"]?.ToString(),
                    dryRunArg || validateOnly,
                    args["verifyRollback"]?.ToObject<bool?>() ?? false,
                    args["return_post_state"]?.ToObject<bool?>() ?? true,
                    args["verbose"]?.ToObject<bool?>() ?? false,
                    args["replaceAll"]?.ToObject<bool?>() ?? false,
                    args["verifyMode"]?.ToString(),
                    args["baseVersion"]?.ToString(),
                    args["rollbackOnFailure"]?.ToObject<bool?>() ?? false,
                    args["autoDeclareVariables"]?.ToObject<bool?>() ?? args["autoInjectVariables"]?.ToObject<bool?>() ?? false);
            }

            if (string.Equals(mode, "semanticops", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "ops", StringComparison.OrdinalIgnoreCase))
            {
                return _writeService.ApplySemanticOps(args);
            }

            if (string.Equals(mode, "jsonpatch", StringComparison.OrdinalIgnoreCase))
            {
                return _writeService.ApplyJsonPatch(args);
            }

            if (string.Equals(mode, "bulk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "bulkwrite", StringComparison.OrdinalIgnoreCase) ||
                args["objects"] != null)
            {
                return _writeService.BulkWrite(args);
            }

            // Default: full XML write
            return _writeService.WriteObject(target, args);
        }

        public MutationResult Execute(MutationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var rawArgs = request.RawArgs != null ? (JObject)request.RawArgs.DeepClone() : new JObject();
            if (!string.IsNullOrEmpty(request.Part)) rawArgs["part"] = request.Part;
            if (!string.IsNullOrEmpty(request.Content)) rawArgs["content"] = request.Content;
            if (request.DryRun) rawArgs["dryRun"] = true;
            if (!string.IsNullOrEmpty(request.ExpectedVersion)) rawArgs["expectedVersion"] = request.ExpectedVersion;
            if (request.AutoDeclareVariables) rawArgs["autoDeclareVariables"] = true;
            if (request.Targets != null) rawArgs["targets"] = request.Targets;
            if (request.SemanticOps != null) rawArgs["ops"] = request.SemanticOps;
            if (request.JsonPatch != null) rawArgs["patch"] = request.JsonPatch;

            string modeStr = request.Mode.ToString().ToLowerInvariant();
            string jsonResp = Mutate(modeStr, request.Target, rawArgs, request.Payload ?? request.Content);
            return MutationResult.FromJson(jsonResp);
        }
    }
}