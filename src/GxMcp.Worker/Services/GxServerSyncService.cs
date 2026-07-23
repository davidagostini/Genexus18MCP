using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// GxServer (Team Development) sync-state surface for genexus_gxserver.
    ///
    /// Primary path is the GeneXus SDK: it resolves
    /// <c>ITeamDevClientService</c> (the model-level Team Development service)
    /// and answers connection / pending / conflict queries from the live KB —
    /// the same source the IDE's Team Development tab reads. This replaces the
    /// old filesystem-heuristic that produced false "not connected" results on
    /// KBs that ARE linked to a GeneXus Server (the link lives in the KB
    /// metadata DB, not in well-known files on disk).
    ///
    /// If the Team Development service isn't loaded in the worker session, or
    /// the SDK throws, it falls back to the legacy file-probe envelopes so the
    /// caller still gets a stable (if coarser) answer.
    ///
    /// commit/update/lock/resolve are WRITE actions and delegate to the
    /// sibling <see cref="GxServerWriteService"/> instead — kept separate so
    /// this read path stays a pure query surface.
    /// </summary>
    public class GxServerSyncService
    {
        private readonly KbService _kb;
        private readonly GxServerWriteService _write;

        public GxServerSyncService(KbService kb)
        {
            _kb = kb;
            _write = new GxServerWriteService(kb);
        }

        public string Run(JObject args)
        {
            string action = args?["action"]?.ToString();
            if (string.IsNullOrWhiteSpace(action)) action = "status";
            action = action.Trim().ToLowerInvariant();

            switch (action)
            {
                case "commit":
                case "update":
                case "lock":
                case "resolve":
                    return _write.Run(action, args ?? new JObject());
            }

            string kbPath = _kb?.GetKbPath();
            string kbAlias = Environment.GetEnvironmentVariable("GX_KB_ALIAS")
                             ?? (string.IsNullOrEmpty(kbPath) ? null : Path.GetFileName(kbPath.TrimEnd('\\', '/')));

            switch (action)
            {
                case "status":
                case "pending":
                case "ignored":
                case "conflicts":
                case "history":
                    break;
                default:
                    return McpResponse.Err(
                        code: "BadAction",
                        message: "Unknown action '" + action + "'. Expected one of: status, pending, ignored, conflicts, history, commit, update, lock, resolve.",
                        hint: "Pass action=status, action=pending, action=ignored, action=conflicts, action=history, action=commit, action=update, action=lock, or action=resolve.",
                        nextSteps: new JArray { McpResponse.NextStep("genexus_gxserver", new JObject { ["action"] = "status" }, "Query connection status.") });
            }

            // Primary: SDK-backed answer from the live KB. Returns null when the
            // Team Development service is unavailable or any SDK call throws, in
            // which case we drop to the legacy file-heuristic below.
            int limit = args?["limit"]?.ToObject<int?>() ?? 10;
            string sdk = TrySdkEnvelope(action, kbAlias, limit);
            if (sdk != null) return sdk;

            switch (action)
            {
                case "status": return StatusEnvelope(kbPath, kbAlias);
                case "pending": return PendingEnvelope(kbPath);
                case "ignored": return IgnoredEnvelope(kbPath);
                case "conflicts": return ConflictsEnvelope(kbPath);
                default: return HistoryEnvelope(kbPath, limit);
            }
        }

        // ----- SDK-backed path (authoritative) -----

        /// <summary>
        /// Builds the response from <see cref="ITeamDevClientService"/> against
        /// the open KB. Returns null (caller falls back to the file-heuristic)
        /// when the service isn't registered in this worker or the SDK throws.
        /// </summary>
        private string TrySdkEnvelope(string action, string kbAlias, int limit)
        {
            KnowledgeBase kb;
            ITeamDevClientService svc;
            try
            {
                kb = _kb?.GetKB() as KnowledgeBase;
                if (kb == null) return null;
                // Self-healing resolve (issue #32 idiom): a single TryGetService can return null
                // when the Team-Dev service lags after a worker respawn, needlessly dropping to
                // the coarse file-heuristic. The resolver retries + forces resolution first.
                svc = GxMcp.Worker.Helpers.SdkServiceResolver.Resolve<ITeamDevClientService>();
                if (svc == null) return null;
            }
            catch { return null; }

            try
            {
                bool linked = svc.IsLinkedKB(kb);
                var model = kb.DesignModel;

                switch (action)
                {
                    case "status":
                    {
                        if (!linked)
                        {
                            return McpResponse.Ok(
                                code: "GxServerStatusRetrieved",
                                result: new JObject
                                {
                                    ["connected"] = false,
                                    ["kbAlias"] = kbAlias ?? string.Empty,
                                    ["hint"] = "This KB is not linked to a GeneXus Server instance.",
                                    ["source"] = "sdk:ITeamDevClientService"
                                });
                        }
                        return McpResponse.Ok(
                            code: "GxServerStatusRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["kbAlias"] = kbAlias ?? string.Empty,
                                ["serverUrl"] = SafeStr(() => svc.GetServerUrl(kb)),
                                ["host"] = SafeStr(() => svc.GetGXserverHost(kb)),
                                ["remoteKbName"] = SafeStr(() => svc.GetRemoteKBName(kb)),
                                ["remoteVersionName"] = SafeStr(() => svc.RemoteVersionName(model)),
                                ["source"] = "sdk:ITeamDevClientService"
                            });
                    }

                    case "pending":
                    {
                        if (!linked) return NotLinked();
                        // GetLocalChanges returns every locally-changed object; the Commit dialog
                        // splits them into "Pending Commits" (committable) and "Ignored Objects".
                        // The ignore flag is the object's ModelEntityOutput OutputTypeId=505 in the
                        // design model (see IsCommitIgnored) — read locally, no server round-trip.
                        var objects = new JArray();
                        int ignoredCount = 0;
                        foreach (var h in EnumLocalChanges(svc, model))
                        {
                            bool ignored = IsCommitIgnored(model, h);
                            if (ignored) ignoredCount++;
                            objects.Add(new JObject
                            {
                                ["name"] = SafeStr(() => (string)h.ObjectName) ?? SafeStr(() => h.GetName()),
                                ["operation"] = SafeStr(() => h.Operation.ToString()),
                                ["lastChange"] = SafeStr(() => h.LastChange.ToUniversalTime().ToString("o")),
                                ["user"] = SafeStr(() => (string)h.Username),
                                // true = object is in the IDE's "Ignored Objects" tab; a full commit
                                // skips it. false = staged in "Pending Commits".
                                ["ignoredForCommit"] = ignored
                            });
                        }
                        return McpResponse.Ok(
                            code: "GxServerPendingRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["count"] = objects.Count,
                                ["committableCount"] = objects.Count - ignoredCount,
                                ["ignoredCount"] = ignoredCount,
                                ["objects"] = objects,
                                ["note"] = ignoredCount > 0
                                    ? "Objects with ignoredForCommit=true are in the IDE 'Ignored Objects' tab and a full commit skips them. Use action=ignored to list only those."
                                    : null,
                                ["source"] = "sdk:ITeamDevClientService"
                            });
                    }

                    case "ignored":
                    {
                        if (!linked) return NotLinked();
                        // Commit-ignored: locally-changed objects excluded from commit (IDE
                        // Commit > "Ignored Objects"). Flagged by ModelEntityOutput type 505.
                        var commitIgnored = new JArray();
                        int committable = 0;
                        foreach (var h in EnumLocalChanges(svc, model))
                        {
                            if (IsCommitIgnored(model, h))
                            {
                                commitIgnored.Add(new JObject
                                {
                                    ["name"] = SafeStr(() => (string)h.ObjectName) ?? SafeStr(() => h.GetName()),
                                    ["type"] = LocalChangeType(model, h),
                                    ["operation"] = SafeStr(() => h.Operation.ToString()),
                                    ["lastChange"] = SafeStr(() => h.LastChange.ToUniversalTime().ToString("o")),
                                    ["user"] = SafeStr(() => (string)h.Username)
                                });
                            }
                            else committable++;
                        }
                        // Update-ignored: objects excluded from server UPDATEs (a distinct list —
                        // the IDE's Update > "Ignored Objects").
                        var updateIgnored = new JArray();
                        foreach (var d in EnumIgnoredForUpdate(svc, model))
                        {
                            updateIgnored.Add(new JObject
                            {
                                ["name"] = SafeStr(() => (string)d.Name),
                                ["guid"] = SafeStr(() => d.Guid.ToString()),
                                ["objectType"] = SafeStr(() => d.ObjectType.ToString()),
                                ["versionDate"] = SafeStr(() => ((DateTime)d.VersionDate).ToUniversalTime().ToString("o"))
                            });
                        }
                        return McpResponse.Ok(
                            code: "GxServerIgnoredRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["commitIgnoredCount"] = commitIgnored.Count,
                                ["commitIgnored"] = commitIgnored,
                                ["committableCount"] = committable,
                                ["updateIgnoredCount"] = updateIgnored.Count,
                                ["updateIgnored"] = updateIgnored,
                                ["note"] = "commitIgnored = locally-changed objects excluded from commit (IDE Commit > Ignored Objects). updateIgnored = objects excluded from server updates (IDE Update > Ignored Objects).",
                                ["source"] = "sdk:ITeamDevClientService+ModelEntityOutput"
                            });
                    }

                    case "conflicts":
                    {
                        if (!linked) return NotLinked();
                        var conflicts = new JArray();
                        foreach (var ct in new[] { UpdateConflict.YesMustOverwrite, UpdateConflict.YesWithAutoMerge })
                        {
                            foreach (var e in EnumConflicts(svc, model, ct))
                            {
                                conflicts.Add(new JObject
                                {
                                    ["object"] = ConflictObjectName(model, e),
                                    ["conflictType"] = ct.ToString()
                                });
                            }
                        }
                        return McpResponse.Ok(
                            code: "GxServerConflictsRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["count"] = conflicts.Count,
                                ["conflicts"] = conflicts,
                                ["source"] = "sdk:ITeamDevClientService"
                            });
                    }

                    default: // history — local change log (most-recent first). Remote
                             // revision history requires server credentials, which this
                             // read-only surface does not collect.
                    {
                        if (!linked) return NotLinked();
                        if (limit <= 0) limit = 10;
                        if (limit > 200) limit = 200;
                        var rows = new System.Collections.Generic.List<JObject>();
                        foreach (var h in EnumLocalChanges(svc, model))
                        {
                            rows.Add(new JObject
                            {
                                ["name"] = SafeStr(() => (string)h.ObjectName) ?? SafeStr(() => h.GetName()),
                                ["operation"] = SafeStr(() => h.Operation.ToString()),
                                ["lastChange"] = SafeStr(() => h.LastChange.ToUniversalTime().ToString("o")),
                                ["user"] = SafeStr(() => (string)h.Username)
                            });
                        }
                        rows.Sort((a, b) => string.CompareOrdinal((string)b["lastChange"], (string)a["lastChange"]));
                        var history = new JArray();
                        for (int i = 0; i < rows.Count && i < limit; i++) history.Add(rows[i]);
                        return McpResponse.Ok(
                            code: "GxServerHistoryRetrieved",
                            result: new JObject
                            {
                                ["connected"] = true,
                                ["limit"] = limit,
                                ["history"] = history,
                                ["scope"] = "localChanges",
                                ["note"] = "Local (uncommitted) change log. Remote revision history requires server credentials.",
                                ["source"] = "sdk:ITeamDevClientService"
                            });
                    }
                }
            }
            catch { return null; }
        }

        private static string NotLinked()
        {
            return McpResponse.Ok(
                code: "GxServerStatusRetrieved",
                result: new JObject
                {
                    ["connected"] = false,
                    ["hint"] = "This KB is not linked to a GeneXus Server instance.",
                    ["source"] = "sdk:ITeamDevClientService"
                });
        }

        private static IEnumerable<dynamic> EnumLocalChanges(ITeamDevClientService svc, KBModel model)
        {
            IEnumerable raw = svc.GetLocalChanges(model);
            if (raw == null) yield break;
            foreach (var h in raw) yield return h;
        }

        // The GeneXus output-type id that marks an object as "ignored for commit". When you
        // right-click an object in the Commit dialog and choose "Add to 'Ignored Objects'", the
        // IDE writes a ModelEntityOutput row of this type against the object in the design model
        // (empty data — the row's presence IS the flag). Reverse-engineered against GeneXus
        // 18.0.7 (metadata-DB before/after diff of the toggle; verified the 505 set equals the
        // IDE's Ignored-Objects tab exactly). The high-level API that owns this constant —
        // UI.Framework ITeamDevClientService.GetIgnoredForCommit()/IsIgnoredForCommit() — does
        // not resolve in the headless worker, so we read the underlying output directly.
        private const int CommitIgnoreOutputTypeId = 505;

        // LoadLastEntityOutput is a public method on Artech.Udm.Framework.Model (KBModel's base).
        // Cached MethodInfo; reflection keeps us tolerant of the out-parameter signature.
        private static System.Reflection.MethodInfo _loadLastEntityOutput;
        private static System.Reflection.MethodInfo GetLoadLastEntityOutput()
        {
            if (_loadLastEntityOutput == null)
            {
                _loadLastEntityOutput = typeof(Artech.Udm.Framework.Model).GetMethod(
                    "LoadLastEntityOutput",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(Artech.Udm.Framework.EntityKey), typeof(int), typeof(DateTime).MakeByRefType(), typeof(byte[]).MakeByRefType() },
                    null);
            }
            return _loadLastEntityOutput;
        }

        // True when the object is in the IDE's Commit > "Ignored Objects" tab: it carries a
        // ModelEntityOutput of type 505 in the design model. Best-effort — false on any failure
        // (so an object is never wrongly reported as ignored).
        private static bool IsCommitIgnored(KBModel model, dynamic h)
        {
            try
            {
                var m = GetLoadLastEntityOutput();
                if (m == null) return false;
                object[] args = new object[] { (Artech.Udm.Framework.EntityKey)(object)h.Key, CommitIgnoreOutputTypeId, null, null };
                return (bool)m.Invoke(model, args);
            }
            catch { return false; }
        }

        // Friendly object-type name (Procedure / WebPanel / Transaction / Environment …) for a
        // local change, resolved from the KBObject behind the key. Best-effort; null if unresolved.
        private static string LocalChangeType(KBModel model, dynamic h)
        {
            try
            {
                var o = Artech.Architecture.Common.Objects.KBObject.Get(model, (Artech.Udm.Framework.EntityKey)(object)h.Key);
                if (o != null && o.TypeDescriptor != null) return (string)o.TypeDescriptor.Name;
            }
            catch { }
            return null;
        }

        // The Update-ignore list (distinct from commit-ignore): objects excluded when receiving
        // server updates. This one IS exposed on the Common service.
        private static IEnumerable<dynamic> EnumIgnoredForUpdate(ITeamDevClientService svc, KBModel model)
        {
            IEnumerable raw;
            try { raw = svc.GetIgnoredObjectsForUpdate(model); }
            catch { yield break; }
            if (raw == null) yield break;
            foreach (var d in raw) yield return d;
        }

        // A conflict entity's ToString() is the type FQN (e.g. "...Objects.WebPanel"), not the
        // object name. Resolve the real name via the KBObject behind the entity key so
        // action=conflicts is actionable (the name feeds action=resolve targets=[...]).
        private static string ConflictObjectName(KBModel model, dynamic e)
        {
            try
            {
                var o = Artech.Architecture.Common.Objects.KBObject.Get(model, e.Key);
                if (o != null && !string.IsNullOrEmpty((string)o.Name)) return (string)o.Name;
            }
            catch { }
            try { string n = (string)e.Name; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            try { return (string)e.ToString(); } catch { return "<unknown>"; }
        }

        private static IEnumerable<dynamic> EnumConflicts(ITeamDevClientService svc, KBModel model, UpdateConflict ct)
        {
            IEnumerable raw = svc.GetConflictEntities(model, ct);
            if (raw == null) yield break;
            foreach (var e in raw) yield return e;
        }

        private static string SafeStr(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        // ----- legacy file-heuristic fallback (also exercised by unit tests) -----

        internal class DetectionResult
        {
            public bool Connected;
            public string DetectedPath;
        }

        internal static DetectionResult Detect(string kbPath)
        {
            var r = new DetectionResult();
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath)) return r;

            string p1 = Path.Combine(kbPath, "Repository", "Repository.gxs");
            if (File.Exists(p1)) { r.Connected = true; r.DetectedPath = p1; return r; }

            string p2 = Path.Combine(kbPath, ".gx", "gxserver-state.xml");
            if (File.Exists(p2)) { r.Connected = true; r.DetectedPath = p2; return r; }

            string p3 = Path.Combine(kbPath, ".gxserver", "state.xml");
            if (File.Exists(p3)) { r.Connected = true; r.DetectedPath = p3; return r; }

            return r;
        }

        internal static string StatusEnvelope(string kbPath, string kbAlias)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerStatusRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["kbAlias"] = kbAlias ?? string.Empty,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            return McpResponse.Ok(
                code: "GxServerStatusRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["kbAlias"] = kbAlias ?? string.Empty,
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath,
                    ["detectedVia"] = det.DetectedPath
                });
        }

        internal static string PendingEnvelope(string kbPath)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerPendingRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            return McpResponse.Ok(
                code: "GxServerPendingRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["objects"] = new JArray(),
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
                });
        }

        internal static string IgnoredEnvelope(string kbPath)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerIgnoredRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            return McpResponse.Ok(
                code: "GxServerIgnoredRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["commitIgnored"] = new JArray(),
                    ["updateIgnored"] = new JArray(),
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
                });
        }

        internal static string ConflictsEnvelope(string kbPath)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerConflictsRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            return McpResponse.Ok(
                code: "GxServerConflictsRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["conflicts"] = new JArray(),
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
                });
        }

        internal static string HistoryEnvelope(string kbPath, int limit)
        {
            var det = Detect(kbPath);
            if (!det.Connected)
            {
                return McpResponse.Ok(
                    code: "GxServerHistoryRetrieved",
                    result: new JObject
                    {
                        ["connected"] = false,
                        ["hint"] = "This KB is not connected to a GeneXus Server instance."
                    });
            }
            if (limit <= 0) limit = 10;
            if (limit > 200) limit = 200;
            return McpResponse.Ok(
                code: "GxServerHistoryRetrieved",
                result: new JObject
                {
                    ["connected"] = true,
                    ["history"] = new JArray(),
                    ["limit"] = limit,
                    ["note"] = "metadata parsing pending — connection detected via " + det.DetectedPath
                });
        }
    }
}
