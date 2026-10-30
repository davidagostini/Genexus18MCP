using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A batch property write that cannot be confirmed is rolled back in two steps: each requested property is written back
    /// to its previous value inside the rollback transaction, and counts as restored only when reading it again gives that
    /// value. The source text of a rolled-back part is written back explicitly. These tests pin those decisions without a
    /// Knowledge Base: what goes back, what is left alone and why, and what the report is allowed to say.
    /// </summary>
    public class PropertyRollbackRestoreTests
    {
        [Fact]
        public void WriteBackPutsEveryPropertyWithAPreviousValueBackInRequestOrder()
        {
            var writes = new List<string>();
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Description"] = "Before",
                ["SOAP"] = "True"
            };

            var written = PropertyRollbackRestore.WriteBack(
                new[] { "Description", "SOAP" }, previous, (name, value) => writes.Add(name + "=" + value));

            Assert.Equal(new[] { "Description=Before", "SOAP=True" }, writes);
            Assert.Equal(new[] { "Description", "SOAP" }, written);
        }

        [Fact]
        public void WriteBackNeverWritesAValueThatWasNotRead()
        {
            var writes = new List<string>();
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Description"] = null,
                ["SOAP"] = "True"
            };

            var written = PropertyRollbackRestore.WriteBack(
                new[] { "Description", "SOAP", "Visible" }, previous, (name, value) => writes.Add(name + "=" + value));

            // An unread value is never invented, not even as an empty string. A name with no entry at all is the same.
            Assert.Equal(new[] { "SOAP=True" }, writes);
            Assert.Equal(new[] { "SOAP" }, written);
        }

        [Fact]
        public void AWriteThatThrowsIsLeftOutAndTheOthersStillGoBack()
        {
            var writes = new List<string>();
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CommitOnExit"] = "Yes",
                ["SOAP"] = "True",
                ["REST"] = "False"
            };

            var written = PropertyRollbackRestore.WriteBack(
                new[] { "CommitOnExit", "SOAP", "REST" }, previous, (name, value) =>
                {
                    if (name == "SOAP") throw new InvalidOperationException("the SDK refused the value");
                    writes.Add(name + "=" + value);
                });

            Assert.Equal(new[] { "CommitOnExit=Yes", "REST=False" }, writes);
            Assert.Equal(new[] { "CommitOnExit", "REST" }, written);
        }

        [Fact]
        public void AWrittenPropertyThatReadsBackItsPreviousValueIsRestored()
        {
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Description"] = "Before",
                ["SOAP"] = "True"
            };

            var report = PropertyRollbackRestore.Confirm(
                new[] { "Description", "SOAP" }, previous, new[] { "Description", "SOAP" },
                name => previous[name]);

            Assert.Equal(new[] { "Description", "SOAP" }, report.Restored);
            Assert.Empty(report.NotRestored);
        }

        [Fact]
        public void AWrittenPropertyWhoseReadBackDiffersIsNotRestoredEvenThoughTheWriteWorked()
        {
            // The write returned without an error, but reading the object again gives another value: it is not restored.
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Description"] = "Before",
                ["SOAP"] = "True"
            };

            var report = PropertyRollbackRestore.Confirm(
                new[] { "Description", "SOAP" }, previous, new[] { "Description", "SOAP" },
                name => name == "SOAP" ? "False" : previous[name]);

            Assert.Equal(new[] { "Description" }, report.Restored);
            Assert.Equal(new[] { "SOAP" }, report.NotRestored);
        }

        [Fact]
        public void AWrittenPropertyThatCannotBeReadBackIsNotRestored()
        {
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Description"] = "Before" };

            var report = PropertyRollbackRestore.Confirm(new[] { "Description" }, previous, new[] { "Description" }, name => null);

            Assert.Empty(report.Restored);
            Assert.Equal(new[] { "Description" }, report.NotRestored);
        }

        [Fact]
        public void APropertyThatWasNotWrittenIsNotRestoredAndIsNotReadBack()
        {
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Description"] = "Before",
                ["SOAP"] = null
            };
            var reads = new List<string>();

            var report = PropertyRollbackRestore.Confirm(
                new[] { "Description", "SOAP" }, previous, new[] { "Description" },
                name => { reads.Add(name); return previous[name]; });

            Assert.Equal(new[] { "Description" }, report.Restored);
            Assert.Equal(new[] { "SOAP" }, report.NotRestored);
            Assert.Equal(new[] { "Description" }, reads);
        }

        [Fact]
        public void TheReportsFollowTheRequestOrderAndCarryNamesOnly()
        {
            const string sentinelValue = "sentinel-value-not-in-reports";
            // The dictionary holds the names in the reverse of the request order.
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SOAP"] = "True",
                ["Description"] = sentinelValue
            };

            var report = PropertyRollbackRestore.Confirm(
                new[] { "Description", "SOAP" }, previous, new[] { "Description", "SOAP" },
                name => name == "Description" ? "something else" : previous[name]);

            Assert.Equal(new[] { "SOAP" }, report.Restored);
            Assert.Equal(new[] { "Description" }, report.NotRestored);
            Assert.DoesNotContain(sentinelValue, report.Restored.Concat(report.NotRestored));
        }

        [Fact]
        public void AnEmptyBatchWritesAndRestoresNothing()
        {
            var writes = new List<string>();

            var written = PropertyRollbackRestore.WriteBack(new string[0], new Dictionary<string, string>(), (name, value) => writes.Add(name));
            var report = PropertyRollbackRestore.Confirm(new string[0], new Dictionary<string, string>(), written, name => name);

            Assert.Empty(writes);
            Assert.Empty(written);
            Assert.Empty(report.Restored);
            Assert.Empty(report.NotRestored);
        }

        [Fact]
        public void EverySourceTextIsSetAndAFailingPartDoesNotStopTheOthers()
        {
            var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sourceTexts = new[]
            {
                new KeyValuePair<string, string>("source-a", "// Procedure: A\r\n"),
                new KeyValuePair<string, string>("source-b", "// Procedure: B\r\n"),
                new KeyValuePair<string, string>("source-c", "// Procedure: C\r\n")
            };

            var failed = PropertyRollbackRestore.RestoreSourceTexts(sourceTexts, (key, text) =>
            {
                if (key == "source-b") throw new InvalidOperationException("the part refused the text");
                set[key] = text;
            });

            Assert.Equal(new[] { "source-b" }, failed);
            Assert.Equal("// Procedure: A\r\n", set["source-a"]);
            Assert.Equal("// Procedure: C\r\n", set["source-c"]);
            Assert.False(set.ContainsKey("source-b"));
        }

        [Fact]
        public void ANoSourcePartsMeansNothingIsWritten()
        {
            var calls = 0;

            var failed = PropertyRollbackRestore.RestoreSourceTexts(new KeyValuePair<string, string>[0], (key, text) => calls++);

            Assert.Equal(0, calls);
            Assert.Empty(failed);
        }

        [Fact]
        public void NoSourceFailureAddsNothingToTheMessage()
        {
            Assert.Equal(string.Empty, PropertyRollbackRestore.DescribeSourceFailures(new string[0]));
        }

        [Fact]
        public void SourceFailuresAreNamedInTheMessage()
        {
            var sentence = PropertyRollbackRestore.DescribeSourceFailures(new[] { "Events", "Rules" });

            Assert.Contains("Events, Rules", sentence);
            Assert.Contains("could not be written back", sentence);
        }

        [Fact]
        public void TheChangedPartsAreNamedInTheReason_AndNothingIsAddedWhenNoneIsKnown()
        {
            Assert.Equal(" (Rules, Events)", PropertyRollbackRestore.DescribeChangedParts(new[] { "Rules", "Events" }));
            Assert.Equal(string.Empty, PropertyRollbackRestore.DescribeChangedParts(new string[0]));
            Assert.Equal(string.Empty, PropertyRollbackRestore.DescribeChangedParts(null));
            Assert.Equal(string.Empty, PropertyRollbackRestore.DescribeChangedParts(new[] { " ", null }));
        }

        [Fact]
        public void ARollbackThatRestoredNothingListsEveryRequestedPropertyAsNotRestored()
        {
            var receipt = new JObject();

            PropertyRollbackRestore.NothingRestored(new[] { "Description", "SOAP" }).AddTo(receipt);

            Assert.Empty((JArray)receipt["propertiesRestored"]);
            Assert.Equal(new[] { "Description", "SOAP" }, receipt["propertiesNotRestored"].ToObject<string[]>());
        }

        [Fact]
        public void TheReportCarriesOnlyNamesIntoTheReceipt()
        {
            var report = PropertyRollbackRestore.Confirm(
                new[] { "Description", "SOAP" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Description"] = "Before", ["SOAP"] = "True" },
                new[] { "Description" }, name => "Before");
            var receipt = new JObject();

            report.AddTo(receipt);

            Assert.Equal(new[] { "Description" }, receipt["propertiesRestored"].ToObject<string[]>());
            Assert.Equal(new[] { "SOAP" }, receipt["propertiesNotRestored"].ToObject<string[]>());
            Assert.DoesNotContain("Before", receipt.ToString());
        }

        [Fact]
        public void TheBatchWriteNeverOverwritesThePreviousValuesTheRollbackRestores()
        {
            // An earlier property of a batch can change a later one (an attribute's type rewrites its length): what the
            // rollback restores is the value read before the first write, so the write transaction must not touch it.
            string body = SourceAssert.MethodBody(
                RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs")),
                "public string SetProperties(string target, JObject properties,");
            int transaction = body.IndexOf("BeginTransaction()", StringComparison.Ordinal);

            Assert.True(transaction > 0, "the batch write transaction was not found");
            Assert.Contains("previousValues[propName] =", body.Substring(0, transaction));
            Assert.DoesNotContain("previousValues[", body.Substring(transaction));
        }

        [Fact]
        public void EveryRollbackThatRestoresNothingReportsThatInsteadOfOmittingTheLists()
        {
            string src = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"));
            string rollback = SourceAssert.MethodBody(src, "private string RollbackPropertySnapshot(");

            // The refused rollback and the one whose restoring transaction never committed both say nothing was restored.
            Assert.Equal(2, SourceAssert.Count(rollback, "PropertyRollbackRestore.NothingRestored(rollback.RequestedNames)"));
            Assert.Contains("if (!restoreCommitted) PropertyRollbackRestore.NothingRestored(", rollback);
            Assert.Contains("restoreCommitted = true;", rollback);
        }

        [Fact]
        public void AReadBackThatThrowsLeavesThatPropertyNotRestoredAndTheOthersAreStillRead()
        {
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Description"] = "Before",
                ["SOAP"] = "True"
            };
            var reads = new List<string>();

            var report = PropertyRollbackRestore.Confirm(
                new[] { "Description", "SOAP" }, previous, new[] { "Description", "SOAP" },
                name =>
                {
                    reads.Add(name);
                    if (name == "Description") throw new InvalidOperationException("the read refused");
                    return previous[name];
                });

            Assert.Equal(new[] { "SOAP" }, report.Restored);
            Assert.Equal(new[] { "Description" }, report.NotRestored);
            Assert.Equal(new[] { "Description", "SOAP" }, reads);
        }
    }
}
