using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class ObjectTextService
    {
        private sealed class LegacyFileBatchState
        {
            public string Root;
            public TextBatchOptions Options;
            public List<TextFileEntry> Files;
            public JArray Results = new JArray();
            public int Succeeded;
            public int Failed;
            public bool StoppedOnError;
            public string CancelledResponse;
        }

        private string RunLegacyImport(string target, JObject args, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return BuildCancelled("import", null, new JArray(), 0, 0, 0);
            if (!TryPrepareLegacyFileBatch(target, args, false, out LegacyFileBatchState state,
                out string errorCode, out string errorMessage, out string errorTarget))
                return McpResponse.Err(code: errorCode, message: errorMessage, target: errorTarget);
            if (state.Options.ListOnly) return BuildLegacyFilePlan("import", state);
            ImportLegacyFiles(state, args, ct);
            if (state.CancelledResponse != null) return state.CancelledResponse;
            return BuildLegacyFileResponse("import", state, "ObjectTextImportPartial", "ObjectTextImportDryRun", "ObjectTextImportCompleted", true);
        }

        private string RunLegacyValidation(string target, JObject args, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return BuildCancelled("validate", null, new JArray(), 0, 0, 0);
            if (!TryPrepareLegacyFileBatch(target, args, false, out LegacyFileBatchState state,
                out string errorCode, out string errorMessage, out string errorTarget))
                return McpResponse.Err(code: errorCode, message: errorMessage, target: errorTarget);
            if (state.Options.ListOnly) return BuildLegacyFilePlan("validate", state);
            ValidateLegacyFiles(state, args, ct);
            if (state.CancelledResponse != null) return state.CancelledResponse;
            return BuildLegacyFileResponse("validate", state, "ObjectTextValidationPartial", "ObjectTextValidationCompleted", "ObjectTextValidationCompleted", false);
        }

        private bool TryPrepareLegacyFileBatch(string target, JObject args, bool defaultDryRun,
            out LegacyFileBatchState state, out string errorCode, out string errorMessage, out string errorTarget)
        {
            state = null;
            errorCode = null;
            errorMessage = null;
            errorTarget = null;
            string inputPath = args["inputPath"]?.ToString() ?? args["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                errorCode = "InputPathRequired";
                errorMessage = "inputPath is required for Object Text file operations.";
                return false;
            }

            if (!TryLoadTextFiles(inputPath, target, args, out string root, out List<TextFileEntry> files, out string loadError))
            {
                errorCode = "ObjectTextManifestInvalid";
                errorMessage = loadError;
                errorTarget = inputPath;
                return false;
            }
            if (!ApplyManifestSelector(files, args, target))
            {
                errorCode = "NoObjectsMatched";
                errorMessage = "No Object Text manifest entries matched the selector.";
                return false;
            }
            if (!TextBatchOptions.TryParse(args, defaultDryRun, false, false, true, false, false, true,
                out TextBatchOptions options, out string optionError))
            {
                errorCode = "InvalidTextOperationOption";
                errorMessage = optionError;
                return false;
            }
            state = new LegacyFileBatchState
            {
                Root = root,
                Options = options,
                Files = files
            };
            return true;
        }

        private static string BuildLegacyFilePlan(string operation, LegacyFileBatchState state)
        {
            int offset = Math.Min(state.Options.Skip, state.Files.Count);
            var objects = new JArray();
            for (int index = 0; index < state.Files.Count; index++)
            {
                TextFileEntry item = state.Files[index];
                objects.Add(new JObject
                {
                    ["name"] = item.Name,
                    ["type"] = item.Type,
                    ["part"] = item.Part,
                    ["file"] = item.File,
                    ["status"] = index < offset ? "skipped" : "planned",
                    ["reason"] = index < offset ? "skip" : "listOnly"
                });
            }
            return McpResponse.Ok(code: operation == "import" ? "ObjectTextImportPlan" : "ObjectTextValidationPlan", result: new JObject
            {
                ["operation"] = operation,
                ["listOnly"] = true,
                ["root"] = state.Root,
                ["planned"] = state.Files.Count,
                ["filesAvailable"] = state.Files.Count,
                ["skipped"] = offset,
                ["remaining"] = Math.Max(0, state.Files.Count - offset),
                ["objects"] = objects
            });
        }

        private void ImportLegacyFiles(LegacyFileBatchState state, JObject args, CancellationToken ct)
        {
            for (int index = 0; index < state.Files.Count; index++)
            {
                if (ct.IsCancellationRequested)
                {
                    state.CancelledResponse = BuildCancelled("import", state.Root, state.Results,
                        state.Succeeded, state.Failed, state.Files.Count);
                    return;
                }
                TextFileEntry item = state.Files[index];
                if (index < state.Options.Skip)
                {
                    AddLegacyFileSkip(state, item);
                    continue;
                }
                if (!TextTreePath.TryResolveUnderRoot(state.Root, item.File, out string fullPath) || !File.Exists(fullPath))
                {
                    state.Failed++;
                    state.Results.Add(new JObject
                    {
                        ["name"] = item.Name,
                        ["type"] = item.Type,
                        ["file"] = item.File,
                        ["status"] = "error",
                        ["code"] = "InputFileNotFound"
                    });
                    if (state.Options.StopOnError) { state.StoppedOnError = true; return; }
                    continue;
                }
                JObject parsed;
                try
                {
                    parsed = ParseResult(_objectService.ImportObjectFromText(
                        item.Name,
                        fullPath,
                        item.Part ?? args["part"]?.ToString(),
                        item.Type ?? args["type"]?.ToString(),
                        state.Options.DryRun,
                        state.Options.ForceSave));
                }
                catch (Exception ex)
                {
                    parsed = new JObject
                    {
                        ["status"] = "error",
                        ["code"] = "ObjectTextImportFailed",
                        ["message"] = ex.Message
                    };
                }
                state.Results.Add(BuildItemResult(item, fullPath, parsed));
                if (IsSuccess(parsed)) state.Succeeded++;
                else
                {
                    state.Failed++;
                    if (state.Options.StopOnError) { state.StoppedOnError = true; return; }
                }
            }
        }

        private void ValidateLegacyFiles(LegacyFileBatchState state, JObject args, CancellationToken ct)
        {
            for (int index = 0; index < state.Files.Count; index++)
            {
                if (ct.IsCancellationRequested)
                {
                    state.CancelledResponse = BuildCancelled("validate", state.Root, state.Results,
                        state.Succeeded, state.Failed, state.Files.Count);
                    return;
                }
                TextFileEntry item = state.Files[index];
                if (index < state.Options.Skip)
                {
                    AddLegacyFileSkip(state, item);
                    continue;
                }
                string fullPath;
                string error;
                if (!TextTreePath.TryResolveUnderRoot(state.Root, item.File, out fullPath) || !File.Exists(fullPath))
                {
                    error = "File does not exist under the manifest root.";
                }
                else
                {
                    error = ValidateTextFile(fullPath, item.Part ?? args["part"]?.ToString());
                    if (error == null && _objectService != null)
                    {
                        try
                        {
                            JObject probe = ParseResult(_objectService.ImportObjectFromText(
                                item.Name, fullPath, item.Part ?? args["part"]?.ToString(),
                                item.Type ?? args["type"]?.ToString(), dryRun: true));
                            if (!IsSuccess(probe)) error = probe["error"]?.ToString() ?? probe["message"]?.ToString() ?? "SDK validation failed.";
                        }
                        catch (Exception ex) { error = "SDK validation failed: " + ex.Message; }
                    }
                }
                var parsed = new JObject
                {
                    ["name"] = item.Name,
                    ["type"] = item.Type,
                    ["part"] = item.Part ?? args["part"]?.ToString() ?? "Source",
                    ["file"] = item.File,
                    ["valid"] = error == null,
                    ["status"] = error == null ? "ok" : "error"
                };
                if (error == null) state.Succeeded++;
                else
                {
                    parsed["message"] = error;
                    state.Failed++;
                }
                state.Results.Add(parsed);
                if (error != null && state.Options.StopOnError) { state.StoppedOnError = true; return; }
            }
        }

        private static void AddLegacyFileSkip(LegacyFileBatchState state, TextFileEntry item)
        {
            state.Results.Add(new JObject
            {
                ["name"] = item.Name,
                ["type"] = item.Type,
                ["part"] = item.Part,
                ["file"] = item.File,
                ["status"] = "skipped",
                ["reason"] = "skip"
            });
        }

        private static string BuildLegacyFileResponse(string operation, LegacyFileBatchState state,
            string partialCode, string successCode, string completedCode, bool includeImportFields)
        {
            var result = BuildAggregateResult(operation, state.Root, state.Results, state.Files.Count,
                state.Succeeded, state.Failed);
            result["listOnly"] = false;
            if (includeImportFields)
            {
                result["dryRun"] = state.Options.DryRun;
                result["forceSave"] = state.Options.ForceSave;
            }
            result["filesAvailable"] = state.Files.Count;
            result["skipped"] = Math.Min(state.Options.Skip, state.Files.Count);
            result["remaining"] = Math.Max(0, state.Files.Count - Math.Min(state.Options.Skip, state.Files.Count) - state.Succeeded - state.Failed);
            result["stoppedOnError"] = state.StoppedOnError;
            return state.Failed > 0
                ? McpResponse.Partial(null, partialCode, result)
                : McpResponse.Ok(code: operation == "import" && state.Options.DryRun ? successCode : completedCode, result: result);
        }
    }
}
