using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class BatchService
    {
        private readonly KbService _kbService;
        private readonly WriteService _writeService;
        private readonly PatchService _patchService;
        private readonly ObjectService _objectService;
        private readonly List<BatchItem> _buffer = new List<BatchItem>();

        public BatchService(KbService kbService, WriteService writeService, PatchService patchService, ObjectService objectService)
        {
            _kbService = kbService;
            _writeService = writeService;
            _patchService = patchService;
            _objectService = objectService;
        }

        public string BatchEdit(string target, JArray changes)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int count = 0;
                var results = new JArray();

                foreach (var change in changes)
                {
                    string part = change["part"]?.ToString() ?? "Source";
                    string mode = change["mode"]?.ToString() ?? "patch";
                    string content = change["content"]?.ToString();
                    string context = change["context"]?.ToString();
                    string operation = change["operation"]?.ToString() ?? "Replace";
                    int expectedCount = change["expectedCount"]?.ToObject<int?>() ?? 1;
                    bool dryRun = change["dryRun"]?.ToObject<bool?>() ?? false;
                    // Item 9 follow-up: forward replaceAll into the batch path so
                    // genexus_edit {targets:[...]} honours the same semantics as the
                    // single-target call. Without this, a per-change replaceAll is
                    // silently dropped and the call returns Ambiguous on N>1 matches.
                    bool replaceAll = change["replaceAll"]?.ToObject<bool?>() ?? false;

                    string result;
                    if (mode == "patch")
                    {
                        result = _patchService.ApplyPatch(target, part, operation, content, context, expectedCount, null, dryRun, verifyRollback: false, returnPostState: true, verbose: false, replaceAll: replaceAll);
                    }
                    else
                    {
                        result = _writeService.WriteObject(target, part, content);
                    }
                    
                    try {
                        results.Add(JObject.Parse(result));
                    } catch {
                        results.Add(new JObject { ["error"] = result });
                    }
                    count++;
                }

                return McpResponse.Ok(
                    target: target,
                    code: "BatchEditCompleted",
                    result: new JObject
                    {
                        ["count"] = count,
                        ["results"] = results,
                        ["duration"] = sw.ElapsedMilliseconds
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "BatchEditFailed",
                    message: "BatchEdit failed: " + ex.Message,
                    hint: "Check each result item for per-change errors. Retry individual changes that failed.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = target },
                        why: "Inspect the target object to confirm its parts are available before retrying.")),
                    target: target);
            }
        }

        public string ProcessBatch(string action, string name, string code)
        {
            if (action == "Add")
            {
                _buffer.Add(new BatchItem { Name = name, Code = code });
                return McpResponse.Ok(target: name, code: "BatchItemBuffered", result: new JObject { ["bufferedCount"] = _buffer.Count });
            }
            else if (action == "Commit")
            {
                int count = 0;
                foreach (var item in _buffer)
                {
                    _writeService.WriteObject(item.Name, "Source", item.Code);
                    count++;
                }
                _buffer.Clear();
                return McpResponse.Ok(target: name, code: "BatchCommitted", result: new JObject { ["count"] = count });
            }
            return McpResponse.Err(
                code: "UnknownBatchAction",
                message: $"Unknown batch action '{action}'.",
                hint: "Supported batch actions are Add and Commit.",
                nextSteps: new JArray(McpResponse.NextStep(
                    tool: "genexus_batch",
                    args: new JObject { ["action"] = "Add", ["name"] = name },
                    why: "Use action=Add to queue an item, then action=Commit to flush all buffered writes.")),
                target: name);
        }

        private class BatchItem { public string Name; public string Code; }

        public string MultiEdit(JArray items)
        {
            try
            {
                if (items == null || items.Count == 0)
                    return McpResponse.Err(
                        code: "NoItemsProvided",
                        message: "No items provided.",
                        hint: "Pass a non-empty items array where each entry has name and changes.");
                // no-nextStep: caller controls the items array; no specific tool call can resolve an empty input

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var allResults = new JArray();
                int totalChanges = 0;

                foreach (var item in items)
                {
                    string name = item["name"]?.ToString();
                    var changes = item["changes"] as JArray;
                    if (string.IsNullOrEmpty(name) || changes == null) continue;

                    string result = BatchEdit(name, changes);
                    try {
                        var parsed = JObject.Parse(result);
                        parsed["object"] = name;
                        allResults.Add(parsed);
                        // BatchEdit (above) only ever returns via McpResponse.Ok/Err — canonical
                        // envelope only, no legacy top-level "count". Read from result.count.
                        totalChanges += parsed["result"]?["count"]?.ToObject<int>() ?? 0;
                    } catch {
                        allResults.Add(new JObject { ["object"] = name, ["error"] = result });
                    }
                }

                return McpResponse.Ok(
                    code: "MultiEditCompleted",
                    result: new JObject
                    {
                        ["objectCount"] = items.Count,
                        ["totalChanges"] = totalChanges,
                        ["results"] = allResults,
                        ["duration"] = sw.ElapsedMilliseconds
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "MultiEditFailed",
                    message: "MultiEdit failed: " + ex.Message,
                    hint: "Check the results array for per-object errors and retry failed objects individually.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_edit",
                        args: new JObject { ["target"] = "<object-name>", ["part"] = "Source" },
                        why: "Retry a single-object edit to isolate which object caused the failure.")));
            }
        }
        /// <summary>
        /// Builds a paginated payload for lifecycle result items (errors list).
        /// Compatible with net48 (no Math.Clamp).
        /// </summary>
        public static JObject BuildResultPayload(IList<string> items, int page, int pageSize)
        {
            // Clamp inputs
            page = Math.Max(page, 1);
            pageSize = Math.Min(Math.Max(pageSize, 1), 200);

            int total = items == null ? 0 : items.Count;
            int skip = (page - 1) * pageSize;
            bool hasMore = skip + pageSize < total;

            var sliced = new JArray();
            if (items != null)
            {
                int end = Math.Min(skip + pageSize, total);
                for (int i = skip; i < end; i++)
                    sliced.Add(items[i]);
            }

            return new JObject
            {
                ["items"] = sliced,
                ["_meta"] = new JObject
                {
                    ["pagination"] = new JObject
                    {
                        ["total"] = total,
                        ["page"] = page,
                        ["page_size"] = pageSize,
                        ["has_more"] = hasMore
                    }
                }
            };
        }

        /// <summary>
        /// Builds a paginated payload for lifecycle status warnings.
        /// Compatible with net48 (no Math.Clamp).
        /// </summary>
        public static JObject BuildStatusPayload(IList<string> warnings, int page, int pageSize)
        {
            // Clamp inputs
            page = Math.Max(page, 1);
            pageSize = Math.Min(Math.Max(pageSize, 1), 200);

            int total = warnings == null ? 0 : warnings.Count;
            int skip = (page - 1) * pageSize;
            bool hasMore = skip + pageSize < total;

            var sliced = new JArray();
            if (warnings != null)
            {
                int end = Math.Min(skip + pageSize, total);
                for (int i = skip; i < end; i++)
                    sliced.Add(warnings[i]);
            }

            return new JObject
            {
                ["warnings"] = sliced,
                ["_meta"] = new JObject
                {
                    ["pagination"] = new JObject
                    {
                        ["total"] = total,
                        ["page"] = page,
                        ["page_size"] = pageSize,
                        ["has_more"] = hasMore
                    }
                }
            };
        }

        public string BatchRead(JArray items, string defaultPart = "Source", JArray requestedParts = null)
        {
            try
            {
                if (items == null || items.Count == 0)
                    return McpResponse.Err(
                        code: "NoItemsProvided",
                        message: "No items provided.",
                        hint: "Pass a non-empty items array where each entry is an object name (string) or an object with name and optionally part.");
                // no-nextStep: caller controls the items array; no specific tool call can resolve an empty input

                if (string.IsNullOrEmpty(defaultPart)) defaultPart = "Source";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = new JArray();

                foreach (var item in items)
                {
                    // genexus_read targets is an array of bare object-name strings
                    // (each item is a JValue), while the internal batch form allows
                    // {name, part} objects. Accept both so `targets:["A","B"]` no
                    // longer crashes with "Cannot access child value on JValue".
                    string name;
                    string part;
                    bool hasItemPart = false;
                    if (item is JObject itemObj)
                    {
                        name = itemObj["name"]?.ToString();
                        hasItemPart = !string.IsNullOrWhiteSpace(itemObj["part"]?.ToString());
                        part = itemObj["part"]?.ToString() ?? defaultPart;
                    }
                    else
                    {
                        name = item?.ToString();
                        part = defaultPart;
                    }
                    if (string.IsNullOrEmpty(name)) continue;

                    var selectedParts = requestedParts?
                        .Select(p => p?.ToString())
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToArray();
                    bool useFieldSelection = !hasItemPart && selectedParts != null && selectedParts.Length > 0;
                    string readResult = useFieldSelection
                        ? _objectService.ReadObjectSourceParts(name, selectedParts)
                        : _objectService.ReadObjectSource(name, part, null, null, "mcp");
                    try {
                        var parsed = JObject.Parse(readResult);
                        parsed["object"] = name;
                        if (useFieldSelection)
                            parsed["requestedParts"] = new JArray(selectedParts);
                        else
                            parsed["part"] = part;
                        results.Add(parsed);
                    } catch {
                        var failed = new JObject { ["object"] = name, ["error"] = readResult };
                        if (useFieldSelection)
                            failed["requestedParts"] = new JArray(selectedParts);
                        else
                            failed["part"] = part;
                        results.Add(failed);
                    }
                }

                return McpResponse.Ok(
                    code: "BatchReadCompleted",
                    result: new JObject
                    {
                        ["count"] = results.Count,
                        ["results"] = results,
                        ["duration"] = sw.ElapsedMilliseconds
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "BatchReadFailed",
                    message: "BatchRead failed: " + ex.Message,
                    hint: "Check each result item for per-object errors and retry the failed reads individually.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_read",
                        args: new JObject { ["name"] = "<object-name>", ["part"] = "Source" },
                        why: "Retry a single-object read to isolate which object caused the failure.")));
            }
        }
    }
}
