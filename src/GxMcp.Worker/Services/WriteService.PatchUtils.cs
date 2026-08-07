using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    // Patch-matching + persisted-state utilities extracted from WriteService.cs (plan 007).
    // Pure move, no logic changes — see plans/007-decompose-writeservice.md.
    public partial class WriteService
    {
        // ----------------------------------------------------------------------
        // v2.3.8 Task 3.1 — EOL-normalized matching helpers (friction-report #4)
        // ----------------------------------------------------------------------
        // Source bytes are preserved on disk; only the comparison is normalized.
        // CRLF/LF are unified and per-line trailing whitespace is trimmed before
        // matching. TryMatch returns indices into the ORIGINAL (non-normalized)
        // source so callers can splice in replacements without corrupting EOLs.

        internal static string NormalizeForCompare(string s)
        {
            if (s == null) return null;
            var lines = s.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
            return string.Join("\n", lines);
        }

        internal static bool TryMatch(string source, string context, out int startIdx, out int endIdx)
        {
            startIdx = endIdx = -1;
            if (source == null || context == null) return false;
            var normSource = NormalizeForCompare(source);
            var normCtx = NormalizeForCompare(context);
            if (normCtx.Length == 0) return false;
            int normIdx = normSource.IndexOf(normCtx, StringComparison.Ordinal);
            if (normIdx < 0) return false;

            int targetLineStart = CountLinesBefore(normSource, normIdx);
            // Walk to the start of the target line in the original source.
            int origPos = 0;
            for (int line = 0; line < targetLineStart && origPos < source.Length; line++)
            {
                int nl = source.IndexOfAny(new[] { '\r', '\n' }, origPos);
                if (nl < 0) { origPos = source.Length; break; }
                origPos = nl + ((source[nl] == '\r' && nl + 1 < source.Length && source[nl + 1] == '\n') ? 2 : 1);
            }

            // Compute column within the normalized line where match starts.
            int prevNL = normSource.LastIndexOf('\n', Math.Max(0, normIdx - 1));
            int normLineStart = prevNL < 0 ? 0 : prevNL + 1;
            int colOffset = normIdx - normLineStart;
            startIdx = Math.Min(source.Length, origPos + colOffset);

            // Walk forward over (ctxLineCount) lines to find the end position in the original source.
            int ctxLineCount = CountLinesBefore(normCtx, normCtx.Length);
            int walker = startIdx;
            for (int i = 0; i < ctxLineCount && walker < source.Length; i++)
            {
                int nl = source.IndexOfAny(new[] { '\r', '\n' }, walker);
                if (nl < 0) { walker = source.Length; break; }
                walker = nl + ((source[nl] == '\r' && nl + 1 < source.Length && source[nl + 1] == '\n') ? 2 : 1);
            }

            // Add the residual column length on the last context line.
            int lastNL = normCtx.LastIndexOf('\n');
            int lastLineLen = lastNL < 0 ? normCtx.Length : (normCtx.Length - lastNL - 1);
            endIdx = Math.Min(source.Length, walker + lastLineLen);
            if (endIdx < startIdx) endIdx = startIdx;
            return true;
        }

        private static int CountLinesBefore(string s, int idx)
        {
            int c = 0;
            int limit = Math.Min(idx, s.Length);
            for (int i = 0; i < limit; i++) if (s[i] == '\n') c++;
            return c;
        }

        // ----------------------------------------------------------------------
        // v2.3.8 Task 3.4 — persistedHash + persistedSnippet on every response
        // ----------------------------------------------------------------------
        // Every write/edit response is wrapped with the SHA256 of the final
        // on-disk source plus a ~10-line snippet, so callers can confirm
        // post-write state without a follow-up read. Applies uniformly to
        // success, no-change, dry-run, rollback, and error responses.

        // ----------------------------------------------------------------------
        // v2.6.6 FR#10 — patch safety guard.
        // ----------------------------------------------------------------------
        /// <summary>
        /// Reject suspicious writes that would silently nuke an object part. A
        /// patch find-string mismatch (CRLF/LF, encoding drift) used to surface
        /// as an empty result string; the unguarded SDK save then persisted the
        /// empty payload and the sha256 of the lost part was e3b0c44... (empty).
        ///
        /// Returns <c>true</c> when the proposed write looks safe. When it
        /// returns <c>false</c>, <paramref name="reason"/> carries a stable
        /// machine-readable code (<c>patch_no_match</c> / <c>suspicious_shrink</c>)
        /// the gateway promotes to an <c>isError</c> envelope.
        /// </summary>
        public static bool IsPatchWriteSafe(string originalContent, string proposedContent, bool anyOpApplied, out string reason)
        {
            reason = null;
            if (proposedContent == null)
            {
                reason = "patch_no_match";
                return false;
            }

            int origLen = originalContent?.Length ?? 0;
            int newLen = proposedContent.Length;

            // Empty proposal with non-empty original is always a patch failure;
            // never let an empty payload reach the SDK save path.
            if (origLen > 0 && newLen == 0)
            {
                reason = "patch_no_match";
                return false;
            }

            // Severe shrink with no recorded op == NoMatch fall-through. The
            // 0.5 ratio matches the brief; tune via tests rather than ad-hoc.
            if (!anyOpApplied && origLen > 0 && newLen < origLen / 2)
            {
                reason = "suspicious_shrink";
                return false;
            }

            return true;
        }

        internal static string ComputeSha256(string content)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content ?? ""));
                return "sha256:" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static string ExtractSnippet(string source, int lineHint, int contextLines = 10)
        {
            if (string.IsNullOrEmpty(source)) return "";
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var start = Math.Max(0, lineHint - contextLines);
            var end = Math.Min(lines.Length, lineHint + contextLines + 1);
            if (end <= start) return "";
            return string.Join("\n", lines.Skip(start).Take(end - start));
        }

        // First line index (0-based) that differs between two texts, or 0 when identical /
        // one is empty. Used to center the persisted snippet on the changed region.
        internal static int FirstDiffLine(string before, string after)
        {
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after)) return 0;
            var a = before.Replace("\r\n", "\n").Split('\n');
            var b = after.Replace("\r\n", "\n").Split('\n');
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return i;
            return a.Length == b.Length ? 0 : n;
        }

        internal static JObject AppendPersistedState(JObject response, string finalSource, int? editLine)
        {
            if (response == null) response = new JObject();
            response["persistedHash"] = ComputeSha256(finalSource ?? "");
            response["persistedSnippet"] = ExtractSnippet(finalSource ?? "", editLine ?? 0, 10);
            return response;
        }

        /// <summary>
        /// Wraps a write-response JSON string with persistedHash + persistedSnippet derived
        /// from the on-disk source after the write attempt (success, partial, or rollback).
        /// Failures to re-read are swallowed — the original envelope is still augmented with
        /// an empty hash/snippet so downstream parsers always find the keys.
        /// </summary>
        private string WrapWithPersistedState(string responseJson, string target, string partName, string sdkPath = null, string priorSource = null, string requestedContent = null)
        {
            JObject parsed = null;
            try { parsed = JObject.Parse(responseJson); }
            catch
            {
                parsed = new JObject { ["raw"] = responseJson ?? "" };
            }

            GxMcp.Worker.Helpers.WriteResultMeta.TagSdkPath(parsed, sdkPath);

            // Skip if the response is already decorated (e.g. nested call).
            if (parsed["persistedHash"] != null && parsed["persistedSnippet"] != null)
                return parsed.ToString();

            string finalSource = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(target) && _objectService != null)
                {
                    string readJson = _objectService.ReadObjectSource(target, partName, null, null, "mcp", true, null);
                    if (!string.IsNullOrWhiteSpace(readJson))
                    {
                        var readObj = JObject.Parse(readJson);
                        finalSource = readObj["source"]?.ToString()
                            ?? readObj["content"]?.ToString()
                            ?? readObj["parts"]?[partName ?? "Source"]?.ToString()
                            ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[PERSISTED-STATE] Re-read failed for " + target + " (" + partName + "): " + ex.Message);
            }

            // issue #31.3: center the snippet on the first changed line when we know the
            // prior source, so the edited region is shown even past the first ~10 lines.
            int? editLine = priorSource != null ? (int?)FirstDiffLine(priorSource, finalSource) : null;
            AppendPersistedState(parsed, finalSource, editLine);

            // #59: every textual mutation exposes the requested/persisted comparison,
            // including SDK normalization, and a successful full write may not claim
            // success unless the re-read state satisfies the request.
            if (requestedContent != null)
            {
                bool requestedMatches = WhitespaceInsensitiveEquals(finalSource, requestedContent);
                parsed["mutation"] = new JObject
                {
                    ["before"] = DescribeContent(priorSource),
                    ["requested"] = DescribeContent(requestedContent),
                    ["persisted"] = DescribeContent(finalSource),
                    ["diff"] = new JObject
                    {
                        ["matches"] = requestedMatches,
                        ["firstDifferentLine"] = FirstDiffLine(requestedContent, finalSource)
                    },
                    ["saved"] = requestedMatches
                };

                string responseStatus = parsed["status"]?.ToString();
                bool successful = string.Equals(responseStatus, "ok", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(responseStatus, "success", StringComparison.OrdinalIgnoreCase);
                string responseCode = parsed["code"]?.ToString();
                bool applied = string.Equals(responseCode, "WriteApplied", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(responseCode, "WriteNoChange", StringComparison.OrdinalIgnoreCase);
                if (successful && applied && !requestedMatches)
                {
                    JObject mutation = (JObject)parsed["mutation"].DeepClone();
                    return Models.McpResponse.Err(
                        code: "WriteNotPersisted",
                        message: "The SDK save completed, but the persisted part does not match the requested content.",
                        hint: "Inspect mutation.diff and retry from the persisted state; SDK normalization is shown explicitly.",
                        target: target,
                        extra: new JObject
                        {
                            ["part"] = partName,
                            ["mutation"] = mutation,
                            ["persistedHash"] = parsed["persistedHash"]?.DeepClone(),
                            ["persistedSnippet"] = parsed["persistedSnippet"]?.DeepClone()
                        });
                }
            }

            // issue #31.2: when the write left the persisted content byte-identical to the
            // prior content, this was a no-op — surface WriteNoChange instead of WriteApplied
            // so callers don't have to diff the hash themselves.
            if (priorSource != null)
            {
                bool changed = !string.Equals(ComputeSha256(priorSource), parsed["persistedHash"]?.ToString(), StringComparison.OrdinalIgnoreCase);
                parsed["changed"] = changed;
                string code = parsed["code"]?.ToString();
                if (!changed && string.Equals(code, "WriteApplied", StringComparison.OrdinalIgnoreCase))
                {
                    parsed["code"] = "WriteNoChange";

                    // issue #36.6 — `changed:false` (persisted == prior) was ambiguous: callers
                    // could not tell "the requested content was already present" (idempotent,
                    // safe) from "the write was dropped" (bug). When we know what was requested,
                    // compare it (whitespace-insensitive) against the persisted state and, ONLY
                    // when they match, assert requestedApplied:true — a positive idempotent
                    // signal. We never assert a "drop" here (normalization differences could
                    // false-alarm); absence of the flag means "verify via persistedSnippet".
                    bool? requestedApplied = null;
                    if (requestedContent != null)
                        requestedApplied = WhitespaceInsensitiveEquals(finalSource, requestedContent);

                    if (requestedApplied == true)
                    {
                        parsed["requestedApplied"] = true;
                        parsed["noChangeReason"] = "The requested content is already present — this was an idempotent no-op (persisted state matches your request). Nothing needed to change.";
                    }
                    else
                    {
                        parsed["noChangeReason"] = "Persisted content is byte-identical to what was there before this call. If you expected a change, compare your requested content against persistedSnippet — the edit may have been a no-op or dropped.";
                    }
                }
            }

            return parsed.ToString(Newtonsoft.Json.Formatting.None);
        }

        // issue #36.6 — compare two content blobs ignoring all whitespace differences, so a
        // pure re-formatting by the serializer isn't mistaken for a content divergence when we
        // decide whether the requested content is already present.
        // issue #36.6 / issues #70 & #71 — compare two content blobs ignoring whitespace & casing differences
        // outside string literals, so pure re-formatting or SDK casing/XML normalization by the serializer
        // isn't mistaken for a content divergence.
        private static bool WhitespaceInsensitiveEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;

            if (IsXmlString(a) && IsXmlString(b))
            {
                return IsXmlEquivalent(a, b);
            }

            return NormalizedCodeEquals(a, b);
        }

        private static bool NormalizedCodeEquals(string a, string b)
        {
            var linesA = a.Replace("\r\n", "\n").Split('\n');
            var linesB = b.Replace("\r\n", "\n").Split('\n');

            if (linesA.Length != linesB.Length) return false;

            for (int i = 0; i < linesA.Length; i++)
            {
                string trimA = linesA[i].Trim();
                string trimB = linesB[i].Trim();

                if (!string.Equals(trimA, trimB, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static bool IsXmlString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string trimmed = s.TrimStart();
            return trimmed.StartsWith("<");
        }

        private static bool IsXmlEquivalent(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                var docA = System.Xml.Linq.XDocument.Parse(a);
                var docB = System.Xml.Linq.XDocument.Parse(b);
                return AreXmlElementsEqual(docA.Root, docB.Root, depth: 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool AreXmlElementsEqual(System.Xml.Linq.XElement e1, System.Xml.Linq.XElement e2, int depth)
        {
            if (e1 == null && e2 == null) return true;
            if (e1 == null || e2 == null) return false;
            if (depth > 64) return false; // Prevent stack overflow on deeply nested XML

            if (e1.Name != e2.Name)
                return false;

            var attrs1 = GetSignificantAttributes(e1);
            var attrs2 = GetSignificantAttributes(e2);

            foreach (var kvp in attrs1)
            {
                if (attrs2.TryGetValue(kvp.Key, out string val2))
                {
                    if (!string.Equals(kvp.Value, val2, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else
                {
                    if (!IsDefaultOrEmptyAttribute(kvp.Key, kvp.Value))
                        return false;
                }
            }

            foreach (var kvp in attrs2)
            {
                if (!attrs1.ContainsKey(kvp.Key))
                {
                    if (!IsDefaultOrEmptyAttribute(kvp.Key, kvp.Value))
                        return false;
                }
            }

            var children1 = e1.Elements().Where(c => !IsEmptyContainer(c)).ToList();
            var children2 = e2.Elements().Where(c => !IsEmptyContainer(c)).ToList();

            if (children1.Count != children2.Count)
                return false;

            for (int i = 0; i < children1.Count; i++)
            {
                if (!AreXmlElementsEqual(children1[i], children2[i], depth + 1))
                    return false;
            }

            if (children1.Count == 0 && children2.Count == 0)
            {
                string t1 = e1.Value?.Trim() ?? "";
                string t2 = e2.Value?.Trim() ?? "";
                if (!string.Equals(t1, t2, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static System.Collections.Generic.Dictionary<string, string> GetSignificantAttributes(System.Xml.Linq.XElement e)
        {
            var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in e.Attributes())
            {
                dict[attr.Name.LocalName] = attr.Value;
            }
            return dict;
        }

        private static bool IsDefaultOrEmptyAttribute(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            if (name.StartsWith("default", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "100", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool IsEmptyContainer(System.Xml.Linq.XElement e)
        {
            return !e.HasElements && string.IsNullOrWhiteSpace(e.Value) && !e.HasAttributes;
        }


        private static JObject DescribeContent(string content)
        {
            if (content == null) return null;
            const int cap = 1200;
            string snippet = content.Length > cap ? content.Substring(0, cap) + "…[truncated]" : content;
            return new JObject
            {
                ["hash"] = ComputeSha256(content),
                ["length"] = content.Length,
                ["snippet"] = snippet
            };
        }
    }
}
