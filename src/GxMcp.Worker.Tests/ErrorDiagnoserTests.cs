using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ErrorDiagnoserTests
    {
        [Fact]
        public void Diagnose_Spc0005_VariableNotDefined_SuggestsAddingVariable()
        {
            string logLine = "error spc0005: Variable '&TotalAmount' not defined in 'CalculateInvoice'.";
            var suggestions = ErrorDiagnoser.Diagnose(new[] { logLine }, "CalculateInvoice");

            Assert.NotEmpty(suggestions);
            var s = suggestions.First();
            Assert.Equal("spc0005", s.ErrorCode);
            Assert.Equal("genexus_variable", s.Tool);
            Assert.Equal("add", s.Arguments["action"]?.ToString());
            Assert.Equal("&TotalAmount", s.Arguments["name"]?.ToString());
            Assert.Equal("CalculateInvoice", s.Arguments["object"]?.ToString());
            Assert.Contains("&TotalAmount", s.Explanation);
        }

        [Fact]
        public void Diagnose_Spc0005_Portuguese_SuggestsAddingVariable()
        {
            string logLine = "error spc0005: Variável '&SaldoFinal' não definida.";
            var suggestions = ErrorDiagnoser.Diagnose(new[] { logLine }, "CalcularSaldo");

            Assert.NotEmpty(suggestions);
            var s = suggestions.First();
            Assert.Equal("spc0005", s.ErrorCode);
            Assert.Equal("&SaldoFinal", s.Arguments["name"]?.ToString());
        }

        [Fact]
        public void Diagnose_Spc0107_NotABusinessComponent_SuggestsSettingProperty()
        {
            string logLine = "error spc0107: 'Customer' is not a Business Component.";
            var suggestions = ErrorDiagnoser.Diagnose(new[] { logLine }, "CustomerSaveProc");

            Assert.NotEmpty(suggestions);
            var s = suggestions.First();
            Assert.Equal("spc0107", s.ErrorCode);
            Assert.Equal("genexus_properties", s.Tool);
            Assert.Equal("set", s.Arguments["action"]?.ToString());
            Assert.Equal("Customer", s.Arguments["name"]?.ToString());
            Assert.Equal("BusinessComponent", s.Arguments["propertyName"]?.ToString());
            Assert.Equal("True", s.Arguments["value"]?.ToString());
        }

        [Fact]
        public void Diagnose_Spc0053_SubroutineNotDefined_SuggestsAddingSubroutine()
        {
            string logLine = "error spc0053: Subroutine 'LoadData' not defined.";
            var suggestions = ErrorDiagnoser.Diagnose(new[] { logLine }, "InvoicePanel");

            Assert.NotEmpty(suggestions);
            var s = suggestions.First();
            Assert.Equal("spc0053", s.ErrorCode);
            Assert.Equal("genexus_edit", s.Tool);
            Assert.Equal("InvoicePanel", s.Arguments["name"]?.ToString());
            Assert.Contains("Sub 'LoadData'", s.Explanation);
        }

        [Fact]
        public void Diagnose_Spc0011_ParmRuleMismatch_SuggestsReviewingRules()
        {
            string logLine = "error spc0011: 'parm' rule invalid for object 'ExportData'.";
            var suggestions = ErrorDiagnoser.Diagnose(new[] { logLine }, "ExportData");

            Assert.NotEmpty(suggestions);
            var s = suggestions.First();
            Assert.Equal("spc0011", s.ErrorCode);
            Assert.Equal("genexus_read", s.Tool);
            Assert.Equal("ExportData", s.Arguments["name"]?.ToString());
            Assert.Equal("Rules", s.Arguments["part"]?.ToString());
        }

        [Fact]
        public void Diagnose_Spc0038_AttributeNotInTable_SuggestsInspectingStructure()
        {
            string logLine = "error spc0038: Attribute 'CustomerEmail' is not in table 'Customer'.";
            var suggestions = ErrorDiagnoser.Diagnose(new[] { logLine });

            Assert.NotEmpty(suggestions);
            var s = suggestions.First();
            Assert.Equal("spc0038", s.ErrorCode);
            Assert.Equal("genexus_structure", s.Tool);
            Assert.Equal("Customer", s.Arguments["name"]?.ToString());
            Assert.Contains("CustomerEmail", s.Explanation);
        }

        [Fact]
        public void Diagnose_MultipleErrors_ProducesDistinctStructuredSuggestions()
        {
            var lines = new[]
            {
                "error spc0005: Variable '&Count' not defined.",
                "error spc0107: 'Product' is not a Business Component.",
                "warning spc0096: Duplicate event 'Enter'."
            };

            var suggestions = ErrorDiagnoser.Diagnose(lines, "ProcessBatch");
            Assert.Equal(2, suggestions.Count);
            Assert.Contains(suggestions, s => s.ErrorCode == "spc0005");
            Assert.Contains(suggestions, s => s.ErrorCode == "spc0107");
        }

        [Fact]
        public void Diagnose_Src0089_SubroutineOrder_SuggestsPlacingEntrypointBeforeSubs()
        {
            string logLine = "[VALIDATION]: src0089: Expressão inválida: esperando a definição da sub-rotina. (Procedure 'Test_PCalculaDesconto' Source, Linha: 22, Char: 3)";
            var suggestions = ErrorDiagnoser.Diagnose(new[] { logLine }, "Test_PCalculaDesconto");

            Assert.NotEmpty(suggestions);
            var s = suggestions.First();
            Assert.Equal("src0089", s.ErrorCode);
            Assert.Equal("genexus_edit", s.Tool);
            Assert.Equal("Test_PCalculaDesconto", s.Arguments["name"]?.ToString());
            Assert.Contains("Do 'Sub'", s.Explanation);
        }

        [Fact]
        public void Diagnose_Src0216_AttributeNotDefined_SuggestsAddingVariable()
        {
            string logLine = "src0216: Attribute 'UnknownVar' not defined.";
            var suggestions = ErrorDiagnoser.Diagnose(new[] { logLine });

            Assert.NotEmpty(suggestions);
            var s = suggestions.First();
            Assert.Equal("src0216", s.ErrorCode);
            Assert.Equal("genexus_variable", s.Tool);
            Assert.Equal("&UnknownVar", s.Arguments["name"]?.ToString());
        }
    }
}
