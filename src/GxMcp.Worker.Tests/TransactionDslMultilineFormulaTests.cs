using GxMcp.Worker.Parsers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #492: `mode=ops` serializes the whole Transaction Structure and
    // reinterprets it line by line. GeneXus allows an attribute formula to wrap
    // across physical lines, so each continuation line became its own node: the
    // truncated first line failed validation, or a continuation was read as a
    // level name - and in one observed case the continuation was created as a
    // stray global Attribute that survived the failed write.
    public class TransactionDslMultilineFormulaTests
    {
        /// The exact attribute from the report: a wrapped formula in the middle
        /// of a Structure.
        private const string WrappedFormula = "PessoaEnderecosGoogleMapsPeloBairro : VARCHAR(1024) // \"...\", [Formula: PessoaEnderecosBairro + \" , \" + PessoaEnderecosMunicipioNome + \" - \" + PessoaEnderecosMunicipioUfSigla +\n\t\" , \" + PessoaEnderecosPaisNome\n\tIF (not PessoaEnderecosBairro.isempty()) and not PessoaEnderecosMunicipioId.isempty();]";

        [Fact]
        public void WrappedFormula_FoldsIntoOneLine()
        {
            var lines = TransactionDslParser.SplitDslLines(WrappedFormula);

            Assert.Single(lines);
            Assert.Contains("PaisNome", lines[0]);
            Assert.Contains("isempty()", lines[0]);
        }

        [Fact]
        public void FoldedLine_KeepsTheWholeFormula()
        {
            var line = Assert.Single(TransactionDslParser.SplitDslLines(WrappedFormula));

            // The formula must be complete: the reported failure quoted only the
            // first physical line, ending at the `+`.
            Assert.DoesNotContain("UfSigla +$", line.TrimEnd());
            Assert.Contains("UfSigla + \" , \" + PessoaEnderecosPaisNome", line);
            Assert.Contains("IF (not PessoaEnderecosBairro.isempty())", line);
            Assert.EndsWith("]", line);
        }

        [Fact]
        public void ContinuationLine_NeverBecomesItsOwnNode()
        {
            var lines = TransactionDslParser.SplitDslLines(WrappedFormula);

            // The continuation text must live inside the attribute's own entry
            // and nowhere else. A line with no `:` is what the parser
            // reinterprets as a level name, which is how `IF McpTstFml2Valor >
            // 0;]` produced "invalid characters for level name".
            Assert.Single(lines);
            Assert.DoesNotContain("\t", lines[0]);
            Assert.DoesNotContain("\n", lines[0]);
            Assert.Contains("IF (not PessoaEnderecosBairro.isempty())", lines[0]);
        }

        [Fact]
        public void SeveralWrappedAttributes_EachFoldIndependently()
        {
            var text = string.Join("\r\n", new[]
            {
                "McpTstFml2Valor : NUMERIC(4)",
                "McpTstFml2Resultado : NUMERIC(6) // \"\", [Formula: McpTstFml2Valor * 2 +",
                "\tMcpTstFml2Extra",
                "\tIF McpTstFml2Valor > 0;]",
                "McpTstFml2Depois : NUMERIC(4)"
            });

            var lines = TransactionDslParser.SplitDslLines(text);

            Assert.Equal(3, lines.Count);
            Assert.Contains("McpTstFml2Valor * 2 + McpTstFml2Extra IF McpTstFml2Valor > 0;", lines[1]);
            Assert.Equal("McpTstFml2Depois : NUMERIC(4)", lines[2]);
        }

        [Fact]
        public void UnwrappedAttributes_AreUnaffected()
        {
            var text = "A : NUMERIC(4)\r\nB : CHARACTER(1)\r\nC : VARCHAR(20) // \"\", [Formula: A + B;]";
            var lines = TransactionDslParser.SplitDslLines(text);

            Assert.Equal(3, lines.Count);
            Assert.Equal("A : NUMERIC(4)", lines[0]);
            Assert.Contains("[Formula: A + B;]", lines[2]);
        }

        [Fact]
        public void MixedLineEndings_DoNotChangeTheFold()
        {
            var lf = WrappedFormula.Replace("\r\n", "\n");
            var cr = WrappedFormula.Replace("\r\n", "\r");

            Assert.Single(TransactionDslParser.SplitDslLines(lf));
            Assert.Single(TransactionDslParser.SplitDslLines(cr));
        }

        [Fact]
        public void BracketInsideAQuotedDescription_DoesNotAffectTheFold()
        {
            // A description may legitimately contain '[' or ']'. Counting them
            // would either truncate the formula or swallow the next attribute.
            var text = "Attr : VARCHAR(50) // \"[nota] qualquer coisa\", [Formula: OutroAttr;";

            var lines = TransactionDslParser.SplitDslLines(text);

            Assert.Single(lines);
            Assert.Contains("[Formula: OutroAttr;", lines[0]);
        }

        [Fact]
        public void UnterminatedFormula_IsStillEmittedForValidation()
        {
            // Silently dropping the text would hide the real syntax error; the
            // caller must see something it can report.
            var lines = TransactionDslParser.SplitDslLines("Attr : VARCHAR(20) [Formula: A + B");

            Assert.Single(lines);
            Assert.Contains("Attr", lines[0]);
        }

        [Theory]
        [InlineData("X : NUMERIC(1) [Formula: A;]", 0)]
        [InlineData("X : NUMERIC(1) [Formula: A", 1)]
        [InlineData("X : NUMERIC(1) [Formula: A;] ", 0)]
        [InlineData("X : NUMERIC(1) [Formula: Iif(Code = ']x', 1, 2)", 1)]
        [InlineData("X : NUMERIC(1) [Formula: Iif(Code = 'it''s ]x', 1, 2);]", 0)]
        [InlineData("X : NUMERIC(1) [Formula: Values[1]", 1)]
        public void BracketDepth_ReflectsOnlyTheFormulaRegion(string line, int expected)
        {
            Assert.Equal(expected, TransactionDslParser.FormulaBracketDepth(line));
        }

        /// The DSL text to parsed-nodes pipeline, without the Knowledge Base.
        ///
        /// `Parse` itself cannot be exercised here because it needs a real
        /// `Transaction`. An earlier version of this guard tried to inspect its
        /// IL instead, which asserted nothing about behaviour - the fold and the
        /// parse are both pure, so the composition is extracted and asserted
        /// directly. That is what makes a mutation restoring the physical-line
        /// split fail, which is the regression that matters.
        [Fact]
        public void WrappedFormula_ProducesOneAttributeNodeWithACompleteFormula()
        {
            var nodes = TransactionDslParser.ParseNodes(WrappedFormula);
            var root = nodes.Count == 1 && nodes[0].IsCompound ? nodes[0].Children : nodes;

            var attributes = root.FindAll(n => n.Name == "PessoaEnderecosGoogleMapsPeloBairro");
            Assert.Single(attributes);

            var formula = attributes[0].Formula;
            Assert.NotNull(formula);
            Assert.Contains("PessoaEnderecosPaisNome", formula);
            Assert.Contains("IF (not PessoaEnderecosBairro.isempty())", formula);
        }

        [Fact]
        public void WrappedFormula_DoesNotProduceANodeForTheContinuation()
        {
            // The reported failure: the continuation `IF McpTstFml2Valor > 0;]`
            // was read as its own entry, then rejected as an invalid level name.
            var nodes = TransactionDslParser.ParseNodes(
                "McpTstFml2Valor : NUMERIC(4)\r\nMcpTstFml2Resultado : NUMERIC(6) [Formula: McpTstFml2Valor * 2 +\r\n\tIF McpTstFml2Valor > 0;]");
            var root = nodes.Count == 1 && nodes[0].IsCompound ? nodes[0].Children : nodes;

            Assert.Equal(2, root.Count);
            Assert.DoesNotContain(root, n => n.Name != null && n.Name.StartsWith("IF McpTstFml2Valor"));
        }

        [Fact]
        public void LineWithoutAFormula_HasZeroDepth()
        {
            Assert.Equal(0, TransactionDslParser.FormulaBracketDepth("Level1"));
            Assert.Equal(0, TransactionDslParser.FormulaBracketDepth("Attr : Numeric(4)"));
        }

        [Fact]
        public void EmptyAndNullText_ProduceNoLines()
        {
            Assert.Empty(TransactionDslParser.SplitDslLines(""));
            Assert.Empty(TransactionDslParser.SplitDslLines(null));
            Assert.Empty(TransactionDslParser.SplitDslLines("   \r\n  \r\n"));
        }

        [Fact]
        public void CreatedGlobalRegistry_IsEmptyOutsideAParse()
        {
            // The registry is a rollback ledger; it must never leak state between
            // calls, or a later failed parse would delete attributes it did not
            // create.
            Assert.Empty(TransactionDslParser.CreatedGlobals);
            TransactionDslParser.UndoCreatedGlobals();
            Assert.Empty(TransactionDslParser.CreatedGlobals);
        }

        [Fact]
        public void UndoIsSafeToCallTwice()
        {
            TransactionDslParser.UndoCreatedGlobals();
            TransactionDslParser.UndoCreatedGlobals();
            Assert.Empty(TransactionDslParser.CreatedGlobals);
        }
    }
}
