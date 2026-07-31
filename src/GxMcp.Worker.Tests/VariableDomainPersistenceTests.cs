using System.Collections.Generic;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // issue #56 — post-save Domain-reference verification on the add-variable path.
    // DroppedDomainBindings decides whether a persisted Variables part still carries
    // the Domain type of every variable the write bound to a Domain.
    public class VariableDomainPersistenceTests
    {
        private static List<(string VarName, string DomainName)> Bindings(params (string, string)[] pairs)
            => new List<(string, string)>(pairs);

        [Fact]
        public void DroppedDomainBindings_EmptyExpected_ReturnsEmpty()
        {
            var dropped = WriteService.DroppedDomainBindings("&EmpresaID : IDManual", Bindings());
            Assert.Empty(dropped);
        }

        [Fact]
        public void DroppedDomainBindings_AllBindingsPresent_ReturnsEmpty()
        {
            string text = "&EmpresaID : IDManual\n&ClienteID : IDManual\n&DataLimite : Data";
            var dropped = WriteService.DroppedDomainBindings(text,
                Bindings(("EmpresaID", "IDManual"), ("DataLimite", "Data")));
            Assert.Empty(dropped);
        }

        [Fact]
        public void DroppedDomainBindings_BindingLost_ReportsIt()
        {
            // The reported 18.0.16 symptom: variable persists without its Domain
            // reference, so the line shows the base type instead of the Domain name.
            string text = "&EmpresaID : NUMERIC(18)\n&ClienteID : IDManual";
            var dropped = WriteService.DroppedDomainBindings(text,
                Bindings(("EmpresaID", "IDManual"), ("ClienteID", "IDManual")));
            Assert.Single(dropped);
            Assert.Equal("EmpresaID", dropped[0].VarName);
            Assert.Equal("IDManual", dropped[0].DomainName);
        }

        [Fact]
        public void DroppedDomainBindings_MatchesNamesWhitespaceInsensitively()
        {
            string text = "&EmpresaID :   IDManual";
            var dropped = WriteService.DroppedDomainBindings(text, Bindings(("EmpresaID", "IDManual")));
            Assert.Empty(dropped);
        }

        [Fact]
        public void DroppedDomainBindings_CaseInsensitiveMatch()
        {
            string text = "&empresaID : idmanual";
            var dropped = WriteService.DroppedDomainBindings(text, Bindings(("EmpresaID", "IDManual")));
            Assert.Empty(dropped);
        }

        [Fact]
        public void DroppedDomainBindings_DoesNotMatchVariablePrefix()
        {
            // &EmpresaID2 must not satisfy the binding for &EmpresaID.
            string text = "&EmpresaID2 : IDManual";
            var dropped = WriteService.DroppedDomainBindings(text, Bindings(("EmpresaID", "IDManual")));
            Assert.Single(dropped);
            Assert.Equal("EmpresaID", dropped[0].VarName);
        }

        [Fact]
        public void DroppedDomainBindings_QualifiedDomainStillMatches()
        {
            // SDK may persist a fully-qualified domain name (e.g. RootModule.IDManual).
            string text = "&EmpresaID : RootModule.IDManual";
            var dropped = WriteService.DroppedDomainBindings(text, Bindings(("EmpresaID", "IDManual")));
            Assert.Empty(dropped);
        }

        [Fact]
        public void DroppedDomainBindings_VariableMissingEntirely_ReportsIt()
        {
            string text = "&ClienteID : IDManual";
            var dropped = WriteService.DroppedDomainBindings(text, Bindings(("EmpresaID", "IDManual")));
            Assert.Single(dropped);
            Assert.Equal("EmpresaID", dropped[0].VarName);
        }
    }
}
