using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Item #12 (v2.6.4): genexus_recipe tool dispatches to RecipeCatalog.
    // RecipeCatalog is the routing layer the LLM hits when whoami's playbooks
    // hint points it here, so the contract (list, known, unknown, missing-name)
    // is regression-critical.
    public class RecipeCatalogTests
    {
        [Theory]
        [InlineData("list")]
        [InlineData("index")]
        [InlineData("LIST")]
        public void Get_List_ReturnsRecipeNamesArray(string name)
        {
            var r = RecipeCatalog.Get(name!);
            Assert.NotNull(r["recipes"]);
            var arr = (JArray)r["recipes"]!;
            Assert.True(arr.Count >= 5, $"expected ≥5 recipes, got {arr.Count}");
        }

        [Theory]
        [InlineData("wwp_on_transaction")]
        [InlineData("wwp_on_webpanel")]
        [InlineData("create_popup")]
        [InlineData("edit_pattern_instance")]
        [InlineData("add_custom_button")]
        [InlineData("popup_blocking_with_reload")]
        [InlineData("radio_group_show_hide")]
        [InlineData("extract_to_procedure")]
        [InlineData("feature_scaffold")]
        public void Get_KnownRecipe_ReturnsGoalStepsPitfalls(string recipeName)
        {
            var r = RecipeCatalog.Get(recipeName);
            Assert.False(r.ContainsKey("error"), $"recipe '{recipeName}' should not be an error envelope");
            Assert.NotNull(r["goal"]);
            Assert.IsType<JArray>(r["steps"]);
            Assert.IsType<JArray>(r["pitfalls"]);
            Assert.True(((JArray)r["steps"]!).Count >= 1, "recipe must have at least one step");
        }

        [Fact]
        public void Get_KnownRecipe_StepShapeIsToolArgsWhy()
        {
            var r = RecipeCatalog.Get("wwp_on_webpanel");
            var steps = (JArray)r["steps"]!;
            foreach (var step in steps)
            {
                Assert.NotNull(step["tool"]);
                Assert.NotNull(step["args"]);
                Assert.NotNull(step["why"]);
            }
        }

        [Fact]
        public void Get_WwpOnWebpanel_StepsMentionInspectFirst()
        {
            // Regression for the original bug: the recipe must tell the LLM to
            // run genexus_inspect BEFORE apply_pattern to confirm parentType.
            var r = RecipeCatalog.Get("wwp_on_webpanel");
            var raw = r.ToString(Newtonsoft.Json.Formatting.None);
            Assert.Contains("genexus_inspect", raw);
            Assert.Contains("CHECK PARENT TYPE", raw, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Get_UnknownRecipe_ReturnsErrorWithAvailableList()
        {
            var r = RecipeCatalog.Get("nope_no_such_recipe");
            Assert.NotNull(r["error"]);
            Assert.NotNull(r["availableRecipes"]);
            Assert.IsType<JArray>(r["availableRecipes"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Get_EmptyName_ReturnsErrorEnvelope(string? name)
        {
            var r = RecipeCatalog.Get(name!);
            Assert.NotNull(r["error"]);
            Assert.NotNull(r["hint"]);
        }

        [Fact]
        public void Get_RecipeNameIsCaseInsensitive()
        {
            var lower = RecipeCatalog.Get("wwp_on_webpanel");
            var upper = RecipeCatalog.Get("WWP_ON_WEBPANEL");
            // Same shape (goal, steps, pitfalls) — exact content equality is
            // overkill; presence of the same top-level keys is enough.
            Assert.True(lower.ContainsKey("goal") && upper.ContainsKey("goal"));
        }

        // ------------------------------------------------------------------
        // Item 60 — versioned recipes
        // ------------------------------------------------------------------

        [Fact]
        public void Get_KnownRecipe_HasVersionField()
        {
            var r = RecipeCatalog.Get("wwp_on_webpanel");
            Assert.NotNull(r["version"]);
            Assert.Equal("v1", r["version"]?.ToString());
        }

        [Fact]
        public void Get_PinnedVersion_ReturnsThatVersion()
        {
            var r = RecipeCatalog.Get("wwp_on_webpanel@v1");
            Assert.False(r.ContainsKey("error"));
            Assert.Equal("v1", r["version"]?.ToString());
            Assert.NotNull(r["goal"]);
        }

        [Fact]
        public void Get_PinnedUnknownVersion_ReturnsErrorEnvelope()
        {
            var r = RecipeCatalog.Get("wwp_on_webpanel@v99");
            Assert.NotNull(r["error"]);
            Assert.Contains("v99", r["error"]?.ToString());
        }

        [Fact]
        public void Get_List_IncludesAvailableVersionsArray()
        {
            var r = RecipeCatalog.Get("list");
            var arr = (JArray)r["recipes"]!;
            Assert.NotEmpty(arr);
            foreach (var entry in arr)
            {
                var versions = entry["availableVersions"] as JArray;
                Assert.NotNull(versions);
                Assert.NotEmpty(versions!);
                Assert.NotNull(entry["latestVersion"]);
            }
        }

        [Fact]
        public void Get_BareName_ResolvesToLatestVersion()
        {
            // With only v1 in the catalog today, bare name resolves to v1.
            var bare = RecipeCatalog.Get("create_popup");
            Assert.Equal("v1", bare["version"]?.ToString());
        }
    }
}
