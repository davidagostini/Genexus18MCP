using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.8.0 — these tests are the regression guard for the canonical MCP
    // response envelope (see docs/envelope.md). They scan worker source files
    // and fail when legacy emission shapes reappear: McpResponse.Success(...),
    // McpResponse.Error(...), or top-level `["status"] = "Success" / "Error"`
    // / "DryRun" / "NoChange" / "Skipped"`. The whole contract is captured by
    // source convention because exercising every service end-to-end would
    // require a live KB.
    public class EnvelopeContractGuardTests
    {
        private static readonly string WorkerServicesDir = Path.GetFullPath(
            Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services"));

        private static readonly string WorkerModelsDir = Path.GetFullPath(
            Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Models"));

        [Fact]
        public void NoServiceCallsLegacyMcpResponseSuccess()
        {
            // McpResponse.Success(action, target, data) was the pre-v2.8.0
            // helper. Use McpResponse.Ok(target, code, result) instead. The
            // legacy method was deleted from McpResponse.cs; this guard fires
            // if anyone adds it back or hand-codes the old shape.
            var offenders = Directory.EnumerateFiles(WorkerServicesDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => Regex.IsMatch(File.ReadAllText(p), @"\bMcpResponse\.Success\s*\("))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Legacy McpResponse.Success(...) call resurfaced. Use McpResponse.Ok(target, code, result) — see docs/envelope.md.\nOffenders:\n  " +
                string.Join("\n  ", offenders));
        }

        [Fact]
        public void NoServiceCallsLegacyMcpResponseError()
        {
            // McpResponse.Error(...) (all overloads) was the pre-v2.8.0
            // helper. Use McpResponse.Err(code, message, hint, nextSteps,
            // target, extra) instead.
            var offenders = Directory.EnumerateFiles(WorkerServicesDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => Regex.IsMatch(File.ReadAllText(p), @"\bMcpResponse\.Error\s*\("))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Legacy McpResponse.Error(...) call resurfaced. Use McpResponse.Err(code, message, hint, nextSteps, target, extra) — see docs/envelope.md.\nOffenders:\n  " +
                string.Join("\n  ", offenders));
        }

        [Fact]
        public void NoServiceEmitsLegacyStatusLiteralsAtTopLevel()
        {
            // Hand-coded `["status"] = "Success" / "Error" / "DryRun" /
            // "NoChange" / "Skipped" / "Ok" / "Ready" / "Running" / "Cold"`
            // is the second source of envelope drift. Block them all.
            //
            // Note: these strings can legitimately appear inside sub-payloads
            // (e.g. a build sub-status block). We grep specifically for the
            // pattern `["status"] = "<Pascal>"` which is how envelope
            // construction is written — sub-payload fields use other names
            // ("buildStatus", "indexStatus", "vBlockStatus", etc.) to avoid
            // confusion. If you find a legit cross-cutting use, add it to
            // the allowlist below with a code comment explaining why.
            var legacyStatuses = new[]
            {
                "Success", "Error", "DryRun", "NoChange", "Skipped",
                "Ok", "Ready", "Running", "Cold"
            };
            // Tightened pattern: only matches the envelope-construction form.
            var pattern = new Regex(
                @"\[""status""\]\s*=\s*""(" + string.Join("|", legacyStatuses) + @")""",
                RegexOptions.Compiled);

            // Allowlist by path — files where the literal is legitimately
            // used in a sub-payload, NOT the envelope status.
            var allowlist = new string[]
            {
                // Add justified exceptions here. Empty for now — every
                // current site has been migrated.
            };

            var offenders = Directory.EnumerateFiles(WorkerServicesDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !allowlist.Any(a => p.Replace('\\', '/').EndsWith(a)))
                .Where(p => pattern.IsMatch(File.ReadAllText(p)))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Legacy `[\"status\"] = \"<Pascal>\"` envelope literal resurfaced. Construct via McpResponse.Ok / .Err / .Partial / .Accepted — see docs/envelope.md.\nOffenders:\n  " +
                string.Join("\n  ", offenders));
        }

        [Fact]
        public void NoServiceEmitsHandRolledStatusErrorJsonStrings()
        {
            // The other escape hatch: hand-coded `"{\"status\":\"Error\"...}"`
            // string literals returned directly. These bypass even the
            // envelope helpers. Block them outright.
            var pattern = new Regex(
                @"""\\""status\\"":\s*\\""(Success|Error|DryRun|NoChange|Skipped|Ok|Ready|Running|Cold)\\""",
                RegexOptions.Compiled);

            var offenders = Directory.EnumerateFiles(WorkerServicesDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => pattern.IsMatch(File.ReadAllText(p)))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Hand-rolled status JSON string resurfaced. Use McpResponse.Ok / .Err — see docs/envelope.md.\nOffenders:\n  " +
                string.Join("\n  ", offenders));
        }

        [Fact]
        public void McpResponseClass_HasOnlyCanonicalHelpers()
        {
            // Pin McpResponse.cs to the canonical surface. Adding back
            // `public static string Success(...)` or `Error(...)` flips this
            // test red so any future contributor sees the breaking-change
            // implication.
            string src = File.ReadAllText(Path.Combine(WorkerModelsDir, "McpResponse.cs"));

            // Required canonical helpers exist.
            Assert.Contains("public static string Ok(", src);
            Assert.Contains("public static string Err(", src);
            Assert.Contains("public static string Partial(", src);
            Assert.Contains("public static string Accepted(", src);
            Assert.Contains("public static JObject NextStep(", src);

            // Legacy helpers must NOT come back.
            Assert.DoesNotContain("public static string Success(", src);
            Assert.DoesNotContain("public static string Error(", src);
        }

        [Fact]
        public void EnvelopeDocExists()
        {
            // The contract doc is part of the deliverable. Renaming or
            // deleting it without updating tests/dispatcher comments is
            // a silent regression risk.
            string docPath = Path.GetFullPath(Path.Combine(
                TestFixtures.FindRepoRoot(), "docs", "envelope.md"));
            Assert.True(File.Exists(docPath),
                "docs/envelope.md is the source of truth for the canonical MCP envelope. " +
                "Do not delete it. Expected at: " + docPath);

            string doc = File.ReadAllText(docPath);
            // Sanity: the 4 canonical statuses must be documented.
            Assert.Contains("\"ok\"", doc);
            Assert.Contains("\"error\"", doc);
            Assert.Contains("\"partial\"", doc);
            Assert.Contains("\"accepted\"", doc);
            Assert.Contains("nextSteps", doc);
        }
    }
}
