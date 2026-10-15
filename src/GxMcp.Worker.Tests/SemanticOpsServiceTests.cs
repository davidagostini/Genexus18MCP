using GxMcp.Worker;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SemanticOpsServiceTests
    {
        private const string MultilineFormula = "Id* : Numeric(4)\nTotal : Numeric(6) // \"Total value\", [Formula: Amount * 2\n\tIF Amount > 0;]";

        [Theory]
        [InlineData(null)]
        [InlineData("Total")]
        public void AddAttribute_InsertsAfterCompleteMultilineFormula(string after)
        {
            var op = SemanticOp.From(new JObject { ["op"] = "add_attribute", ["name"] = "Extra", ["type"] = "Numeric(4)", ["after"] = after });
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(MultilineFormula, new[] { op }, "strict");
            Assert.False(outcome.Aborted);
            Assert.Equal(MultilineFormula + "\nExtra : Numeric(4)", outcome.Text);
        }

        [Fact]
        public void AddAfterFormulaInMiddle_PreservesLaterAttributes()
        {
            var op = SemanticOp.From(new JObject { ["op"] = "add_attribute", ["name"] = "Extra", ["type"] = "Numeric(4)", ["after"] = "Total" });
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(MultilineFormula + "\nTail : Numeric(2)", new[] { op }, "strict");
            Assert.Equal(MultilineFormula + "\nExtra : Numeric(4)\nTail : Numeric(2)", outcome.Text);
        }

        [Fact]
        public void SetType_PreservesWholeMultilineFormula()
        {
            var op = SemanticOp.From(new JObject { ["op"] = "set_attribute", ["name"] = "Total", ["type"] = "Numeric(8)" });
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(MultilineFormula, new[] { op }, "strict");
            Assert.Equal(MultilineFormula.Replace("Total : Numeric(6)", "Total : Numeric(8)"), outcome.Text);
        }

        [Fact]
        public void SetAttribute_ReplacesCompleteFormulaAndPreservesDescription()
        {
            var op = SemanticOp.From(new JObject { ["op"] = "set_attribute", ["name"] = "Total", ["formula"] = "Amount * 3;" });
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(MultilineFormula, new[] { op }, "strict");
            Assert.False(outcome.Aborted);
            Assert.Contains("[Formula: Amount * 3;]", outcome.Text);
            Assert.Contains("\"Total value\"", outcome.Text);
            Assert.DoesNotContain("IF Amount", outcome.Text);
            Assert.DoesNotContain("Amount * 2", outcome.Text);
            Assert.Equal(1, outcome.Text.Split(new[] { "[Formula:" }, System.StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void SetFormula_KeepsDescriptionBeforeQuotedFormulaAndPreservesMetadata()
        {
            string dsl = "Label : VarChar(20) // \"Chosen label\", [Formula: Old()\n;], [Nullable]";
            var op = SemanticOp.From(new JObject { ["op"] = "set_attribute", ["name"] = "Label", ["formula"] = "New(\"quoted [value]\");" });
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(dsl, new[] { op }, "strict");
            Assert.Equal("Label : VarChar(20) // \"Chosen label\", [Formula: New(\"quoted [value]\");], [Nullable]", outcome.Text);
        }

        [Theory]
        [InlineData("Iif(Code = ']x', 1, 2)")]
        [InlineData("Iif(Code = 'it''s ]x', 1, 2)")]
        [InlineData("Iif(Code = \"it\"\"s ]x\", 1, 2)")]
        public void ReplaceFormula_RespectsBothQuotedStringDelimiters(string oldFormula)
        {
            string dsl = "Total : Numeric(6) // \"Chosen\", [Formula: " + oldFormula + "\n;], [Nullable]\nTail : Numeric(4)";
            var op = SemanticOp.From(new JObject { ["op"] = "set_attribute", ["name"] = "Total", ["formula"] = "Amount * 3;" });
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(dsl, new[] { op }, "strict");
            Assert.Equal("Total : Numeric(6) // \"Chosen\", [Formula: Amount * 3;], [Nullable]\nTail : Numeric(4)", outcome.Text);
            op = SemanticOp.From(new JObject { ["op"] = "set_attribute", ["name"] = "Total", ["formula"] = "" });
            outcome = new SemanticOpsService().ApplyTransactionStructureDsl(dsl, new[] { op }, "strict");
            Assert.Equal("Total : Numeric(6) // \"Chosen\", [Nullable]\nTail : Numeric(4)", outcome.Text);
        }

        [Fact]
        public void ClearDescription_PreservesFormulaCommentSyntax()
        {
            var op = SemanticOp.From(new JObject { ["op"] = "set_attribute", ["name"] = "Total", ["description"] = "" });
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(MultilineFormula, new[] { op }, "strict");
            Assert.Equal("Id* : Numeric(4)\nTotal : Numeric(6) // [Formula: Amount * 2\n\tIF Amount > 0;]", outcome.Text);
        }

        [Fact]
        public void BatchRemove_RemovesCompleteFormulaAndPreservesNestedLayout()
        {
            string dsl = MultilineFormula + "\nLines\n{\n\tLineId* : Numeric(4)\n}";
            var ops = new[] {
                SemanticOp.From(new JObject { ["op"] = "remove_attribute", ["name"] = "Total" }),
                SemanticOp.From(new JObject { ["op"] = "add_attribute", ["name"] = "Extra", ["type"] = "Numeric(4)" })
            };
            var outcome = new SemanticOpsService().ApplyTransactionStructureDsl(dsl, ops, "strict");
            Assert.False(outcome.Aborted);
            Assert.Equal("Id* : Numeric(4)\nExtra : Numeric(4)\nLines\n{\n\tLineId* : Numeric(4)\n}", outcome.Text);
        }

        // Bug #1: dryRun capability check for Transaction Structure remove_attribute.
        [Theory]
        [InlineData("CustomerId : Numeric(8.0)\nCustomerName : Character(40)", "CustomerId", false)]
        [InlineData("CustomerId* : Numeric(8.0)\nCustomerName : Character(40)", "CustomerId", true)]
        [InlineData("CustomerId* : Numeric(8.0)\nCustomerName : Character(40)", "CustomerName", false)]
        [InlineData("CustomerId* : Numeric(8.0)", "customerid", true)]        // case-insensitive
        [InlineData("CustomerId* : Numeric(8.0)", "Missing", false)]          // not present
        public void IsKeyAttributeInDsl_DetectsKeyMarker(string dsl, string name, bool expected)
        {
            Assert.Equal(expected, SemanticOpsService.IsKeyAttributeInDsl(dsl, name));
        }

        [Fact]
        public void IsKeyAttributeInDsl_NullOrEmpty_IsFalse()
        {
            Assert.False(SemanticOpsService.IsKeyAttributeInDsl(null, "X"));
            Assert.False(SemanticOpsService.IsKeyAttributeInDsl("X* : Numeric(4.0)", null));
            Assert.False(SemanticOpsService.IsKeyAttributeInDsl("", "X"));
        }

        [Fact]
        public void SetAttribute_UpdatesType()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Structure><Attribute><Name>CustomerId</Name>" +
                "<Type>Numeric(8.0)</Type></Attribute></Structure></Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"set_attribute\",\"name\":\"CustomerId\",\"type\":\"Numeric(10.0)\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.Contains("Numeric(10.0)", result);
            Assert.DoesNotContain("Numeric(8.0)", result);
        }

        [Fact]
        public void AddAttribute_AppendsUnderStructure()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Structure><Attribute><Name>CustomerId</Name>" +
                "<Type>Numeric(8.0)</Type></Attribute></Structure></Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"add_attribute\",\"name\":\"CustomerName\",\"type\":\"Character(40)\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.Contains("<Name>CustomerName</Name>", result);
            Assert.Contains("<Type>Character(40)</Type>", result);
        }

        [Fact]
        public void RemoveAttribute_DeletesMatchingAttribute()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Structure>" +
                "<Attribute><Name>CustomerId</Name><Type>Numeric(8.0)</Type></Attribute>" +
                "<Attribute><Name>CustomerName</Name><Type>Character(40)</Type></Attribute>" +
                "</Structure></Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"remove_attribute\",\"name\":\"CustomerName\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.DoesNotContain("CustomerName", result);
            Assert.Contains("CustomerId", result);
        }

        [Fact]
        public void AddRule_AppendsRuleElement()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Rules><Rule><Text>error('x') if true;</Text></Rule></Rules>" +
                "</Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"add_rule\",\"text\":\"noaccept(CustomerId);\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.Contains("<Text>noaccept(CustomerId);</Text>", result);
        }

        [Fact]
        public void RemoveRule_DeletesByMatchSubstring()
        {
            string trnXml =
                "<Transaction><Name>Customer</Name>" +
                "<Rules>" +
                "<Rule><Text>error('a') if true;</Text></Rule>" +
                "<Rule><Text>noaccept(CustomerId);</Text></Rule>" +
                "</Rules></Transaction>";

            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse(
                    "{\"op\":\"remove_rule\",\"match\":\"noaccept\"}"))
            };
            var result = svc.Apply(trnXml, "Transaction", ops);
            Assert.DoesNotContain("noaccept", result);
            Assert.Contains("error('a')", result);
        }

        [Fact]
        public void UnknownOp_Throws()
        {
            string trnXml = "<Transaction><Name>Customer</Name></Transaction>";
            var svc = new SemanticOpsService();
            var ops = new[] {
                SemanticOp.From(JObject.Parse("{\"op\":\"frobnicate\",\"x\":1}"))
            };
            var ex = Assert.Throws<UsageException>(() => svc.Apply(trnXml, "Transaction", ops));
            Assert.Equal("usage_error", ex.Code);
        }

        [Fact]
        public void SetProperty_UpdatesTopLevelElement()
        {
            string xml = "<Transaction><Name>Customer</Name><Description>old</Description></Transaction>";
            var svc = new SemanticOpsService();
            var ops = new[] { SemanticOp.From(JObject.Parse(
                "{\"op\":\"set_property\",\"path\":\"/Description\",\"value\":\"new\"}")) };
            var result = svc.Apply(xml, "Transaction", ops);
            Assert.Contains("<Description>new</Description>", result);
        }
    }
}
