using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.8.0 — for a "dumb LLM" client to recover from an error without
    // prose-parsing, every error envelope from a curated list must carry
    // a non-empty `error.nextSteps[]` with concrete tool/args/why entries.
    //
    // This is a source-level guard. It locates each curated error code in
    // the worker source and verifies the surrounding McpResponse.Err(...)
    // call passes a `nextSteps:` argument constructed via the
    // McpResponse.NextStep helper. A regression looks like passing
    // `nextSteps: null` or omitting the argument; this test will flag it.
    public class NextStepsCurationGuardTests
    {
        // Codes that the user-facing client actually hits and recovers from.
        // Keep this list deliberately short — every entry is a known
        // friction point where the LLM has previously gotten stuck without
        // a concrete next step. Adding to this list is fine; removing
        // requires updating the friction memory + envelope.md.
        private static readonly string[] CuratedErrorCodes = new[]
        {
            "PartNotFound",
            "ObjectNotFound",
            "KbNotOpened",
            "VisualXmlUnavailable",
            "PatternXmlUnavailable",
            "FormTypeTransitionUnsupported",
            "IdeHoldsLock",
            "SearchIndexMissing",
            "SearchIndexEmpty",
            "GhCliNotInstalled",
            "UnknownAction",
            "UnknownMethodOrAction",
        };

        private static string FindWorkerDirectory(params string[] subPaths)
        {
            var dir = new DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string parts = Path.Combine(subPaths);
                string candidate = Path.Combine(dir.FullName, "src", "GxMcp.Worker", parts);
                if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate Worker subpath starting from " + System.AppDomain.CurrentDomain.BaseDirectory);
        }

        private static readonly string WorkerServicesDir = FindWorkerDirectory("Services");

        public static System.Collections.Generic.IEnumerable<object[]> Codes() =>
            CuratedErrorCodes.Select(c => new object[] { c });

        [Theory]
        [MemberData(nameof(Codes))]
        public void CuratedErrorCode_HasNextStepsAtEmissionSite(string code)
        {
            // Locate each `code: "<X>"` occurrence, then determine the bounds
            // of the enclosing `McpResponse.Err(...)` call by balanced-paren
            // walking. A regex `[^)]*` approach breaks inside nested calls
            // (the JArray / NextStep parens trip it). The balanced walker
            // handles arbitrary nesting.
            var codeMarker = new Regex(
                @"\bcode\s*:\s*""" + Regex.Escape(code) + @"""",
                RegexOptions.Compiled);

            var offenders = new System.Collections.Generic.List<string>();
            int totalMatches = 0;

            foreach (var path in Directory.EnumerateFiles(WorkerServicesDir, "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(path);
                foreach (Match m in codeMarker.Matches(src))
                {
                    // Walk back to find `McpResponse.Err(`.
                    int openParenIdx = -1;
                    int probe = m.Index;
                    int parenDepth = 0;
                    while (probe > 0)
                    {
                        char c = src[probe];
                        if (c == ')') parenDepth++;
                        else if (c == '(')
                        {
                            if (parenDepth == 0) { openParenIdx = probe; break; }
                            parenDepth--;
                        }
                        probe--;
                    }
                    if (openParenIdx < 0) continue;

                    // Confirm the call is McpResponse.Err.
                    int searchStart = System.Math.Max(0, openParenIdx - 40);
                    string before = src.Substring(searchStart, openParenIdx - searchStart);
                    if (!before.Contains("McpResponse.Err")) continue;

                    // Walk forward from openParenIdx to find the matching close.
                    int closeIdx = -1;
                    parenDepth = 0;
                    for (int i = openParenIdx; i < src.Length; i++)
                    {
                        char c = src[i];
                        if (c == '(') parenDepth++;
                        else if (c == ')')
                        {
                            parenDepth--;
                            if (parenDepth == 0) { closeIdx = i; break; }
                        }
                    }
                    if (closeIdx < 0) continue;

                    totalMatches++;
                    string call = src.Substring(openParenIdx, closeIdx - openParenIdx + 1);
                    bool hasNextSteps = call.Contains("nextSteps:") || call.Contains("McpResponse.NextStep(");
                    if (hasNextSteps) continue;

                    int windowStart = System.Math.Max(0, openParenIdx - 600);
                    int windowEnd = System.Math.Min(src.Length, closeIdx + 600);
                    string window = src.Substring(windowStart, windowEnd - windowStart);
                    if (window.Contains("no-nextStep:")) continue;

                    offenders.Add(Path.GetFileName(path) + " @ char " + openParenIdx);
                }
            }

            Assert.True(totalMatches > 0,
                "Curated error code '" + code + "' is not emitted by any worker service. " +
                "Either add a service that emits it, or remove it from the curated list.");

            Assert.True(offenders.Count == 0,
                "Curated error code '" + code + "' has emission site(s) missing nextSteps[] curation.\n" +
                "An LLM client relies on nextSteps[] to recover from this code without prose-parsing.\n" +
                "Either add a `nextSteps: new JArray(McpResponse.NextStep(tool, args, why))` argument,\n" +
                "or document the omission with a `// no-nextStep: <one sentence>` comment within the call.\n" +
                "Offenders:\n  " + string.Join("\n  ", offenders));
        }

        // v2.8.0 — transient error codes must always carry `retryAfterMs:` so
        // an LLM client knows how long to wait before retrying. Codes here
        // signal "try again in a bit"; without retryAfterMs the client either
        // hammers in a tight loop (wastes worker thread) or sleeps too long.
        public static readonly string[] TransientErrorCodes = new[]
        {
            "KbNotOpened",
            "OpenInProgress",
            "SearchIndexMissing",
            "SearchIndexEmpty",
            "Reindexing",
            "IndexCold",
            "IndexBuilding",
            "InProgress",
            "ProjectionTimedOut",
            "WorkerBooting",
            "Booting",
        };

        public static System.Collections.Generic.IEnumerable<object[]> TransientCodes() =>
            TransientErrorCodes.Select(c => new object[] { c });

        [Theory]
        [MemberData(nameof(TransientCodes))]
        public void TransientErrorCode_CarriesRetryAfterMs(string code)
        {
            var codeMarker = new Regex(
                @"\bcode\s*:\s*""" + Regex.Escape(code) + @"""",
                RegexOptions.Compiled);

            var offenders = new System.Collections.Generic.List<string>();
            int totalMatches = 0;

            foreach (var path in Directory.EnumerateFiles(WorkerServicesDir, "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(path);
                foreach (Match m in codeMarker.Matches(src))
                {
                    // Walk back to McpResponse.Err( using balanced parens.
                    int openParenIdx = -1;
                    int probe = m.Index;
                    int parenDepth = 0;
                    while (probe > 0)
                    {
                        char c = src[probe];
                        if (c == ')') parenDepth++;
                        else if (c == '(')
                        {
                            if (parenDepth == 0) { openParenIdx = probe; break; }
                            parenDepth--;
                        }
                        probe--;
                    }
                    if (openParenIdx < 0) continue;
                    int searchStart = System.Math.Max(0, openParenIdx - 40);
                    string before = src.Substring(searchStart, openParenIdx - searchStart);
                    if (!before.Contains("McpResponse.Err")) continue;

                    int closeIdx = -1;
                    parenDepth = 0;
                    for (int i = openParenIdx; i < src.Length; i++)
                    {
                        char c = src[i];
                        if (c == '(') parenDepth++;
                        else if (c == ')')
                        {
                            parenDepth--;
                            if (parenDepth == 0) { closeIdx = i; break; }
                        }
                    }
                    if (closeIdx < 0) continue;

                    totalMatches++;
                    string call = src.Substring(openParenIdx, closeIdx - openParenIdx + 1);
                    if (call.Contains("retryAfterMs:")) continue;
                    offenders.Add(Path.GetFileName(path) + " @ char " + openParenIdx);
                }
            }

            if (totalMatches == 0) return; // unused transient code is fine

            Assert.True(offenders.Count == 0,
                "Transient error code '" + code + "' is missing `retryAfterMs:` at one or more emission sites. " +
                "Without retryAfterMs an LLM client either hammers the gateway in a tight loop or sleeps too long. " +
                "Pass `retryAfterMs: <int>` to McpResponse.Err(...) — see NextStepsCurationGuardTests.TransientErrorCodes for the curated list.\n" +
                "Offenders:\n  " + string.Join("\n  ", offenders));
        }

        [Fact]
        public void NextStepHelperContractIsStable()
        {
            // McpResponse.NextStep(tool, args, why) is the only sanctioned
            // way to build a nextStep entry. Tools that hand-roll
            // `new JObject { ["tool"] = ..., ["args"] = ..., ["why"] = ... }`
            // bypass the helper and risk drifting from the canonical
            // {tool, args, why} keys (e.g. typoed "reason" instead of "why").
            string modelsPath = FindWorkerDirectory("Models", "McpResponse.cs");
            string src = File.ReadAllText(modelsPath);

            // The helper signature is part of the public contract.
            Assert.Contains("public static JObject NextStep(string tool, JObject args = null, string why = null)", src);
            // Helper builds the three canonical keys.
            Assert.Contains("[\"tool\"] = tool", src);
            Assert.Contains("[\"args\"] = args", src);
            Assert.Contains("[\"why\"] = why", src);
        }
    }
}
