using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The commit of a variable batch, run with a stand-in for the live Variables part. The stand-in refuses
    // names the way VariablesPart.BeforeItemAdd does (it throws), so the tests exercise what happens when the
    // SDK refuses an item that staging could not foresee.
    public class VariableBatchSupportTests
    {
        private sealed class FakePart
        {
            private readonly HashSet<string> refused;
            private readonly HashSet<string> stuck;

            public FakePart(string[] refusedNames = null, string[] stuckNames = null, params string[] initial)
            {
                refused = new HashSet<string>(refusedNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
                stuck = new HashSet<string>(stuckNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
                Items.AddRange(initial);
            }

            public List<string> Items { get; } = new List<string>();
            public List<string> Attempted { get; } = new List<string>();
            public List<string> Removed { get; } = new List<string>();

            public void Add(string name)
            {
                Attempted.Add(name);
                if (refused.Contains(name)) throw new InvalidOperationException("'" + name + "' is a standard variable");
                Items.Add(name);
            }

            public void Remove(string name)
            {
                if (stuck.Contains(name)) throw new InvalidOperationException("'" + name + "' cannot be removed");
                Removed.Add(name);
                Items.Remove(name);
            }

            public bool Commit(IReadOnlyList<string> staged, out int refusedIndex, out Exception refusal)
            {
                return VariableBatchSupport.TryAddAll(staged, Add, Remove, out refusedIndex, out refusal);
            }
        }

        [Fact]
        public void Commit_AddsEveryStagedItemInOrder()
        {
            var part = new FakePart();

            bool committed = part.Commit(new[] { "A", "B", "C" }, out int refusedIndex, out Exception refusal);

            Assert.True(committed);
            Assert.Equal(-1, refusedIndex);
            Assert.Null(refusal);
            Assert.Equal(new[] { "A", "B", "C" }, part.Items);
            Assert.Empty(part.Removed);
        }

        [Fact]
        public void Commit_OfAnEmptyBatchChangesNothing()
        {
            var part = new FakePart(null, null, "Existing");

            Assert.True(part.Commit(new string[0], out _, out _));
            Assert.Equal(new[] { "Existing" }, part.Items);
        }

        [Fact]
        public void Commit_WhenTheSdkRefusesAnItem_TheItemsAlreadyAddedAreRemovedLastFirst()
        {
            var part = new FakePart(new[] { "Msg" });

            bool committed = part.Commit(new[] { "A", "B", "Msg", "D" }, out int refusedIndex, out Exception refusal);

            Assert.False(committed);
            Assert.Equal(2, refusedIndex);
            Assert.Contains("'Msg' is a standard variable", refusal.Message);
            Assert.Equal(new[] { "B", "A" }, part.Removed);
            Assert.Empty(part.Items);
            // The items after the refused one are never attempted.
            Assert.Equal(new[] { "A", "B", "Msg" }, part.Attempted);
        }

        [Fact]
        public void Commit_WhenTheFirstItemIsRefused_NothingIsRemoved()
        {
            var part = new FakePart(new[] { "Msg" });

            bool committed = part.Commit(new[] { "Msg", "A" }, out int refusedIndex, out _);

            Assert.False(committed);
            Assert.Equal(0, refusedIndex);
            Assert.Empty(part.Removed);
            Assert.Empty(part.Items);
            Assert.Equal(new[] { "Msg" }, part.Attempted);
        }

        [Fact]
        public void Commit_LeavesThePartExactlyAsItWasBefore()
        {
            var part = new FakePart(new[] { "Mode" }, null, "Existing1", "Existing2");

            Assert.False(part.Commit(new[] { "A", "B", "Mode" }, out _, out _));

            Assert.Equal(new[] { "Existing1", "Existing2" }, part.Items);
        }

        [Fact]
        public void Commit_AnItemThatCannotBeRemovedAgainIsNotHidden()
        {
            var part = new FakePart(new[] { "Msg" }, new[] { "A" });

            var failure = Assert.Throws<InvalidOperationException>(() =>
                part.Commit(new[] { "A", "B", "Msg" }, out _, out _));

            // Both causes are named, and the item that could be removed was removed all the same.
            Assert.Contains("'Msg' is a standard variable", failure.Message);
            Assert.Contains("'A' cannot be removed", failure.Message);
            Assert.Equal(new[] { "B" }, part.Removed);
            Assert.Equal(new[] { "A" }, part.Items);
        }

        private static JArray Outcomes()
        {
            return new JArray(
                new JObject { ["name"] = "A", ["status"] = "Added" },
                new JObject { ["name"] = "Bravo", ["status"] = "Added", ["dimensions"] = 1 },
                new JObject { ["name"] = "C", ["itemStatus"] = "Exists" },
                new JObject { ["name"] = "D", ["status"] = "Added" });
        }

        [Fact]
        public void MarkRefused_FailsOnlyTheRefusedItemAndKeepsItsPlace()
        {
            var outcomes = Outcomes();

            VariableBatchSupport.MarkRefused(outcomes, "bravo", "no way");

            Assert.Equal(4, outcomes.Count);
            Assert.Equal("Failed", (string)outcomes[1]["status"]);
            Assert.Equal("no way", (string)outcomes[1]["reason"]);
            Assert.Equal("Bravo", (string)outcomes[1]["name"]);
            // The siblings are untouched here: discarding the batch is the caller's step.
            Assert.Equal("Added", (string)outcomes[0]["status"]);
            Assert.Equal("Added", (string)outcomes[3]["status"]);
            Assert.Null(outcomes[2]["status"]);
            Assert.Equal("Exists", (string)outcomes[2]["itemStatus"]);
        }

        [Fact]
        public void MarkRefused_ReportsTheItemEvenWhenNoOutcomeNamesIt()
        {
            var outcomes = Outcomes();

            VariableBatchSupport.MarkRefused(outcomes, "Zulu", "no way");

            Assert.Equal(5, outcomes.Count);
            Assert.Equal("Zulu", (string)outcomes[4]["name"]);
            Assert.Equal("Failed", (string)outcomes[4]["status"]);
            Assert.Equal(0, outcomes.OfType<JObject>().Count(o => (string)o["status"] == "Failed" && (string)o["name"] != "Zulu"));
        }

        // The SDK's own predicate (VariableDefinition.IsStandard): public, static, no Knowledge Base needed.
        [Theory]
        [InlineData("Msg")]
        [InlineData("Err")]
        [InlineData("Op")]
        [InlineData("Mr")]
        [InlineData("Mode")]
        [InlineData("Line")]
        [InlineData("Page")]
        [InlineData("Output")]
        [InlineData("msg")]
        public void IsSdkStandardName_RecognizesTheNamesTheSdkReserves(string name)
        {
            Assert.True(VariableBatchSupport.IsSdkStandardName(name));
        }

        [Theory]
        [InlineData("CustomerId")]
        [InlineData("SampleRecord")]
        [InlineData("Message")]
        [InlineData("")]
        [InlineData(null)]
        public void IsSdkStandardName_LeavesOtherNamesAlone(string name)
        {
            Assert.False(VariableBatchSupport.IsSdkStandardName(name));
        }
    }
}
