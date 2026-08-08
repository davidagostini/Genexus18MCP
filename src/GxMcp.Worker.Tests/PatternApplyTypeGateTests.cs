using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Item #10 (v2.6.4): apply_pattern parent-type gate. Original bug — applying
    // WWP on a WebPanel created the host but bound it as a Transaction, causing
    // IDE errors. The gate rejects non-eligible types upfront and validates
    // settings.template against the available list before any SDK churn.
    // TryBuildTypeGateRejection is the pure helper; covered here in isolation.
    public class PatternApplyTypeGateTests
    {
        [Theory]
        [InlineData("WorkWithPlus")]
        [InlineData("WWP")]
        [InlineData("workwithplus")]
        [InlineData("wwp")]
        public void Transaction_AnyCase_NoRejection(string key)
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "Customer", patternKey: key, parentType: "Transaction",
                callerTemplate: null, availableTemplates: null);
            Assert.Null(r);
        }

        [Theory]
        [InlineData("WebPanel")]
        [InlineData("WebComponent")]
        [InlineData("SDPanel")]
        public void WebPanelKind_NoTemplate_NoRejection(string parentType)
        {
            // Without callerTemplate, the gate doesn't reject — the SDK
            // auto-discovers a template downstream.
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel", patternKey: "WorkWithPlus", parentType: parentType,
                callerTemplate: null, availableTemplates: new List<string> { "MatIsoTemplate" });
            Assert.Null(r);
        }

        [Theory]
        [InlineData("Procedure")]
        [InlineData("SDT")]
        [InlineData("Domain")]
        [InlineData("DataProvider")]
        [InlineData("WorkflowDiagram")]
        public void NonEligibleType_Rejected_WithValidParentTypesAndHint(string parentType)
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "Foo", patternKey: "WorkWithPlus", parentType: parentType,
                callerTemplate: null, availableTemplates: null);
            Assert.NotNull(r);
            var env = JObject.Parse(r);
            Assert.Equal("error", env["status"]!.ToString());
            Assert.Equal(parentType, env["parentType"]!.ToString());
            Assert.Equal("Foo", env["target"]!.ToString());
            Assert.NotNull(env["validParentTypes"]);
            var valid = (JArray)env["validParentTypes"]!;
            Assert.Contains("Transaction", valid);
            Assert.Contains("WebPanel", valid);
            Assert.Contains("WebComponent", valid);
            Assert.Contains("SDPanel", valid);
            Assert.NotNull(env["error"]?["hint"]);
        }

        [Fact]
        public void WebPanel_BadTemplate_RejectedWithAvailableTemplates()
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel",
                patternKey: "WorkWithPlus",
                parentType: "WebPanel",
                callerTemplate: "NotInCatalog",
                availableTemplates: new List<string> { "MatIsoTemplate", "TransactionResp2", "PopoverEmpty" });

            Assert.NotNull(r);
            var env = JObject.Parse(r);
            Assert.Equal("error", env["status"]!.ToString());
            Assert.Contains("NotInCatalog", env["error"]?["message"]!.ToString());
            var available = (JArray)env["availableTemplates"]!;
            Assert.Equal(3, available.Count);
            Assert.Contains("MatIsoTemplate", available);
        }

        [Fact]
        public void WebPanel_TemplateCaseInsensitiveMatch_NoRejection()
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel",
                patternKey: "WorkWithPlus",
                parentType: "WebPanel",
                callerTemplate: "matisotemplate",
                availableTemplates: new List<string> { "MatIsoTemplate" });
            Assert.Null(r);
        }

        [Fact]
        public void WebPanel_GoodTemplate_NoRejection()
        {
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel",
                patternKey: "WorkWithPlus",
                parentType: "WebPanel",
                callerTemplate: "MatIsoTemplate",
                availableTemplates: new List<string> { "MatIsoTemplate", "PopoverEmpty" });
            Assert.Null(r);
        }

        [Theory]
        [InlineData("WebPanel", "webpanel-direct-attach")]
        [InlineData("WebComponent", "webcomponent-direct-attach")]
        [InlineData("SDPanel", "sdpanel-direct-attach")]
        [InlineData("Transaction", "transaction-family")]
        [InlineData("Procedure", "unknown")]
        public void BindingMode_ReflectsParentLifecycle(string parentType, string expected)
        {
            Assert.Equal(expected, PatternApplyService.GetWwpBindingMode(parentType));
        }

        [Fact]
        public void NonWwpPattern_NoRejection_RegardlessOfType()
        {
            // The gate only fires for WorkWithPlus / WWP keys. Other patterns
            // pass through with no opinion on parent type.
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "Foo", patternKey: "SomeFuturePattern", parentType: "Procedure",
                callerTemplate: null, availableTemplates: null);
            Assert.Null(r);
        }

        [Fact]
        public void NullParentType_TreatedAsIneligible_Rejected()
        {
            // Defensive: TypeDescriptor.Name can be null/empty on edge cases —
            // the gate must still produce a clear rejection rather than crash.
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "Foo", patternKey: "WorkWithPlus", parentType: null,
                callerTemplate: null, availableTemplates: null);
            Assert.NotNull(r);
            var env = JObject.Parse(r);
            Assert.Equal("error", env["status"]!.ToString());
        }

        [Fact]
        public void WebPanel_EmptyAvailableList_NoTemplateCheck()
        {
            // If template discovery returned an empty list (KB hasn't loaded
            // pattern templates yet), don't pre-emptively reject the caller's
            // template — the downstream SDK call will give a better error.
            string r = PatternApplyService.TryBuildTypeGateRejection(
                objName: "MyPanel",
                patternKey: "WorkWithPlus",
                parentType: "WebPanel",
                callerTemplate: "Anything",
                availableTemplates: new List<string>());
            Assert.Null(r);
        }
    }
}
