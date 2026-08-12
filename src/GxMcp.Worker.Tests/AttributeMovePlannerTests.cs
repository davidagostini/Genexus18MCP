using System;
using GxMcp.Worker.Services.Structure;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AttributeMovePlannerTests
    {
        private static readonly string[] Before =
        {
            "ProcessoID",
            "ProcessoRPI_ProcessoID",
            "ProcessoNome",
            "ProcessoArmazemID",
            "ProcessoTipoOperacao",
            "RoteiroProcessoID",
            "RoteiroProcessoDescricao"
        };

        [Fact]
        public void After_MovesOnlyTargetAndKeepsRelativeOrder()
        {
            var plan = AttributeMovePlanner.Create(Before, "ProcessoRPI_ProcessoID",
                before: null, after: "RoteiroProcessoID", position: null);

            Assert.Equal(1, plan.OldPosition);
            Assert.Equal(5, plan.NewPosition);
            Assert.Equal(new[]
            {
                "ProcessoID", "ProcessoNome", "ProcessoArmazemID",
                "ProcessoTipoOperacao", "RoteiroProcessoID",
                "ProcessoRPI_ProcessoID", "RoteiroProcessoDescricao"
            }, plan.OrderedNames);
        }

        [Fact]
        public void Before_AndPosition_AreZeroBased()
        {
            var before = AttributeMovePlanner.Create(Before, "ProcessoRPI_ProcessoID",
                before: "RoteiroProcessoDescricao", after: null, position: null);
            var position = AttributeMovePlanner.Create(Before, "ProcessoRPI_ProcessoID",
                before: null, after: null, position: 5);

            Assert.Equal(before.OrderedNames, position.OrderedNames);
        }

        [Fact]
        public void RequiresExactlyOneSelector()
        {
            Assert.Throws<ArgumentException>(() => AttributeMovePlanner.Create(
                Before, "ProcessoID", null, null, null));
            Assert.Throws<ArgumentException>(() => AttributeMovePlanner.Create(
                Before, "ProcessoID", "ProcessoNome", null, 0));
        }

        [Fact]
        public void RejectsMissingReferenceAndOutOfRangePosition()
        {
            Assert.Throws<InvalidOperationException>(() => AttributeMovePlanner.Create(
                Before, "ProcessoID", null, "Missing", null));
            Assert.Throws<ArgumentOutOfRangeException>(() => AttributeMovePlanner.Create(
                Before, "ProcessoID", null, null, 99));
        }
    }
}
