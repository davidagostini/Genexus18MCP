using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class RouterContractCoverageTests
    {
        [Theory]
        [InlineData("genexus_delete_object", "{}", "Object", "Delete")]
        [InlineData("genexus_worker_reload", "{}", "Object", "WorkerReload")]
        [InlineData("genexus_validate_payload", "{}", "Write", "ValidatePayload")]
        [InlineData("genexus_bulk_edit", "{}", "Write", "Bulk")]
        [InlineData("genexus_sdk_probe", "{}", "SdkProbe", "Run")]
        [InlineData("genexus_format", "{}", "Formatting", "Format")]
        [InlineData("genexus_security", "{}", "Security", "audit_gam")]
        [InlineData("genexus_edit_form", "{action:' insert '}", "WebFormEdit", "insert")]
        [InlineData("genexus_run_object", "{}", "RunObject", "Resolve")]
        [InlineData("genexus_explain", "{}", "Explain", "Explain")]
        [InlineData("genexus_kb_readme", "{}", "KbReadme", "Generate")]
        [InlineData("genexus_pr_description", "{}", "PrDescription", "Generate")]
        [InlineData("genexus_multi_agent_lock", "{}", "MultiAgentLock", "status")]
        [InlineData("genexus_memory", "{}", "Memory", "recall")]
        [InlineData("genexus_what_if", "{}", "WhatIf", "Simulate")]
        [InlineData("genexus_doctor", "{}", "Doctor", "Diagnose")]
        [InlineData("genexus_tutorial", "{}", "Tutorial", "Step")]
        [InlineData("genexus_playbook", "{}", "Playbook", "Read")]
        [InlineData("genexus_gxserver", "{}", "GxServer", "Run")]
        [InlineData("genexus_compare", "{}", "Compare", "Run")]
        [InlineData("genexus_module", "{}", "Module", "Run")]
        [InlineData("genexus_gam", "{}", "Gam", "Run")]
        [InlineData("genexus_merge", "{}", "Merge", "Run")]
        [InlineData("genexus_kb_version", "{}", "KbVersion", "Run")]
        [InlineData("genexus_transfer", "{}", "Transfer", "Run")]
        [InlineData("genexus_deploy", "{}", "Deploy", "Run")]
        [InlineData("genexus_github", "{}", "Github", "CreatePr")]
        [InlineData("genexus_ai_complete", "{}", "AiComplete", "Complete")]
        [InlineData("genexus_voice", "{}", "Voice", "Intent")]
        [InlineData("genexus_auto_test", "{}", "AutoTest", "Generate")]
        [InlineData("genexus_reverse_pattern", "{}", "ReversePattern", "Infer")]
        [InlineData("genexus_orient", "{}", "Orient", "Welcome")]
        [InlineData("genexus_api", "{}", "Api", "list")]
        [InlineData("genexus_wwp", "{}", "WwpAction", "Run")]
        public void Operations_direct_routes_match_contract(string tool, string json, string module, string action)
        {
            AssertRoute(new OperationsRouter().ConvertToolCall(tool, JObject.Parse(json)), module, action);
        }

        [Theory]
        [InlineData("genexus_create", "object", "Object", "Create")]
        [InlineData("genexus_create", "object_atomic", "AtomicCreate", "Run")]
        [InlineData("genexus_create", "popup", "Popup", "Create")]
        [InlineData("genexus_create", "sd_panel_create", "SdPanel", "create")]
        [InlineData("genexus_create", "sd_panel_inspect", "SdPanel", "inspect")]
        [InlineData("genexus_create", "sd_panel_edit", "SdPanel", "edit")]
        [InlineData("genexus_create", "save_as", "Object", "SaveAs")]
        [InlineData("genexus_create", "scaffold", "Forge", "Scaffold")]
        [InlineData("genexus_create", "translate", "Conversion", "TranslateTo")]
        [InlineData("genexus_create", "sample", "Pattern", "GetSample")]
        [InlineData("genexus_create", "template", "Write", "ApplyTemplate")]
        [InlineData("genexus_create", "curl_procedure", "CurlProc", "Run")]
        [InlineData("genexus_telemetry", "logs", "Object", "ReadLogs")]
        [InlineData("genexus_telemetry", "friction_append", "FrictionLog", "Append")]
        [InlineData("genexus_telemetry", "friction_tail", "FrictionLog", "Tail")]
        [InlineData("genexus_telemetry", "learning_report", "Learning", "Report")]
        [InlineData("genexus_telemetry", "profile_analyze", "Profile", "analyze")]
        [InlineData("genexus_telemetry", "profile_hotspots", "Profile", "hotspots")]
        [InlineData("genexus_telemetry", "profile_correlate", "Profile", "correlate")]
        [InlineData("genexus_io", "asset_find", "Asset", "Find")]
        [InlineData("genexus_io", "asset_read", "Asset", "Read")]
        [InlineData("genexus_io", "asset_write", "Asset", "Write")]
        [InlineData("genexus_io", "export_part", "Object", "ExportText")]
        [InlineData("genexus_io", "import_part", "Object", "ImportText")]
        [InlineData("genexus_io", "export_unified", "Export", "Unified")]
        [InlineData("genexus_io", "screenshot_publish", "ScreenshotPublish", "Publish")]
        [InlineData("genexus_io", "ocr", "Ocr", "Run")]
        [InlineData("genexus_versioning", "history_list", "History", "list")]
        [InlineData("genexus_versioning", "history_get", "History", "get_source")]
        [InlineData("genexus_versioning", "history_save", "History", "save")]
        [InlineData("genexus_versioning", "history_restore", "History", "restore")]
        [InlineData("genexus_versioning", "undo", "Undo", "Undo")]
        [InlineData("genexus_versioning", "time_travel", "TimeTravel", "Recover")]
        [InlineData("genexus_versioning", "blame", "Blame", "Get")]
        [InlineData("genexus_versioning", "diff", "Diff", "textVsText")]
        [InlineData("genexus_versioning", "diff_generated", "GeneratedDiff", "Diff")]
        [InlineData("genexus_db", "drift_check", "DbDrift", "Check")]
        [InlineData("genexus_db", "drift_report", "DbDrift", "Report")]
        [InlineData("genexus_db", "optimize_analyze", "DbOptimize", "Analyze")]
        [InlineData("genexus_db", "optimize_suggest", "DbOptimize", "SuggestIndexes")]
        [InlineData("genexus_db", "optimize_report", "DbOptimize", "Report")]
        [InlineData("genexus_db", "sql_ddl", "Analyze", "GetSQL")]
        [InlineData("genexus_db", "sql_navigation", "Analyze", "GetSqlForNavigation")]
        [InlineData("genexus_db", "sample_data", "Analyze", "GenerateSampleData")]
        [InlineData("genexus_db", "types_list", "types", "list")]
        [InlineData("genexus_db", "types_describe", "types", "describe")]
        [InlineData("genexus_db", "types_validate", "types", "validate_value")]
        [InlineData("genexus_db", "reorg_impact", "ReorgImpact", "Run")]
        [InlineData("genexus_db", "reorg_preview", "ReorgImpact", "Preview")]
        [InlineData("genexus_db", "translations_import", "Analyze", "TranslationsImport")]
        [InlineData("genexus_browser", "smoke", "smoke_test", "Run")]
        [InlineData("genexus_browser", "a11y", "a11y_audit", "Audit")]
        [InlineData("genexus_browser", "wcag", "WcagCheck", "Check")]
        [InlineData("genexus_browser", "capture", "browser_capture", "Capture")]
        [InlineData("genexus_browser", "cross", "CrossBrowser", "Run")]
        [InlineData("genexus_browser", "preview", "Preview", "Render")]
        public void Operations_umbrella_actions_match_contract(string tool, string actionName, string module, string action)
        {
            var args = new JObject { ["action"] = actionName, ["name"] = "SampleObject", ["type"] = "Procedure" };
            AssertRoute(new OperationsRouter().ConvertToolCall(tool, args), module, action);
        }

        [Theory]
        [InlineData("linter", "Linter", "linter")]
        [InlineData("navigation", "Analyze", "GetNavigation")]
        [InlineData("hierarchy", "Analyze", "GetHierarchy")]
        [InlineData("impact", "Analyze", "ImpactAnalysis")]
        [InlineData("data_context", "Analyze", "GetDataContext")]
        [InlineData("ui_context", "UI", "GetUIContext")]
        [InlineData("pattern_metadata", "Analyze", "GetPatternMetadata")]
        [InlineData("summary", "Analyze", "Summarize")]
        [InlineData("code_metrics", "Analyze", "GetCodeMetrics")]
        [InlineData("kb_stats", "KbStats", "Run")]
        [InlineData("table_relations", "TableRelations", "Run")]
        [InlineData("explain", "Analyze", "ExplainCode")]
        [InlineData("callers", "Analyze", "FindCallerSites")]
        [InlineData("event_flow", "Analyze", "GetEventFlow")]
        [InlineData("dependency_heatmap", "Analyze", "DependencyHeatmap")]
        [InlineData("cross_platform_impact", "Analyze", "CrossPlatformImpact")]
        [InlineData("parent_context", "Analyze", "ParentContext")]
        [InlineData("unknown", "Analyze", "Analyze")]
        public void Analyze_modes_match_contract(string mode, string module, string action)
        {
            var args = new JObject { ["mode"] = mode, ["name"] = "SampleObject", ["type"] = "Procedure" };
            AssertRoute(new AnalyzeRouter().ConvertToolCall("genexus_analyze", args), module, action);
        }

        [Theory]
        [InlineData("genexus_inspect", "Analyze", "GetConversionContext")]
        [InlineData("genexus_inject_context", "Analyze", "InjectContext")]
        [InlineData("genexus_get_signature", "Analyze", "GetParameters")]
        [InlineData("genexus_linter", "Linter", "linter")]
        [InlineData("genexus_get_navigation", "Analyze", "GetNavigation")]
        public void Analyze_direct_routes_match_contract(string tool, string module, string action)
        {
            AssertRoute(new AnalyzeRouter().ConvertToolCall(tool, new JObject()), module, action);
        }

        [Theory]
        [InlineData("build", "Build", "Build")]
        [InlineData("cancel", "Build", "Cancel")]
        [InlineData("specify", "Build", "Specify")]
        [InlineData("rebuild", "Build", "RebuildAll")]
        [InlineData("reorg", "Build", "Reorg")]
        [InlineData("reorg_preview", "Build", "ReorgPreview")]
        [InlineData("validate", "Validation", "Check")]
        [InlineData("validate-kb", "KB", "ValidateConditions")]
        [InlineData("snapshots-list", "KB", "ListPatternSnapshots")]
        [InlineData("snapshots-restore", "KB", "RestorePatternSnapshot")]
        [InlineData("sync", "Build", "Sync")]
        [InlineData("index", "KB", "BulkIndex")]
        public void Lifecycle_actions_match_contract(string lifecycleAction, string module, string action)
        {
            var args = new JObject { ["action"] = lifecycleAction, ["target"] = "SampleObject" };
            AssertRoute(new SystemRouter().ConvertToolCall("genexus_lifecycle", args), module, action);
        }

        [Fact]
        public void Lifecycle_status_result_and_compile_check_cover_special_contracts()
        {
            var router = new SystemRouter();
            AssertRoute(router.ConvertToolCall("genexus_lifecycle", JObject.Parse("{action:'build',mode:'compile_check'}")), "Build", "CompileCheck");
            AssertRoute(router.ConvertToolCall("genexus_lifecycle", JObject.Parse("{action:'status',target:'job',wait:999}")), "Build", "Status");
            AssertRoute(router.ConvertToolCall("genexus_lifecycle", JObject.Parse("{action:'status',wait:-1}")), "KB", "GetIndexStatus");
            AssertRoute(router.ConvertToolCall("genexus_lifecycle", JObject.Parse("{action:'result',target:'job'}")), "Build", "Result");
            Assert.Null(router.ConvertToolCall("genexus_lifecycle", JObject.Parse("{action:'result'}")));
        }

        [Theory]
        [InlineData("genexus_doc", "{action:'wiki'}", "Wiki", "Generate")]
        [InlineData("genexus_doc", "{action:'visualize'}", "Visualizer", "Generate")]
        [InlineData("genexus_doc", "{action:'health'}", "Health", "GetReport")]
        [InlineData("genexus_test", "{}", "Test", "Run")]
        [InlineData("genexus_kb", "{action:'set_startup'}", "KB", "SetStartupObject")]
        [InlineData("genexus_kb", "{action:'get_startup'}", "KB", "GetStartupObject")]
        [InlineData("genexus_kb", "{action:'get_environment'}", "KB", "GetActiveEnvironment")]
        [InlineData("genexus_kb", "{action:'set_environment',environment:'development'}", "KB", "SetActiveEnvironment")]
        [InlineData("genexus_kb_explorer", "{}", "KbExplorer", "Locate")]
        [InlineData("genexus_navigation", "{}", "Navigation", "View")]
        [InlineData("genexus_build_plan", "{}", "BuildPlan", "Generate")]
        [InlineData("genexus_validate", "{}", "Validation", "Check")]
        [InlineData("genexus_build", "{action:'Build'}", "Build", "Build")]
        public void System_direct_routes_match_contract(string tool, string json, string module, string action)
        {
            AssertRoute(new SystemRouter().ConvertToolCall(tool, JObject.Parse(json)), module, action);
        }

        [Fact]
        public void Invalid_routes_are_explicit_or_null()
        {
            var operations = new OperationsRouter();
            AssertRoute(operations.ConvertToolCall("genexus_create", new JObject()), "Error", "InvalidAction");
            AssertRoute(operations.ConvertToolCall("genexus_telemetry", new JObject()), "Error", "InvalidAction");
            AssertRoute(operations.ConvertToolCall("genexus_io", new JObject()), "Error", "InvalidAction");
            AssertRoute(operations.ConvertToolCall("genexus_versioning", new JObject()), "Error", "InvalidAction");
            AssertRoute(operations.ConvertToolCall("genexus_db", new JObject()), "Error", "InvalidAction");
            AssertRoute(operations.ConvertToolCall("genexus_browser", new JObject()), "Error", "InvalidAction");
            Assert.Null(operations.ConvertToolCall("unknown", new JObject()));
            Assert.Null(new AnalyzeRouter().ConvertToolCall("unknown", new JObject()));
            Assert.Null(new SystemRouter().ConvertToolCall("unknown", new JObject()));
        }

        private static void AssertRoute(object? result, string module, string action)
        {
            Assert.NotNull(result);
            var routed = JObject.FromObject(result!);
            Assert.Equal(module, (string?)routed["module"]);
            Assert.Equal(action, (string?)routed["action"]);
        }
    }
}
