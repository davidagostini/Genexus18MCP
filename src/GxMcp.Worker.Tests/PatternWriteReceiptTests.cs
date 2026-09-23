using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatternWriteReceiptTests
    {
        private const string Before = "<instance caption='Old'/>";
        private const string Requested = "<instance caption='New' includeInExportImportCsv='true'/>";

        [Theory]
        [InlineData("<instance caption='New'/>", "partial", true, false)]
        [InlineData(Before, "unchanged", false, false)]
        [InlineData(Requested, "requested", true, true)]
        public void CommittedWriteSeparatesPersistenceFromRequestedEquality(string observed, string state, bool persisted, bool verified)
        {
            var result = new JObject();
            PatternWriteReceipt.Apply(result, true, Before, Requested, observed, "snapshot.xml");
            Assert.True((bool)result["saved"]);
            Assert.Equal(state, (string)result["persistenceState"]);
            Assert.Equal(persisted, (bool)result["persisted"]);
            Assert.Equal(verified, (bool)result["verified"]);
            Assert.Equal(state == "partial", (bool)result["partialPersistenceDetected"]);
            Assert.Matches("^sha256:[0-9a-f]{64}$", (string)result["beforeHash"]);
            Assert.Matches("^sha256:[0-9a-f]{64}$", (string)result["persistedHash"]);
            Assert.False((bool)result["rollback"]["attempted"]);
            Assert.Equal("AtomicRollbackUnavailable", (string)result["rollback"]["reason"]);
        }

        [Theory]
        [InlineData(Requested, "ok", "WriteApplied")]
        [InlineData("<instance caption='New'/>", "partial", "PatternVerificationMismatch")]
        public void BestEffortCompletionKeepsObservedSourceAndTokenWithoutClaimingUnverifiedSuccess(string observed, string status, string code)
        {
            var receipt = new JObject();
            PatternWriteReceipt.Apply(receipt, true, Before, Requested, observed, "snapshot.xml", versionToken: "fresh-token");
            var response = JObject.Parse(PatternWriteReceipt.Complete("Panel", receipt));
            PatternWriteReceipt.Promote(response, "Panel", "PatternInstance");
            Assert.Equal(status, (string)response["status"]);
            Assert.Equal(code, (string)response["code"]);
            Assert.Equal(observed, (string)response["source"]);
            Assert.Equal("fresh-token", (string)response["versionToken"]);
            Assert.True((bool)response["postSaveVerification"]["reReadConfirmed"]);
        }

        [Theory]
        [InlineData("<instance caption='New'/>")]
        [InlineData(null)]
        [InlineData(Before)]
        public void CallerRollbackCannotCompensateCommittedPatternSave(string observed)
        {
            var receipt = new JObject { ["status"] = "error", ["code"] = "PatternVerificationMismatch" };
            PatternWriteReceipt.Apply(receipt, true, Before, Requested, observed, "snapshot.xml", versionToken: "fresh-token");
            var writer = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GxMcp.Worker.Services.WriteService));
            var rollback = writer.GetType().GetMethod("RollbackFullWriteFailure",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var response = JObject.Parse((string)rollback.Invoke(writer,
                new object[] { receipt.ToString(), "Panel", "PatternInstance", null, Before }));
            Assert.False((bool)response["rollback"]["attempted"]);
            Assert.Equal("AtomicRollbackUnavailable", (string)response["rollback"]["reason"]);
            Assert.Equal("snapshot.xml", (string)response["snapshot"]);
            Assert.Equal(receipt["persisted"], response["persisted"]);
            Assert.Equal(receipt["source"], response["source"]);
            Assert.Equal(receipt["versionToken"], response["versionToken"]);
        }

        [Fact]
        public void PatternMutationInvalidatesIndexedSourceAndEveryKnownInspectionAlias()
        {
            var id = System.Guid.NewGuid().ToString();
            string name = "PatternCache" + System.Guid.NewGuid().ToString("N");
            var entry = new GxMcp.Worker.Models.SearchIndex.IndexEntry
            {
                Guid = id, Name = name, Module = "Module", Path = "Module/" + name,
                Type = "WebPanel", FullSource = "OldNeedle()", FullSourcePart = "Events"
            };
            var index = new GxMcp.Worker.Services.IndexCacheService();
            index.LoadFromEntries(new[] { entry });
            index.EnsureSourceTokenIndex();
            Assert.True(index.TryGetLoadedIndex().SourceTokenIndex.ContainsKey("oldneedle"));
            var before = System.DateTime.UtcNow.AddSeconds(-1);
            GxMcp.Worker.Services.WriteService.InvalidatePatternMutationCaches(index, id);
            Assert.Null(entry.FullSource);
            Assert.Null(entry.FullSourcePart);
            Assert.False(index.TryGetLoadedIndex().SourceTokenIndex.ContainsKey("oldneedle"));
            Assert.Single(index.TryGetLoadedIndex().Objects);
            foreach (string alias in new[] { id, name, "Module." + name, "Module/" + name })
                Assert.True(GxMcp.Worker.Services.WriteService.WasTargetWrittenSince(alias, before));
        }

        [Fact]
        public void SuccessfulNestedReceiptRetainsCanonicalVersionAndVerification()
        {
            var inner = new JObject();
            PatternWriteReceipt.Apply(inner, true, Before, Requested, Requested, "snapshot.xml", versionToken: "fresh-token");
            var response = new JObject { ["status"] = "ok", ["code"] = "WriteApplied", ["result"] = inner };
            var writer = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GxMcp.Worker.Services.WriteService));
            var wrap = writer.GetType().GetMethod("WrapWithPersistedState",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var result = JObject.Parse((string)wrap.Invoke(writer, new object[]
                { response.ToString(), "Panel", "PatternInstance", null, Before, Requested, null, null, false }));
            Assert.Equal("fresh-token", (string)result["versionToken"]);
            Assert.Equal("fresh-token", (string)result["postSaveVerification"]["versionToken"]);
            Assert.True((bool)result["postSaveVerification"]["reReadConfirmed"]);
            Assert.True((bool)result["verified"]);
            Assert.True((bool)result["saveAttempted"]);
            Assert.Equal(Requested, (string)result["source"]);
            Assert.Equal("PatternInstance", (string)result["part"]);
            Assert.Equal("Panel", (string)result["target"]);
        }

        [Fact]
        public void ChildWithoutSpecificationIsSkippedWithoutDereference()
        {
            var child = (Artech.Packages.Patterns.Objects.PatternInstanceElement)
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Artech.Packages.Patterns.Objects.PatternInstanceElement));
            Assert.False(PatternPropertyPreflight.MatchesChild(child, "attribute", "Name"));
            Assert.False(PatternPropertyPreflight.MatchesChild(null, "attribute", "Name"));
        }

        [Fact]
        public void GenericWriterWrapperPreservesCommittedPartialReceipt()
        {
            var receipt = new JObject { ["code"] = "PatternVerificationMismatch", ["status"] = "error" };
            PatternWriteReceipt.Apply(receipt, true, Before, Requested, "<instance caption='New'/>", "snapshot.xml");
            var writer = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GxMcp.Worker.Services.WriteService));
            var wrap = writer.GetType().GetMethod("WrapWithPersistedState",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            string json = (string)wrap.Invoke(writer, new object[]
                { receipt.ToString(), "Panel", "PatternInstance", null, Before, Requested, null, null, false });
            var result = JObject.Parse(json);
            Assert.True((bool)result["saved"]);
            Assert.True((bool)result["persisted"]);
            Assert.Equal("partial", (string)result["persistenceState"]);
            Assert.False((bool)result["verified"]);
        }

        [Fact]
        public void FailedRereadDoesNotClaimNoMutationOrRollback()
        {
            var result = new JObject();
            PatternWriteReceipt.Apply(result, true, Before, Requested, null, "snapshot.xml");
            Assert.True((bool)result["saved"]);
            Assert.Equal(JTokenType.Null, result["persisted"].Type);
            Assert.False((bool)result["persistedStateKnown"]);
            Assert.False((bool)result["rollback"]["verified"]);
        }

        [Fact]
        public void RollbackWithoutIndependentRereadRemainsUnverified()
        {
            var result = new JObject();
            PatternWriteReceipt.Apply(result, false, Before, Requested, null, "snapshot.xml", true, "rollback failed");
            Assert.Equal(JTokenType.Null, result["saved"].Type);
            Assert.True((bool)result["rollback"]["attempted"]);
            Assert.False((bool)result["rollback"]["verified"]);
            Assert.Equal("rollback failed", (string)result["rollback"]["error"]);
        }

        [Theory]
        [InlineData("includeInExportImportCsv")]
        [InlineData("includeInExportImportExcel")]
        public void NativeSpecificationRejectsDroppedPropertyBeforeSave(string unsupported)
        {
            var specification = new Artech.Packages.Patterns.Specification.SpecificationType
            {
                Name = "instance",
                Attributes = new[] { new Artech.Packages.Patterns.Specification.SpecificationAttribute { Name = "caption", Type = "string" } }
            };
            // Element construction requires a live PatternBasePart; exercise the
            // real SDK specification without inventing a KB or persistence claim.
            Assert.Equal(unsupported, PatternPropertyPreflight.UnsupportedAttribute(
                XElement.Parse(Before), XElement.Parse("<instance caption='New' " + unsupported + "='true'/>"),
                System.Linq.Enumerable.Select(specification.Attributes, a => a.Name)));
        }

        [Theory]
        [InlineData("includeInExportImportCsv")]
        [InlineData("includeInExportImportExcel")]
        public void UnsupportedMixedPropertyBatchIsRejectedByInstalledSpecification(string unsupported)
        {
            var before = XElement.Parse(Before);
            var requested = XElement.Parse("<instance caption='New' " + unsupported + "='true'/>");
            Assert.Equal(unsupported, PatternPropertyPreflight.UnsupportedAttribute(before, requested, new[] { "caption" }));
        }

        [Fact]
        public void InstalledSpecificationAllowsSupportedPropertyAndPreservesUnchangedLegacyProperty()
        {
            var before = XElement.Parse("<instance caption='Old' legacy='true'/>");
            var after = XElement.Parse("<instance caption='New' legacy='true'/>");
            Assert.Null(PatternPropertyPreflight.UnsupportedAttribute(before, after, new[] { "caption" }));
        }
    }
}
