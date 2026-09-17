using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GxMcp.Worker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class TextMirrorService
    {
        private sealed class MirrorCatchupState
        {
            public string Root;
            public bool IncludeReferences;
            public bool IncludeVisualParts;
            public int Interval;
            public List<PendingChange> Batch;
            public List<PendingChange> ChangedBatch;
            public List<PendingChange> DeletedBatch;
            public bool EffectiveFull;
            public JObject Export;
            public JObject Deletion;
            public string ExportStatus = "ok";
            public string DeletionError;
            public int Acknowledged;
            public int RemovedFiles;
        }

        private string RunMirrorCatchup(bool full, JObject overrides)
        {
            if (_objectTextService == null)
                return McpResponse.Err(code: "TextMirrorUnavailable", message: "The text mirror is not connected to ObjectTextService.");
            if (!TryPrepareMirrorCatchup(full, overrides, out MirrorCatchupState state, out string preparationError))
                return preparationError;

            MarkMissingMirrorChangesAsDeleted(state.Batch);
            state.DeletedBatch = state.Batch.Where(change => change.Deleted).ToList();
            state.ChangedBatch = state.Batch.Where(change => !change.Deleted).ToList();
            if (!state.EffectiveFull && state.ChangedBatch.Count == 0 && state.DeletedBatch.Count == 0)
                return CompleteMirrorNoChanges();

            if (!ExportMirrorChanges(state)) return state.Export.ToString(Formatting.None);
            state.Acknowledged += AcknowledgeMirrorExport(state.Export);
            ProcessMirrorDeletions(state);
            if (state.EffectiveFull)
                state.RemovedFiles += RemoveOrphanedFiles(state.Root, state.IncludeReferences);
            return CompleteMirrorCatchup(state);
        }

        private bool TryPrepareMirrorCatchup(bool full, JObject overrides,
            out MirrorCatchupState state, out string errorResponse)
        {
            state = null;
            errorResponse = null;
            lock (_gate)
            {
                if (!_running)
                {
                    errorResponse = McpResponse.Err(code: "TextMirrorNotRunning", message: "The text mirror is not running.");
                    return false;
                }
                state = new MirrorCatchupState
                {
                    Root = _root,
                    IncludeReferences = _includeReferences,
                    IncludeVisualParts = overrides?["includeVisualParts"]?.ToObject<bool?>() ?? false,
                    Interval = _intervalMs,
                    Batch = _pending.Values
                        .OrderBy(change => change.Type, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(change => change.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    EffectiveFull = full || _pending.Count == 0
                        && (overrides?["full"]?.ToObject<bool?>() ?? false)
                };
            }
            return true;
        }

        private void MarkMissingMirrorChangesAsDeleted(List<PendingChange> batch)
        {
            SearchIndex loadedIndex = _indexCacheService?.TryGetLoadedIndex();
            if (loadedIndex == null) return;
            foreach (PendingChange change in batch)
            {
                if (change.Deleted) continue;
                bool stillExists = loadedIndex.FindByName(change.Name)
                    .Any(entry => string.Equals(entry.Type, change.Type, StringComparison.OrdinalIgnoreCase));
                if (!stillExists) change.Deleted = true;
            }
        }

        private bool ExportMirrorChanges(MirrorCatchupState state)
        {
            if (!state.EffectiveFull && state.ChangedBatch.Count == 0)
                return true;
            var exportArgs = new JObject
            {
                ["format"] = "native",
                ["mode"] = "newAndModified",
                ["outputPath"] = state.Root,
                ["overwrite"] = true,
                ["includeReferences"] = state.IncludeReferences,
                ["includeVisualParts"] = state.IncludeVisualParts
            };
            if (!state.EffectiveFull)
            {
                var targets = new JArray();
                foreach (PendingChange change in state.ChangedBatch)
                    targets.Add(new JObject { ["name"] = change.Name, ["type"] = change.Type });
                exportArgs["targets"] = targets;
            }
            state.Export = Parse(_objectTextService.Execute("export_kb_to_text", null, exportArgs, CancellationToken.None));
            state.ExportStatus = state.Export["status"]?.ToString();
            return !string.Equals(state.ExportStatus, "error", StringComparison.OrdinalIgnoreCase);
        }

        private int AcknowledgeMirrorExport(JObject export)
        {
            JArray objects = export?["result"]?["objects"] as JArray;
            if (objects == null) return 0;
            int acknowledged = 0;
            lock (_gate)
            {
                foreach (JObject item in objects.OfType<JObject>())
                {
                    string itemStatus = item["status"]?.ToString();
                    if (!string.Equals(itemStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(itemStatus, "skipped", StringComparison.OrdinalIgnoreCase)) continue;
                    string key = (item["type"]?.ToString() ?? string.Empty) + ":" + item["name"]?.ToString();
                    if (_pending.Remove(key)) acknowledged++;
                }
            }
            return acknowledged;
        }

        private void ProcessMirrorDeletions(MirrorCatchupState state)
        {
            if (state.DeletedBatch.Count == 0) return;
            state.Deletion = RemoveMirroredObjects(state.Root, state.DeletedBatch, out state.DeletionError);
            if (state.DeletionError != null)
            {
                lock (_gate) { _lastError = state.DeletionError; }
                return;
            }
            lock (_gate)
            {
                foreach (PendingChange change in state.DeletedBatch)
                {
                    string key = (change.Type ?? string.Empty) + ":" + change.Name;
                    if (_pending.Remove(key)) state.Acknowledged++;
                }
            }
        }

        private string CompleteMirrorNoChanges()
        {
            lock (_gate)
            {
                _lastTickUtc = DateTime.UtcNow;
                PersistWatermarkNoLock();
            }
            return McpResponse.Ok(code: "TextMirrorNoChanges", result: Snapshot());
        }

        private string CompleteMirrorCatchup(MirrorCatchupState state)
        {
            lock (_gate)
            {
                _processedChanges += state.Acknowledged;
                _processedBatches++;
                _removedFiles += state.RemovedFiles;
                _lastBatchSize = state.Batch.Count;
                _lastTickUtc = DateTime.UtcNow;
                _lastError = state.DeletionError
                    ?? (string.Equals(state.ExportStatus, "partial", StringComparison.OrdinalIgnoreCase)
                        ? state.Export?["result"]?["failed"]?.ToString() ?? "Some mirror objects failed."
                        : null);
                _watermarkUtc = _lastTickUtc;
                PersistWatermarkNoLock();
                if (_running && _pending.Count > 0) ScheduleTimerNoLock(state.Interval);
            }

            var result = Snapshot();
            result["full"] = state.EffectiveFull;
            result["acknowledged"] = state.Acknowledged;
            result["removedFiles"] = state.RemovedFiles;
            result["export"] = state.Export?["result"] ?? new JObject();
            if (state.Deletion != null) result["deletion"] = state.Deletion;
            return string.Equals(state.ExportStatus, "partial", StringComparison.OrdinalIgnoreCase) || state.DeletionError != null
                ? McpResponse.Partial(null, "TextMirrorCatchupPartial", result)
                : McpResponse.Ok(code: "TextMirrorCatchupCompleted", result: result);
        }
    }
}
