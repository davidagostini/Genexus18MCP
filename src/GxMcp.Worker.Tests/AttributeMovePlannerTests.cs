using System;
using GxMcp.Worker.Services.Structure;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AttributeMovePlannerTests
    {
        private static readonly string[] Before =
        {
            "SampleId",
            "SampleSubtypeId",
            "SampleName",
            "SampleLocationId",
            "SampleOperationType",
            "SampleRouteId",
            "SampleRouteDescription"
        };

        [Fact]
        public void After_MovesOnlyTargetAndKeepsRelativeOrder()
        {
            var plan = AttributeMovePlanner.Create(Before, "SampleSubtypeId",
                before: null, after: "SampleRouteId", position: null);

            Assert.Equal(1, plan.OldPosition);
            Assert.Equal(5, plan.NewPosition);
            Assert.Equal(new[]
            {
                "SampleId", "SampleName", "SampleLocationId",
                "SampleOperationType", "SampleRouteId",
                "SampleSubtypeId", "SampleRouteDescription"
            }, plan.OrderedNames);
        }

        [Fact]
        public void Before_AndPosition_AreZeroBased()
        {
            var before = AttributeMovePlanner.Create(Before, "SampleSubtypeId",
                before: "SampleRouteDescription", after: null, position: null);
            var position = AttributeMovePlanner.Create(Before, "SampleSubtypeId",
                before: null, after: null, position: 5);

            Assert.Equal(before.OrderedNames, position.OrderedNames);
        }

        [Fact]
        public void RequiresExactlyOneSelector()
        {
            Assert.Throws<ArgumentException>(() => AttributeMovePlanner.Create(
                Before, "SampleId", null, null, null));
            Assert.Throws<ArgumentException>(() => AttributeMovePlanner.Create(
                Before, "SampleId", "SampleName", null, 0));
        }

        [Fact]
        public void RejectsMissingReferenceAndOutOfRangePosition()
        {
            Assert.Throws<InvalidOperationException>(() => AttributeMovePlanner.Create(
                Before, "SampleId", null, "Missing", null));
            Assert.Throws<ArgumentOutOfRangeException>(() => AttributeMovePlanner.Create(
                Before, "SampleId", null, null, 99));
        }
    }
}
