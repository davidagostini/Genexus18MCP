using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolSchemaSizeTests
    {
        private static string FindToolDefinitionsJson()
        {
            // Preferred: alongside the test output (propagated via Gateway's <Content> item).
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            if (File.Exists(beside)) return beside;

            // Fallback: walk up from base dir to repo src (for IDE test runs from src tree).
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("Could not locate tool_definitions.json from test base " + AppContext.BaseDirectory);
        }

        [Fact]
        public void TotalToolSchemaSizeIsUnderBudget()
        {
            var path = FindToolDefinitionsJson();
            Assert.True(File.Exists(path), $"tool_definitions.json not found at {path}");
            var content = File.ReadAllText(path);
            var approxTokens = content.Length / 4;
            // This budget guards the combined size of every tool schema in
            // tool_definitions.json (approximated as content.Length / 4 tokens). MCP
            // clients pay this cost on every session's tool list, so growth here is
            // deliberate — bump the constant only alongside a schema change that needs
            // the extra room, and check headroom before adding a new field to an
            // existing tool. Full bump-by-bump history lives in CHANGELOG.md; only the
            // last few entries are kept here for quick context:
            //   2026-07-09 (genexus_merge spike): 11750 → 12200 for the new
            //   genexus_merge tool (IMergeService object-merge, WRITE +
            //   destructiveHint=true). Measured ~12084 tokens; ~116 headroom.
            //   2026-07-09 (SDK-coverage batch integration): 12200 → 13300 for
            //   genexus_kb_version, genexus_module, genexus_gam, plus genexus_gxserver
            //   write actions landed together. Measured ~13150 tokens; ~150 headroom.
            //   2026-07-10 (issue #28 authoring papercuts): 13300 → 13600 for
            //   genexus_variable length/decimals/collection params + genexus_create
            //   firstItem/firstItemType SDT-seed params. Measured ~13378 tokens;
            //   ~222 headroom.
            //   2026-07-14 (issue #32): 13600 → 14100 for genexus_variable batch
            //   `variables[]` add + typeName/VarChar docs and genexus_gxserver commit
            //   `targets[]` (partial commit). Measured ~13856 tokens; ~244 headroom.
            //   2026-07-15 (per-KB memory): 14100 → 14550 for the new genexus_memory
            //   tool (save/recall/list/forget per-KB fact store). Measured ~14333
            //   tokens; ~217 headroom.
            //   2026-07-15 (per-KB memory, Phase 3): added consolidate/promote actions
            //   + message/dryRun params to genexus_memory. Measured ~14469 tokens;
            //   ~81 headroom — still under the 14550 budget, no bump needed.
            //   2026-07-20 (compile_check): 14550 → 14750 for the genexus_lifecycle
            //   `mode` param (compile_check) + discoverability copy in the tool
            //   description and an example. Measured ~14693 tokens; ~57 headroom.
            //   2026-07-20 (issue #39 create_index): 14750 → 14900 for the
            //   genexus_structure `create_index` action (enum value + payload docs +
            //   example) — the GeneXus-parity way to enforce attribute uniqueness.
            //   Measured ~14823 tokens; ~77 headroom.
            //   2026-07-20 (issue #39 data-model batch): 14900 → 15100 for the
            //   genexus_structure drop_index / set_attribute / set_level / set_domain
            //   actions (enum values + expanded payload docs + examples). Measured
            //   ~15008 tokens; ~92 headroom.
            //   2026-07-20 (issue #39 batch 2 authoring): 15100 → 15400 for the new
            //   genexus_authoring tool (add_external_method / add_external_property /
            //   add_menu_option). Measured ~15300 tokens; ~100 headroom.
            //   2026-07-20 (issue #39 batch 3): 15400 → 15600 for genexus_authoring
            //   add_condition (Data Selector filter). (add_theme_color was prototyped
            //   then dropped — classic Theme colors are a virtual-part projection,
            //   IDE-only, like SDPanel parts.) Measured ~15330 tokens; ~270 headroom.
            //   2026-07-20 (SDK-endpoints P0/P1 batch): 15600 → 16200 for the new
            //   genexus_transfer (XPZ export/import) + genexus_deploy tools, plus
            //   genexus_analyze mode=kb_stats, genexus_db action=reorg_impact,
            //   genexus_security action=scan_native, and genexus_gxserver
            //   pipeline_* actions/params. Measured ~16049 tokens; ~151 headroom.
            //   2026-07-20 (reliability batch): 16200 → 16400 for genexus_lifecycle's
            //   new compile_check `callers`/`callerCap` (target-only scoping) and build
            //   `deploy` (full deploy → runnable output) params. Measured ~16254; ~146 headroom.
            //   2026-07-24 (issue #50): 16400 → 16600 for genexus_create's new
            //   folder/module/parentPath destination args (rejected with
            //   FolderPlacementUnsupported). Measured ~16489; ~111 headroom.
            //   2026-07-24 (issue #50 rework — real move): 16600 → 16700 for
            //   genexus_properties action=move (destination/destKind/dryRun params)
            //   + reworked genexus_create folder/module copy (now creates-then-moves
            //   instead of rejecting). Measured ~16629; ~71 headroom.
            //   2026-07-24 (issue #52): 16700 → 16900 for genexus_structure update_visual
            //   SDT support (description + payload docs for isCollection/collectionItemName/
            //   basedOnDomain + an SDT example). Measured ~16760; ~140 headroom.
            Assert.True(approxTokens < 16900, $"tool_definitions.json is ~{approxTokens} tokens; budget 16900.");
        }
    }
}
