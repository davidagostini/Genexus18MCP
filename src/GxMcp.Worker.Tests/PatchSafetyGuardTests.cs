using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// v2.6.6 FR#10 — patch safety guard. Reproduces the v2.6.5 friction where a
    /// NoMatch fall-through let WriteService persist an empty payload and the
    /// Events part of an object was lost with sha256 = e3b0c44... (empty).
    /// </summary>
    public class PatchSafetyGuardTests
    {
        [Fact]
        public void ApplyFindReplace_CrlfSource_LfFind_DoesNotPersistEmpty()
        {
            // v2.6.5 reproducer: Events part with CRLF source, find pattern with LF only.
            // ApplyFindReplace ALREADY normalizes via TryMatch so this resolves to a
            // legitimate match, but the regression contract here is: the result MUST
            // either be (true, real-replacement) or (false, original-source). Never
            // (true, empty) and never (false, empty).
            string crlfSource = "Event 'Save'\r\n    Composite\r\n        DoSomething()\r\n    EndComposite\r\nEndEvent\r\n";
            var patch = new JObject
            {
                ["find"] = "DoSomething()",
                ["replace"] = "DoSomethingElse()"
            };
            var (ok, result, _) = PatchService.ApplyFindReplace(crlfSource, patch);
            Assert.True(ok);
            Assert.False(string.IsNullOrEmpty(result));
            Assert.Contains("DoSomethingElse()", result);
        }

        [Fact]
        public void ApplyFindReplace_NoMatch_ReturnsOriginalNotEmpty()
        {
            // The exact failure mode from the friction report: when the find string
            // doesn't appear at all in the source, ApplyFindReplace MUST return the
            // original source (never empty) so a naive caller cannot accidentally
            // persist an empty payload.
            string crlfSource = "Event 'Save'\r\n    Composite\r\n        DoSomething()\r\n    EndComposite\r\nEndEvent\r\n";
            var patch = new JObject
            {
                ["find"] = "ThisStringDoesNotExist",
                ["replace"] = ""
            };
            var (ok, result, reason) = PatchService.ApplyFindReplace(crlfSource, patch);
            Assert.False(ok);
            Assert.Equal(crlfSource, result);
            Assert.Equal("NoMatch", reason);
        }

        [Fact]
        public void IsPatchWriteSafe_EmptyPayload_NonEmptyOriginal_Rejects()
        {
            string original = "lots of content\nspanning many\nlines\n";
            bool ok = WriteService.IsPatchWriteSafe(original, "", anyOpApplied: false, out string reason);
            Assert.False(ok);
            Assert.Equal("patch_no_match", reason);
        }

        [Fact]
        public void IsPatchWriteSafe_EmptyPayload_WithConfirmedReplace_Allows()
        {
            bool ok = WriteService.IsPatchWriteSafe("the complete source", "", anyOpApplied: true, out string reason);
            Assert.True(ok);
            Assert.Null(reason);
        }

        [Fact]
        public void ApplyFindReplace_CompleteSource_WithEmptyReplacement_ReturnsIntentionalEmptyResult()
        {
            string source = "line one\r\nline two\r\n";
            var patch = new JObject
            {
                ["find"] = source,
                ["replace"] = ""
            };

            var (ok, result, reason) = PatchService.ApplyFindReplace(source, patch);

            Assert.True(ok);
            Assert.Equal(string.Empty, result);
            Assert.Null(reason);
        }

        [Fact]
        public void PatchService_TextWritesUseTransactionalPartSaveAndExposeVerificationReceipt_ViaConvention()
        {
            string patchSource = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "GxMcp.Worker", "Services", "PatchService.cs"));
            string receiptSource = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "GxMcp.Worker", "Services", "PatchPersistenceReceipt.cs"));

            Assert.DoesNotContain("preferFastSourceSave: true", patchSource);
            Assert.Contains("preferFastSourceSave: false", patchSource);
            Assert.Contains("writePayload[\"saved\"]", patchSource);
            Assert.Contains("writePayload[\"verified\"]", patchSource);
            Assert.Contains("payload[\"persistedMatchCount\"]", receiptSource);
            Assert.Contains("payload[\"oldContentPresent\"]", receiptSource);
            Assert.Contains("writePayload[\"versionToken\"]", patchSource);
            Assert.Contains("BaseVersionRequired", patchSource);
            Assert.Contains("CommentOnlyWriteNotPersisted", receiptSource);
            Assert.Contains("implicitOperations", patchSource);
            Assert.Contains("StringSplitOptions.None", patchSource);
            Assert.DoesNotContain("context?.Split(new[] { '\\n' }, StringSplitOptions.RemoveEmptyEntries)", patchSource);
        }

        [Fact]
        public void IsPatchWriteSafe_NullPayload_Rejects()
        {
            bool ok = WriteService.IsPatchWriteSafe("nonempty", null, anyOpApplied: false, out string reason);
            Assert.False(ok);
            Assert.Equal("patch_no_match", reason);
        }

        [Fact]
        public void IsPatchWriteSafe_SuspiciousShrink_NoOpApplied_Rejects()
        {
            // Simulates the v2.6.5 NoMatch fall-through: caller passed a tiny string
            // through as "replacement" without any successful op recorded. The guard
            // must catch this before WriteObject persists.
            string original = new string('x', 1000);
            string proposed = "tiny";
            bool ok = WriteService.IsPatchWriteSafe(original, proposed, anyOpApplied: false, out string reason);
            Assert.False(ok);
            Assert.Equal("suspicious_shrink", reason);
        }

        [Fact]
        public void IsPatchWriteSafe_SuspiciousShrink_WithOpApplied_Allows()
        {
            // Legitimate big-delete patch: caller recorded a successful op. Guard
            // must NOT block these — only the no-op fall-through case is unsafe.
            string original = new string('x', 1000);
            string proposed = "tiny";
            bool ok = WriteService.IsPatchWriteSafe(original, proposed, anyOpApplied: true, out string reason);
            Assert.True(ok);
            Assert.Null(reason);
        }

        [Fact]
        public void IsPatchWriteSafe_NormalEdit_Allows()
        {
            string original = "abc\ndef\nghi\n";
            string proposed = "abc\nDEF!\nghi\n";
            bool ok = WriteService.IsPatchWriteSafe(original, proposed, anyOpApplied: true, out string reason);
            Assert.True(ok);
            Assert.Null(reason);
        }

        [Fact]
        public void IsPatchWriteSafe_EmptyOriginal_AllowsEmptyProposal()
        {
            // Creating a new part with empty initial state is legal.
            bool ok = WriteService.IsPatchWriteSafe("", "", anyOpApplied: false, out string reason);
            Assert.True(ok);
            Assert.Null(reason);
        }
    }
}
