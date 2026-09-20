using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.8.0 — prevent NEW test code from regressing into legacy envelope
    // assertions. Without this guard, a contributor copying an old test
    // template might write `Assert.Equal("Success", obj["status"])` and
    // it would PASS today (the legacy literal doesn't exist on the wire,
    // so the assertion only fails — but it could be silently fixed by
    // changing the envelope back to legacy). Catch the pattern at the
    // source instead.
    public class TestSourceEnvelopeGuardTests
    {
        private static readonly string WorkerTestsDir = Path.GetFullPath(
            Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker.Tests"));

        // Files that ARE allowed to mention the legacy literals because they
        // are the regression guards themselves OR they exercise translation
        // logic that needs both shapes available in a single test.
        private static readonly string[] Allowlist = new[]
        {
            "EnvelopeContractGuardTests.cs",      // scans for legacy patterns — must contain them as strings
            "EnvelopeConformance.cs",             // the validator — contains the literal "Success" as a rejected token
            "HealthServiceEnvelopeTests.cs",      // exercises rejection of legacy shape via validator
            "TestSourceEnvelopeGuardTests.cs",    // this file — patterns appear in regex strings
            "NextStepsCurationGuardTests.cs",     // patterns may appear in source-scan strings
        };

        [Fact]
        public void NoLegacyStatusAssertionsInTestFiles()
        {
            // Forbid: Assert.Equal("Success", obj["status"]) and friends.
            // The canonical assertion is Assert.Equal("ok", (string)obj["status"]).
            var pattern = new Regex(
                @"Assert\.\w+\s*\(\s*""(Success|Error|DryRun|NoChange|Skipped|Ok|Ready|Running|Cold)""\s*,\s*\(?[^)]*?\[?""status""\]?",
                RegexOptions.Compiled);

            var offenders = Directory.EnumerateFiles(WorkerTestsDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !Allowlist.Any(a => p.EndsWith(a, System.StringComparison.OrdinalIgnoreCase)))
                .Where(p => pattern.IsMatch(File.ReadAllText(p)))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Legacy status assertion resurfaced in a test file. The canonical envelope uses lower-case " +
                "status values (ok/error/partial/accepted) — see docs/envelope.md. Update offenders to assert " +
                "the canonical shape: e.g. Assert.Equal(\"ok\", (string)obj[\"status\"]); Assert.Equal(\"CodeX\", (string)obj[\"code\"]).\n" +
                "Offenders:\n  " +
                string.Join("\n  ", offenders.Select(Path.GetFileName)));
        }

        [Fact]
        public void NoLegacyTopLevelFieldReadsInTestFiles()
        {
            // Forbid pattern: obj["message"] when it's a top-level error message read.
            // We don't want to forbid all obj["message"] reads (some are inside result/error
            // sub-objects), so we look specifically for the LITERAL pattern
            // `obj["status"]` followed within ~200 chars by a top-level read of a
            // field that should now live under result/error. Simpler heuristic:
            // forbid obj["details"] / obj["noChange"] / obj["action"] top-level reads
            // outside the allowlist.
            var legacyFieldPattern = new Regex(
                @"\bobj\[""(noChange|action)""\]",
                RegexOptions.Compiled);

            var offenders = Directory.EnumerateFiles(WorkerTestsDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !Allowlist.Any(a => p.EndsWith(a, System.StringComparison.OrdinalIgnoreCase)))
                .Where(p => legacyFieldPattern.IsMatch(File.ReadAllText(p)))
                .ToList();

            Assert.True(offenders.Count == 0,
                "Test file reads a legacy top-level field. v2.8.0 removed top-level `noChange` and `action`; " +
                "the relevant info now lives under `result` (success) or `error` (failure), with status semantics " +
                "carried by `code` (e.g. code:\"NoChange\"). See docs/envelope.md.\n" +
                "Offenders:\n  " +
                string.Join("\n  ", offenders.Select(Path.GetFileName)));
        }
    }
}
