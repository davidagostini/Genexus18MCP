using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // Issue #62 — atomic create/update. These tests pin the PURE, KB-free parts of
    // AtomicCreateService: the pre-save field validation (per-field error attribution,
    // including the issue #56 UnknownType failure mode), the Parm rule rendering, the
    // KB reference pre-flight (ValidateKbReferences with an injected resolver), and the
    // optimistic version fingerprint. The SDK-touching orchestration (CreateObject /
    // AddVariables / WriteObject / compensation) is exercised live over HTTP; these
    // tests lock the decision logic so a payload shape change cannot silently slip.
    public class AtomicCreateServiceTests
    {
        // ── ParseAndValidate: happy paths ──────────────────────────────────────

        [Fact]
        public void ParseAndValidate_FullDefinition_NoErrors()
        {
            var args = JObject.Parse(@"{
                ""type"": ""Procedure"",
                ""name"": ""MonitorIntegracaoReativar"",
                ""variables"": [
                    { ""varName"": ""MonitorIntegracaoId"", ""typeName"": ""Numeric"" },
                    { ""name"": ""DataProcessamento"", ""typeName"": ""Date"" }
                ],
                ""rules"": [ ""Parm(in:&MonitorIntegracaoId);"" ],
                ""source"": ""// source da procedure"",
                ""validate"": true
            }");

            var spec = AtomicCreateService.ParseAndValidate(args);

            Assert.Empty(spec.Errors);
            Assert.Equal("Procedure", spec.Type);
            Assert.Equal("MonitorIntegracaoReativar", spec.Name);
            Assert.Equal(2, spec.Variables.Count);
            Assert.Contains("Parm(in:&MonitorIntegracaoId);", spec.RulesText);
            Assert.Equal("// source da procedure", spec.Source);
            Assert.True(spec.Validate);
        }

        [Fact]
        public void ParseAndValidate_WithProperties_PreservesProperties()
        {
            var args = JObject.Parse(@"{
                ""type"": ""Procedure"",
                ""name"": ""P1"",
                ""properties"": {
                    ""Description"": ""My Procedure Description"",
                    ""Folder"": ""Module1""
                }
            }");

            var spec = AtomicCreateService.ParseAndValidate(args);

            Assert.Empty(spec.Errors);
            Assert.NotNull(spec.Properties);
            Assert.Equal("My Procedure Description", spec.Properties["Description"]?.ToString());
            Assert.Equal("Module1", spec.Properties["Folder"]?.ToString());
        }

        [Fact]
        public void ParseAndValidate_TypeAndNameRequired()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject());
            Assert.Contains(spec.Errors, e => e["field"]?.ToString() == "type");
            Assert.Contains(spec.Errors, e => e["field"]?.ToString() == "name");
        }

        [Fact]
        public void ParseAndValidate_InvalidMode_ReportsFieldError()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["mode"] = "upsert"
            });
            Assert.Contains(spec.Errors, e => e["field"]?.ToString() == "mode");
        }

        // ── Issue #56 regression: unknown/typo variable types caught BEFORE save ─

        // Syntax-level: VariableTypeResolver marks bare identifiers (e.g. "IDManual")
        // as DomainReference candidates — legitimate SDT/BC/Domain references. Only
        // strings that are neither a known type NOR a bare identifier (spaces, digits,
        // invalid chars) are rejected at parse time. Existence of bare names in the KB
        // is the separate ValidateKbReferences pre-flight (next group).
        [Fact]
        public void ParseAndValidate_SyntaxUnrecognizedType_AttributedToExactIndex()
        {
            var args = JObject.Parse(@"{
                ""type"": ""Procedure"",
                ""name"": ""P"",
                ""variables"": [
                    { ""varName"": ""Ok"", ""typeName"": ""Numeric"" },
                    { ""varName"": ""Broken"", ""typeName"": ""ID Manua"" },
                    { ""varName"": ""AlsoBroken"", ""typeName"": ""123Bad"" }
                ]
            }");

            var spec = AtomicCreateService.ParseAndValidate(args);

            var fieldErrs = spec.Errors.Select(e => new { Field = e["field"]?.ToString(), Msg = e["errors"]?.FirstOrDefault()?.ToString() }).ToList();
            Assert.Equal(2, spec.Errors.Count);
            Assert.Contains(fieldErrs, e => e.Field == "variables[1]" && e.Msg.Contains("ID Manua"));
            Assert.Contains(fieldErrs, e => e.Field == "variables[2]" && e.Msg.Contains("123Bad"));
            // The valid first variable must NOT be reported.
            Assert.DoesNotContain(fieldErrs, e => e.Field == "variables[0]");
        }

        [Fact]
        public void ParseAndValidate_MissingVarName_AttributedToExactIndex()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["variables"] = new JArray(
                    new JObject { ["typeName"] = "Numeric" })
            });
            var fieldErrs = spec.Errors.Select(e => e["field"]?.ToString()).ToList();
            Assert.Contains("variables[0]", fieldErrs);
        }

        [Fact]
        public void ParseAndValidate_MalformedRule_UnbalancedParens()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["rules"] = new JArray("Parm(in:&Id;")
            });
            Assert.Contains(spec.Errors, e =>
                e["field"]?.ToString() == "rules" &&
                e["errors"]?.FirstOrDefault()?.ToString().Contains("unbalanced") == true);
        }

        [Fact]
        public void ParseAndValidate_NoVariables_Valid()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["source"] = "// only source"
            });
            Assert.Empty(spec.Errors);
        }

        // ── ValidateKbReferences (issue #56): bare names must EXIST in the KB ──

        // A bare name like "IDManua" (typo of a Domain) parses fine as a
        // DomainReference candidate — the KB-level pre-flight is what catches it,
        // attributed to the exact array index, before any save.
        [Fact]
        public void ValidateKbReferences_UnknownBareName_AttributedToExactIndex()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["variables"] = new JArray(
                    new JObject { ["varName"] = "Good", ["typeName"] = "IDManual" },
                    new JObject { ["varName"] = "Bad", ["typeName"] = "IDManua" },
                    new JObject { ["varName"] = "Primitive", ["typeName"] = "Numeric" })
            });

            // Fake KB resolver: only "IDManual" exists (plus primitives never reach
            // this check because their canonical type is not DomainReference).
            AtomicCreateService.ValidateKbReferences(spec, name =>
                string.Equals(name, "IDManual", System.StringComparison.OrdinalIgnoreCase));

            var fieldErrs = spec.Errors.Select(e => new { Field = e["field"]?.ToString(), Msg = e["errors"]?.FirstOrDefault()?.ToString() }).ToList();
            Assert.Single(spec.Errors);
            Assert.Contains(fieldErrs, e => e.Field == "variables[1]" && e.Msg.Contains("IDManua"));
            Assert.DoesNotContain(fieldErrs, e => e.Field == "variables[0]");
            Assert.DoesNotContain(fieldErrs, e => e.Field == "variables[2]");
        }

        [Fact]
        public void ValidateKbReferences_AllKnown_NoErrors()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["variables"] = new JArray(
                    new JObject { ["varName"] = "A", ["typeName"] = "IDManual" },
                    new JObject { ["varName"] = "B", ["typeName"] = "SdtAluno" })
            });
            AtomicCreateService.ValidateKbReferences(spec, _ => true);
            Assert.Empty(spec.Errors);
        }

        [Fact]
        public void ValidateKbReferences_NullResolver_Skips()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["variables"] = new JArray(new JObject { ["varName"] = "A", ["typeName"] = "IDManual" })
            });
            AtomicCreateService.ValidateKbReferences(spec, null); // no KB → defer to write path
            Assert.Empty(spec.Errors);
        }

        // ── RenderParmRule ─────────────────────────────────────────────────────

        [Theory]
        [InlineData("[]", "")]
        [InlineData("[\"&Id\"]", "Parm(in:&Id);")]
        [InlineData("[\"&Id\", \"out:&Msg\", \"inout:&Ctx\"]", "Parm(in:&Id, out:&Msg, inout:&Ctx);")]
        [InlineData("[\"Id\", \"out:Msg\"]", "Parm(in:&Id, out:&Msg);")]
        [InlineData("[\"&A\", \"  &B  \"]", "Parm(in:&A, in:&B);")]
        public void RenderParmRule_Variants(string parmsJson, string expected)
        {
            var parms = JArray.Parse(parmsJson);
            Assert.Equal(expected, AtomicCreateService.RenderParmRule(parms));
        }

        [Fact]
        public void RenderParmRule_Null_Empty()
        {
            Assert.Equal("", AtomicCreateService.RenderParmRule(null));
        }

        // ── ComputeVersion (optimistic concurrency token) ─────────────────────

        [Fact]
        public void ComputeVersion_DifferentContent_DifferentToken()
        {
            string a = AtomicCreateService.ComputeVersion("// a", "", "&X : Numeric");
            string b = AtomicCreateService.ComputeVersion("// b", "", "&X : Numeric");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void ComputeVersion_SameContent_SameToken()
        {
            string a = AtomicCreateService.ComputeVersion("// a", "Parm(in:&X);", "&X : Numeric");
            string b = AtomicCreateService.ComputeVersion("// a", "Parm(in:&X);", "&X : Numeric");
            Assert.Equal(a, b);
            Assert.Equal(64, a.Length); // SHA-256 hex
        }

        [Fact]
        public void ComputeVersion_NullParts_Stable()
        {
            string a = AtomicCreateService.ComputeVersion(null, null, null);
            string b = AtomicCreateService.ComputeVersion("", "", "");
            Assert.Equal(a, b);
        }
    }
}
