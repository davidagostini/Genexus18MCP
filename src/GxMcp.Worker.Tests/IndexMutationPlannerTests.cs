using System;
using System.Collections.Generic;
using GxMcp.Worker.Services.Structure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IndexMutationPlannerTests
    {
        private static readonly string[] TableAttributes =
        {
            "QueueStartedAt", "QueueCreatedAt", "JobId", "ScheduleId"
        };

        [Fact]
        public void Create_PreservesRequestedAttributeOrderAndType()
        {
            IndexCreatePlan plan = IndexMutationPlanner.Create(new JObject
            {
                ["name"] = "UQueuePending",
                ["unique"] = false,
                ["attributes"] = new JArray(TableAttributes)
            }, TableAttributes, Array.Empty<TableIndexState>());

            Assert.Equal("UQueuePending", plan.WouldCreate.Name);
            Assert.Equal("Duplicate", plan.WouldCreate.IndexType);
            Assert.Equal(TableAttributes, plan.Attributes);
            Assert.All(plan.WouldCreate.Members, member => Assert.Equal("Ascending", member.Order));
            Assert.False(plan.WouldCreate.NameGeneratedBySdk);
        }

        [Fact]
        public void Projected_AddsIndexWithoutChangingSnapshot()
        {
            var original = Existing("PK_QUEUE", "Primary", "QueueId");
            IndexCreatePlan plan = IndexMutationPlanner.Create(new JObject
            {
                ["name"] = "UQueuePending",
                ["unique"] = false,
                ["attributes"] = new JArray("QueueStartedAt", "QueueCreatedAt")
            }, TableAttributes, new[] { original });

            List<TableIndexState> projected = plan.Projected();

            Assert.Single(plan.Before);
            Assert.Equal(2, projected.Count);
            Assert.Equal("PK_QUEUE", plan.Before[0].Name);
            Assert.Equal("UQueuePending", projected[1].Name);
        }

        [Theory]
        [InlineData("DuplicateIndexAttribute", "QueueStartedAt", "QueueStartedAt")]
        [InlineData("AttributeNotInTable", "QueueStartedAt", "UnknownAttribute")]
        public void Create_RejectsInvalidMemberSets(string expectedCode, string first, string second)
        {
            IndexPlanException error = Assert.Throws<IndexPlanException>(() =>
                IndexMutationPlanner.Create(new JObject
                {
                    ["name"] = "UQueuePending",
                    ["attributes"] = new JArray(first, second)
                }, TableAttributes, Array.Empty<TableIndexState>()));

            Assert.Equal(expectedCode, error.Code);
        }

        [Fact]
        public void Create_RejectsDuplicateNameBeforeAnyMutation()
        {
            IndexPlanException error = Assert.Throws<IndexPlanException>(() =>
                IndexMutationPlanner.Create(new JObject
                {
                    ["name"] = "UQueuePending",
                    ["attributes"] = new JArray("QueueStartedAt")
                }, TableAttributes, new[] { Existing("uqueuepending", "Duplicate", "JobId") }));

            Assert.Equal("IndexAlreadyExists", error.Code);
        }

        [Fact]
        public void Create_RejectsDuplicateDefinitionEvenWithDifferentName()
        {
            IndexPlanException error = Assert.Throws<IndexPlanException>(() =>
                IndexMutationPlanner.Create(new JObject
                {
                    ["name"] = "UQueuePending2",
                    ["unique"] = false,
                    ["attributes"] = new JArray("QueueStartedAt", "QueueCreatedAt")
                }, TableAttributes, new[]
                {
                    Existing("UQueuePending", "Duplicate", "QueueStartedAt", "QueueCreatedAt")
                }));

            Assert.Equal("DuplicateIndexDefinition", error.Code);
        }

        [Theory]
        [InlineData("unique", "yes", "InvalidIndexType")]
        [InlineData("order", "Random", "InvalidIndexOrder")]
        [InlineData("name", "1 invalid", "InvalidIndexName")]
        public void Create_ValidatesPayloadTypes(string field, string value, string expectedCode)
        {
            var payload = new JObject
            {
                ["name"] = "UQueuePending",
                ["attributes"] = new JArray("QueueStartedAt")
            };
            payload[field] = value;

            IndexPlanException error = Assert.Throws<IndexPlanException>(() =>
                IndexMutationPlanner.Create(payload, TableAttributes, Array.Empty<TableIndexState>()));

            Assert.Equal(expectedCode, error.Code);
        }

        [Fact]
        public void VersionToken_ChangesForConcurrentIndexState()
        {
            var before = new[] { Existing("PK_QUEUE", "Primary", "QueueId") };
            var after = new[]
            {
                Existing("PK_QUEUE", "Primary", "QueueId"),
                Existing("UQueuePending", "Duplicate", "QueueStartedAt")
            };

            string first = IndexMutationPlanner.ComputeVersionToken("trn", "table", before);
            string second = IndexMutationPlanner.ComputeVersionToken("trn", "table", after);

            Assert.NotEqual(first, second);
            Assert.Equal(first, IndexMutationPlanner.ComputeVersionToken("trn", "table", before));
        }

        [Fact]
        public void SameState_DetectsOrderAndAttributeDivergence()
        {
            var expected = Existing("UQueuePending", "Duplicate", "QueueStartedAt", "QueueCreatedAt");
            var reordered = Existing("UQueuePending", "Duplicate", "QueueCreatedAt", "QueueStartedAt");
            var descending = Existing("UQueuePending", "Duplicate", "QueueStartedAt", "QueueCreatedAt");
            descending.Members[0].Order = "Descending";

            Assert.False(IndexMutationPlanner.SameState(new[] { expected }, new[] { reordered }));
            Assert.False(IndexMutationPlanner.SameState(new[] { expected }, new[] { descending }));
            Assert.True(IndexMutationPlanner.SameState(new[] { expected }, new[] { expected.Clone() }));
        }

        [Fact]
        public void SameState_IgnoresCollectionOrderButVersionTokenRemainsStable()
        {
            var primary = Existing("PK_QUEUE", "Primary", "QueueId");
            var pending = Existing("UQueuePending", "Duplicate", "QueueStartedAt");
            var first = new[] { primary, pending };
            var reversed = new[] { pending.Clone(), primary.Clone() };

            Assert.True(IndexMutationPlanner.SameState(first, reversed));
            Assert.Equal(
                IndexMutationPlanner.ComputeVersionToken("trn", "table", first),
                IndexMutationPlanner.ComputeVersionToken("trn", "table", reversed));
        }

        [Fact]
        public void RollbackCandidate_NeverSelectsAnIndexFromTheSnapshot()
        {
            var primary = Existing("PK_QUEUE", "Primary", "QueueId");
            var requested = Existing("UQueuePending", "Duplicate", "QueueStartedAt");
            var unrelated = Existing("UAnotherWriter", "Duplicate", "QueueCreatedAt");

            TableIndexState selected = IndexMutationPlanner.FindRollbackCandidate(
                new[] { primary }, new[] { unrelated, primary.Clone(), requested }, requested, requested.Name);

            Assert.Equal(requested.Name, selected.Name);
            Assert.Null(IndexMutationPlanner.FindRollbackCandidate(
                new[] { primary, requested }, new[] { primary.Clone(), requested.Clone(), unrelated },
                requested, requested.Name));
        }

        private static TableIndexState Existing(string name, string type, params string[] attributes)
        {
            var state = new TableIndexState { Name = name, IndexType = type, Source = "User" };
            foreach (string attribute in attributes)
                state.Members.Add(new IndexMemberState { Name = attribute, Order = "Ascending" });
            return state;
        }
    }
}
