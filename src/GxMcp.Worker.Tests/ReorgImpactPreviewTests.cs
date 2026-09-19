using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #61 — reorg_preview. These tests pin the PURE, KB-free parts of
    /// ReorgImpactService.Preview: the before/after column diff (type family,
    /// length, decimals, nullable per issue #57), destructive-warning emission,
    /// and the human-readable column/DDL renderers. The SDK-touching orchestration
    /// (level walk, Table structure read, deep ImpactDatabase) is exercised live
    /// over HTTP; these tests lock the decision logic so a payload shape change
    /// cannot silently slip.
    /// </summary>
    public class ReorgImpactPreviewTests
    {
        private static JObject Col(string name, string type, int len = 0, int dec = 0, bool nullable = false, bool isKey = false)
        {
            return new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["length"] = len,
                ["decimals"] = dec,
                ["nullable"] = nullable,
                ["isKey"] = isKey
            };
        }

        // ── TypeFamily ────────────────────────────────────────────────────────

        [Theory]
        [InlineData("NUMERIC(8,0)", "numeric")]
        [InlineData("Numeric", "numeric")]
        [InlineData("VARCHAR(40)", "character")]
        [InlineData("Character", "character")]
        [InlineData("Date", "date")]
        [InlineData("DateTime", "date")]
        [InlineData("GUID", "guid")]
        [InlineData("Boolean", "boolean")]
        [InlineData(null, "unknown")]
        [InlineData("", "unknown")]
        public void TypeFamily_Normalizes(string typeName, string expected)
        {
            Assert.Equal(expected, ReorgImpactService.TypeFamily(typeName));
        }

        // ── RenderColumnDef (issue #61 example shape) ─────────────────────────

        [Fact]
        public void RenderColumnDef_Issue61ExampleShape()
        {
            // "NUMERIC(18) NOT NULL" / "NUMERIC(18) NULL" — the exact before/after
            // strings the issue #61 conceptual response uses.
            Assert.Equal("NUMERIC(18) NOT NULL", ReorgImpactService.RenderColumnDef(Col("Qtd", "NUMERIC", 18)));
            Assert.Equal("NUMERIC(18) NULL", ReorgImpactService.RenderColumnDef(Col("Qtd", "NUMERIC", 18, 0, nullable: true)));
        }

        [Fact]
        public void RenderColumnDef_IncludesDecimals()
        {
            Assert.Equal("NUMERIC(18,2) NOT NULL", ReorgImpactService.RenderColumnDef(Col("V", "NUMERIC", 18, 2)));
        }

        [Fact]
        public void RenderColumnDef_NullInput_ReturnsAbsent()
        {
            Assert.Equal("<absent>", ReorgImpactService.RenderColumnDef(null));
        }

        // ── DiffColumns: happy path ───────────────────────────────────────────

        [Fact]
        public void DiffColumns_IdenticalStructures_NoChanges()
        {
            var before = new JArray
            {
                Col("Id", "NUMERIC", 8, 0, nullable: false, isKey: true),
                Col("Name", "CHARACTER", 40, 0, nullable: true)
            };
            var after = new JArray
            {
                Col("Id", "NUMERIC", 8, 0, nullable: false, isKey: true),
                Col("Name", "CHARACTER", 40, 0, nullable: true)
            };
            Assert.Empty(ReorgImpactService.DiffColumns(before, after));
        }

        // ── Issue #57 scenario: logical says NULL, physical says NOT NULL ─────

        [Fact]
        public void DiffColumns_NullableTrueAfter_EmitsChange_NotDestructive()
        {
            // #57: the logical structure declares Nullable but the physical Table
            // column is NOT NULL — the divergence a reorg would fix.
            var before = new JArray { Col("ProcessamentoQtd", "NUMERIC", 18, 0, nullable: false) };
            var after = new JArray { Col("ProcessamentoQtd", "NUMERIC", 18, 0, nullable: true) };

            var changes = ReorgImpactService.DiffColumns(before, after);
            var change = Assert.Single(changes);
            Assert.Equal("nullable", change["field"]?.ToString());
            Assert.Equal("NUMERIC(18) NOT NULL", change["before"]?.ToString());
            Assert.Equal("NUMERIC(18) NULL", change["after"]?.ToString());
            Assert.False(change["destructive"]?.ToObject<bool>());
        }

        [Fact]
        public void DiffColumns_NullableFalseAfter_EmitsDestructiveWarning()
        {
            // Making a nullable column NOT NULL can fail on existing NULL rows.
            var before = new JArray { Col("X", "CHARACTER", 40, 0, nullable: true) };
            var after = new JArray { Col("X", "CHARACTER", 40, 0, nullable: false) };

            var changes = ReorgImpactService.DiffColumns(before, after);
            var change = Assert.Single(changes);
            Assert.Equal("nullable", change["field"]?.ToString());
            Assert.True(change["destructive"]?.ToObject<bool>());
            Assert.NotNull(change["warning"]);
            Assert.Contains("NOT NULL", change["warning"]?["message"]?.ToString() ?? "");
        }

        // ── Type / length changes ─────────────────────────────────────────────

        [Fact]
        public void DiffColumns_CrossFamilyTypeChange_IsDestructive()
        {
            var before = new JArray { Col("V", "NUMERIC", 8, 0) };
            var after = new JArray { Col("V", "CHARACTER", 40, 0) };

            var changes = ReorgImpactService.DiffColumns(before, after);
            var change = Assert.Single(changes);
            Assert.Equal("type", change["field"]?.ToString());
            Assert.True(change["destructive"]?.ToObject<bool>());
            Assert.NotNull(change["warning"]);
        }

        [Fact]
        public void DiffColumns_LengthShrink_IsDestructive()
        {
            var before = new JArray { Col("Name", "CHARACTER", 40, 0) };
            var after = new JArray { Col("Name", "CHARACTER", 20, 0) };

            var changes = ReorgImpactService.DiffColumns(before, after);
            var change = Assert.Single(changes);
            Assert.Equal("length", change["field"]?.ToString());
            Assert.True(change["destructive"]?.ToObject<bool>());
        }

        [Fact]
        public void DiffColumns_LengthGrow_NotDestructive()
        {
            var before = new JArray { Col("Name", "CHARACTER", 20, 0) };
            var after = new JArray { Col("Name", "CHARACTER", 40, 0) };

            var changes = ReorgImpactService.DiffColumns(before, after);
            var change = Assert.Single(changes);
            Assert.False(change["destructive"]?.ToObject<bool>());
            Assert.Null(change["warning"]);
        }

        // ── Added / dropped columns ───────────────────────────────────────────

        [Fact]
        public void DiffColumns_AddedColumn_NotDestructive()
        {
            var before = new JArray { Col("Id", "NUMERIC", 8, 0) };
            var after = new JArray { Col("Id", "NUMERIC", 8, 0), Col("NewCol", "CHARACTER", 20, 0) };

            var changes = ReorgImpactService.DiffColumns(before, after);
            var change = Assert.Single(changes);
            Assert.Equal("added", change["field"]?.ToString());
            Assert.Equal("<absent>", change["before"]?.ToString());
            Assert.False(change["destructive"]?.ToObject<bool>());
        }

        [Fact]
        public void DiffColumns_DroppedColumn_IsDestructive()
        {
            var before = new JArray { Col("Id", "NUMERIC", 8, 0), Col("Doomed", "CHARACTER", 20, 0) };
            var after = new JArray { Col("Id", "NUMERIC", 8, 0) };

            var changes = ReorgImpactService.DiffColumns(before, after);
            var change = Assert.Single(changes);
            Assert.Equal("dropped", change["field"]?.ToString());
            Assert.True(change["destructive"]?.ToObject<bool>());
            Assert.NotNull(change["warning"]);
        }

        // ── RenderCreateTable ─────────────────────────────────────────────────

        [Fact]
        public void RenderCreateTable_BuildsHeuristicDdl_WithPk()
        {
            var cols = new JArray
            {
                Col("Id", "NUMERIC", 8, 0, nullable: false, isKey: true),
                Col("Name", "CHARACTER", 40, 0, nullable: true)
            };
            string ddl = ReorgImpactService.RenderCreateTable("Customer", cols);
            Assert.NotNull(ddl);
            Assert.Contains("CREATE TABLE [Customer]", ddl);
            Assert.Contains("[Id] NUMERIC(8) NOT NULL", ddl);
            Assert.Contains("[Name] CHARACTER(40) NULL", ddl);
            Assert.Contains("PRIMARY KEY ([Id])", ddl);
        }

        [Fact]
        public void RenderCreateTable_EscapesClosingBracketsInIdentifiers()
        {
            string ddl = ReorgImpactService.RenderCreateTable("Customer]Archive", new JArray
            {
                Col("Id]Legacy", "NUMERIC", 8, 0, nullable: false, isKey: true)
            });

            Assert.Contains("CREATE TABLE [Customer]]Archive]", ddl);
            Assert.Contains("[Id]]Legacy] NUMERIC(8) NOT NULL", ddl);
        }

        [Fact]
        public void RenderCreateTable_EmptyOrNull_ReturnsNull()
        {
            Assert.Null(ReorgImpactService.RenderCreateTable("", new JArray()));
            Assert.Null(ReorgImpactService.RenderCreateTable("T", null));
        }

        // ── Guard: no KB open → NoKbOpen envelope ─────────────────────────────

        [Fact]
        public void Preview_NoKb_ReturnsNoKbOpen()
        {
            var svc = new ReorgImpactService(null, null);
            var jo = JObject.Parse(svc.Preview(JObject.Parse("{\"name\":\"Customer\"}")));
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }
    }
}
