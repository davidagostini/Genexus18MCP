using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GeneXusSyntaxTreeTests
    {
        [Fact]
        public void Parse_ExtractsSubroutinesAndEvents()
        {
            string source = @"
Event Start
    &Title = 'Hello'
EndEvent

Sub 'CalculateTotals'
    &Total = &Qty * &Price
EndSub

Sub 'PrintReport'
    Do 'CalculateTotals'
EndSub
";

            var tree = GeneXusSyntaxTree.Parse(source);

            Assert.True(tree.ContainsEvent("Start"));
            Assert.True(tree.ContainsSubroutine("CalculateTotals"));
            Assert.True(tree.ContainsSubroutine("PrintReport"));

            var sub = tree.FindSubroutine("CalculateTotals");
            Assert.NotNull(sub);
            Assert.Contains("&Total = &Qty * &Price", sub.Content);
        }

        [Fact]
        public void Parse_IgnoresComments()
        {
            string source = @"
// Sub 'OldSubroutine'
// EndSub

Sub 'RealSub'
    &A = 1
EndSub
";

            var tree = GeneXusSyntaxTree.Parse(source);

            Assert.False(tree.ContainsSubroutine("OldSubroutine"));
            Assert.True(tree.ContainsSubroutine("RealSub"));
        }
    }
}
