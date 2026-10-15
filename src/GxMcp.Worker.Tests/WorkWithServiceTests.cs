using GxMcp.Worker;
using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WorkWithServiceTests
    {
        private const string SampleWorkWithXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkWithInstance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level>
    <view>
      <tabs>
        <tab code=""General"" name=""General"" type=""Tabular"">
          <actions>
            <action name=""Update"" caption=""Update"" />
            <action name=""Delete"" caption=""Delete"" />
          </actions>
        </tab>
      </tabs>
    </view>
  </level>
</WorkWithInstance>";

        private const string SampleWorkWithXmlNoActions = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkWithInstance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level>
    <view>
      <tabs>
        <tab code=""General"" name=""General"" type=""Tabular"" />
      </tabs>
    </view>
  </level>
</WorkWithInstance>";

        private const string SampleWorkWithXmlWithDefaultsAndNeighbors = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkWithInstance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level>
    <selection page=""&lt;default&gt;"">
      <actions>
        <action name=""Export"" caption=""Export"" />
      </actions>
    </selection>
    <view>
      <tabs>
        <tab code=""General"" name=""General"" type=""Tabular"">
          <actions>
            <action name=""Update"" caption=""Update"" />
            <action name=""Delete"" caption=""Delete"" />
          </actions>
        </tab>
        <tab code=""Orders"" name=""Orders"" type=""Grid"" page=""&lt;default&gt;"">
          <actions>
            <action name=""ViewOrder"" caption=""View Order"" />
          </actions>
        </tab>
      </tabs>
    </view>
  </level>
</WorkWithInstance>";

        private static JArray RemovalTabs() => JArray.Parse(@"[
            {'code':'General','name':'General','actions':[{'name':'Keep','caption':'Keep'},{'name':'Remove','caption':'Remove'},{'name':'Neighbor','parameters':['Id']}]},
            {'code':'Detail','name':'Detail','actions':[{'name':'Remove','caption':'Different tab'}]}]");

        [Fact]
        public void RemovalPlan_DeletesOnlyExactTabActionAndLeavesInputIntact()
        {
            var before = RemovalTabs();
            var expected = WorkWithSdkAdapter.PlanActionRemoval(before, "general", "remove");
            Assert.Equal(3, ((JArray)before[0]["actions"]).Count);
            Assert.Equal(2, ((JArray)expected[0]["actions"]).Count);
            Assert.Equal("Keep", expected[0]["actions"][0]["name"].ToString());
            Assert.Equal("Neighbor", expected[0]["actions"][1]["name"].ToString());
            Assert.True(JToken.DeepEquals(before[1], expected[1]));
        }

        [Theory]
        [InlineData("Missing", "Remove", "WorkWithTabAmbiguous")]
        [InlineData("General", "Missing", "WorkWithActionAmbiguous")]
        public void RemovalPlan_RefusesUnresolvedSelector(string tab, string action, string code)
        {
            var error = Assert.Throws<UsageException>(() => WorkWithSdkAdapter.PlanActionRemoval(RemovalTabs(), tab, action));
            Assert.Equal(code, error.Code);
        }

        [Fact]
        public void RemovalPlan_RefusesDuplicateTabsOrActions()
        {
            var before = RemovalTabs();
            before.Add(before[0].DeepClone());
            Assert.Throws<UsageException>(() => WorkWithSdkAdapter.PlanActionRemoval(before, "General", "Remove"));
            before = RemovalTabs();
            ((JArray)before[0]["actions"]).Add(before[0]["actions"][1].DeepClone());
            Assert.Throws<UsageException>(() => WorkWithSdkAdapter.PlanActionRemoval(before, "General", "Remove"));
        }

        public sealed class NativeActionFixture { public string Name { get; set; } }
        public sealed class NativeTabFixture
        {
            public string Code { get; set; }
            public string Name { get; set; }
            public List<NativeActionFixture> Actions { get; } = new List<NativeActionFixture>();
        }
        public sealed class NativeViewFixture { public List<NativeTabFixture> Tabs { get; } = new List<NativeTabFixture>(); }
        public sealed class NativeLevelFixture { public NativeViewFixture View { get; } = new NativeViewFixture(); }
        public sealed class NativeModelFixture { public List<NativeLevelFixture> Levels { get; } = new List<NativeLevelFixture>(); }

        [Fact]
        public void NativeRemoval_InvokesTypedCollectionRemoveAndPreservesNeighbors()
        {
            var model = new NativeModelFixture();
            var level = new NativeLevelFixture();
            model.Levels.Add(level);
            var general = new NativeTabFixture { Code = "General" };
            general.Actions.AddRange(new[] { new NativeActionFixture { Name = "Keep" }, new NativeActionFixture { Name = "Remove" } });
            var detail = new NativeTabFixture { Code = "Detail" };
            detail.Actions.Add(new NativeActionFixture { Name = "Remove" });
            level.View.Tabs.AddRange(new[] { general, detail });
            WorkWithSdkAdapter.RemoveNativeAction(model, "general", "remove");
            Assert.Single(general.Actions);
            Assert.Equal("Keep", general.Actions[0].Name);
            Assert.Single(detail.Actions);
            Assert.Equal("Remove", detail.Actions[0].Name);
        }

        [Fact]
        public void RemovalRoute_RequiresConcurrencyAndFreshFullProjectionVerification()
        {
            var source = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WorkWithService.cs");
            Assert.Contains("BaseVersionRequired", source);
            Assert.Contains("FindObjectFresh(instance.Name, guid: instance.Guid.ToString())", source);
            Assert.Contains("PlanActionRemoval(before, tab, action)", source);
            Assert.Contains("PatternSnapshotStore.SaveSnapshot", source);
            Assert.Contains("JToken.DeepEquals(expected, after)", source);
            Assert.Contains("SaveNativeModel(originalModel, current)", source);
            Assert.Contains("JToken.DeepEquals(before, restoredTabs)", source);
            Assert.Contains("RollbackConcurrencyConflict", source);
            int transaction = source.IndexOf("using (var transaction = instance.Model.KB.BeginTransaction())", StringComparison.Ordinal);
            int check = source.IndexOf("string preSaveXml", transaction, StringComparison.Ordinal);
            int save = source.IndexOf("SaveNativeModel(editedModel, instance)", transaction, StringComparison.Ordinal);
            Assert.True(transaction >= 0 && check > transaction && save > check);
            var adapter = GxMcp.TestSupport.RepoSource.WithoutComments("src", "GxMcp.Worker", "Compatibility", "WorkWithSdkAdapter.cs");
            Assert.Contains("using (var isolation = new PatternSaveIsolationScope(instance))", adapter);
        }

        [Theory]
        [InlineData(6, 0, false)]
        [InlineData(6, 6, true)]
        [InlineData(0, 1, false)]
        public void KnownActionArity_RejectsMismatchBeforeAnySave(int expected, int supplied, bool matches)
        {
            var advisory = new JObject { ["expectedParameterCount"] = expected, ["suppliedParameterCount"] = supplied, ["arityMatches"] = expected == supplied, ["warning"] = "arity mismatch" };
            var plan = new JObject { ["signatureAdvisory"] = advisory };
            Assert.Equal(matches, WorkWithSdkAdapter.ValidateActionArity(plan, out string code, out string message));
            Assert.Equal(expected, plan["signatureAdvisory"]["expectedParameterCount"].Value<int>());
            Assert.Equal(supplied, plan["signatureAdvisory"]["suppliedParameterCount"].Value<int>());
            if (!matches)
            {
                Assert.Equal("ActionParameterArityMismatch", code);
                Assert.Equal("arity mismatch", message);
                Assert.False(plan["valid"].Value<bool>());
            }
        }

        [Fact]
        public void UnknownActionArity_DoesNotInventAValidationFailure()
        {
            Assert.True(WorkWithSdkAdapter.ValidateActionArity(new JObject(), out string code, out string message));
            Assert.Null(code);
            Assert.Null(message);
        }

        [Fact]
        public void TryAddActionToXml_AddsActionWithProcedureConditionAndParameters_Success()
        {
            var parameters = new List<string> { "&CustomerId", "'PDF'" };
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXml,
                tabCode: "General",
                actionName: "ExportReport",
                caption: "Export PDF",
                procObj: null,
                condition: "&IsAdmin = true",
                parameters: parameters,
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.True(added, errorMessage);
            Assert.Null(errorCode);
            Assert.NotNull(newXml);

            bool listed = WorkWithSdkAdapter.TryListActionsFromXml(newXml, out JArray tabs, out string listError);
            Assert.True(listed, listError);
            Assert.Single(tabs);

            var generalTab = (JObject)tabs[0];
            Assert.Equal("General", generalTab["code"]?.ToString());

            var actions = (JArray)generalTab["actions"];
            Assert.Equal(3, actions.Count);

            var exportAct = actions.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), "ExportReport", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(exportAct);
            Assert.Equal("Export PDF", exportAct["caption"]?.ToString());
            Assert.Equal("&IsAdmin = true", exportAct["condition"]?.ToString());

            var actParams = (JArray)exportAct["parameters"];
            Assert.Equal(2, actParams.Count);
            Assert.Equal("&CustomerId", actParams[0].ToString());
            Assert.Equal("'PDF'", actParams[1].ToString());
        }

        [Fact]
        public void TryAddActionToXml_DuplicateAction_FailsWithActionAlreadyExists()
        {
            bool addedFirst = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXml,
                tabCode: "General",
                actionName: "ExportReport",
                caption: "Export PDF",
                procObj: null,
                condition: null,
                parameters: null,
                out string firstXml,
                out string errCode1,
                out string errMsg1);

            Assert.True(addedFirst, errMsg1);

            bool addedSecond = WorkWithSdkAdapter.TryAddActionToXml(
                firstXml,
                tabCode: "General",
                actionName: "ExportReport",
                caption: "Another Export",
                procObj: null,
                condition: null,
                parameters: null,
                out string _,
                out string errCode2,
                out string errMsg2);

            Assert.False(addedSecond);
            Assert.Equal("ActionAlreadyExists", errCode2);
            Assert.Contains("already exists", errMsg2);
        }

        [Fact]
        public void TryAddActionToXml_TabNotFound_FailsWithTabNotFound()
        {
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXml,
                tabCode: "Invoices",
                actionName: "ExportReport",
                caption: "Export",
                procObj: null,
                condition: null,
                parameters: null,
                out string _,
                out string errCode,
                out string errMsg);

            Assert.False(added);
            Assert.Equal("TabNotFound", errCode);
            Assert.Contains("Invoices", errMsg);
        }

        [Fact]
        public void TryAddActionToXml_CreatesActionsContainerIfMissing()
        {
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXmlNoActions,
                tabCode: "General",
                actionName: "PrintInvoice",
                caption: "Print",
                procObj: null,
                condition: null,
                parameters: null,
                out string newXml,
                out string errCode,
                out string errMsg);

            Assert.True(added, errMsg);
            Assert.Null(errCode);

            bool listed = WorkWithSdkAdapter.TryListActionsFromXml(newXml, out JArray tabs, out string listErr);
            Assert.True(listed, listErr);
            var actions = (JArray)tabs[0]["actions"];
            Assert.Single(actions);
            Assert.Equal("PrintInvoice", actions[0]["name"]?.ToString());
        }

        [Fact]
        public void PatternRegistry_WorkWithPatternId_IsWorkWith()
        {
            Assert.Equal(new Guid("78cecefe-be7d-4980-86ce-8d6e91fba04b"), PatternRegistry.WorkWithPatternId);

            var wwManifest = new PatternManifest
            {
                Id = PatternRegistry.WorkWithPatternId,
                Name = "WorkWith",
                Publisher = "GeneXus"
            };
            Assert.True(wwManifest.IsWorkWith);
            Assert.False(wwManifest.IsWorkWithPlus);

            var wwpManifest = new PatternManifest
            {
                Id = PatternRegistry.WorkWithPlusPatternId,
                Name = "WorkWithPlus",
                Publisher = "DVelop"
            };
            Assert.True(wwpManifest.IsWorkWithPlus);
            Assert.False(wwManifest.IsWorkWithPlus);
        }

        [Fact]
        public void BuildActionPlan_IncludesValidationsAndExplicitValidationLimits()
        {
            var parameters = new List<string> { "procUrlTeste(\"General\")", "TestTrnEmpresaId" };
            var plan = WorkWithSdkAdapter.BuildActionPlan(
                instanceName: "WorkWithWebTestTrn",
                tabCode: "General",
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "TestTrnPropria",
                parameters: parameters);

            Assert.NotNull(plan);
            Assert.Equal("WorkWithWebTestTrn", plan["instance"]?.ToString());
            Assert.Equal("General", plan["tab"]?.ToString());
            Assert.Equal("ExecutaTeste", plan["actionName"]?.ToString());
            Assert.Equal("Executa Teste", plan["caption"]?.ToString());
            Assert.Equal("procExecutaTeste", plan["procedure"]?.ToString());
            Assert.Equal("TestTrnPropria", plan["condition"]?.ToString());

            var planParams = plan["parameters"] as JArray;
            Assert.NotNull(planParams);
            Assert.Equal(2, planParams.Count);
            Assert.Equal("procUrlTeste(\"General\")", planParams[0].ToString());
            Assert.Equal("TestTrnEmpresaId", planParams[1].ToString());

            // Issue #476: Plan must declare validations performed
            var validations = plan["validations"] as JObject;
            Assert.NotNull(validations);
            Assert.True(validations["targetObjectResolved"]?.ToObject<bool>());
            Assert.True(validations["workWithInstanceResolved"]?.ToObject<bool>());
            Assert.True(validations["tabExists"]?.ToObject<bool>());
            Assert.True(validations["actionNameFormatValid"]?.ToObject<bool>());
            Assert.True(validations["actionNameUniqueOnTab"]?.ToObject<bool>());
            Assert.True(validations["procedureResolved"]?.ToObject<bool>());
            Assert.True(validations["procedureTypeValid"]?.ToObject<bool>());

            // Issue #476: Plan must explicitly report validation limits (signature arity/types, expressions)
            var limits = plan["validationLimits"] as JObject;
            Assert.NotNull(limits);
            Assert.Contains("parm() rule", limits["signatureAndTypes"]?.ToString());
            Assert.Contains("not compiled", limits["conditionExpression"]?.ToString());
            Assert.Contains("not compiled", limits["parameterExpressions"]?.ToString());
        }

        [Fact]
        public void VerifyActionMatches_AllFieldsMatch_ReturnsTrue()
        {
            var observed = new JObject
            {
                ["name"] = "ExecutaTeste",
                ["caption"] = "Executa Teste",
                ["procedure"] = "procExecutaTeste",
                ["condition"] = "TestTrnPropria",
                ["parameters"] = new JArray("procUrlTeste(\"General\")", "TestTrnEmpresaId")
            };

            bool matches = WorkWithSdkAdapter.VerifyActionMatches(
                observed,
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "TestTrnPropria",
                parameters: new[] { "procUrlTeste(\"General\")", "TestTrnEmpresaId" },
                out string mismatchDetail);

            Assert.True(matches, mismatchDetail);
            Assert.Null(mismatchDetail);
        }

        [Fact]
        public void VerifyActionMatches_CaptionMismatch_ReturnsFalseWithDetail()
        {
            var observed = new JObject
            {
                ["name"] = "ExecutaTeste",
                ["caption"] = "Outro Caption",
                ["procedure"] = "procExecutaTeste",
                ["condition"] = "",
                ["parameters"] = new JArray()
            };

            bool matches = WorkWithSdkAdapter.VerifyActionMatches(
                observed,
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "",
                parameters: Array.Empty<string>(),
                out string mismatchDetail);

            Assert.False(matches);
            Assert.NotNull(mismatchDetail);
            Assert.Contains("Caption mismatch", mismatchDetail);
        }

        [Fact]
        public void VerifyActionMatches_ParameterOrderOrCountMismatch_ReturnsFalseWithDetail()
        {
            var observed = new JObject
            {
                ["name"] = "ExecutaTeste",
                ["caption"] = "Executa Teste",
                ["procedure"] = "procExecutaTeste",
                ["condition"] = "",
                ["parameters"] = new JArray("ParamB", "ParamA") // Swapped order
            };

            bool matches = WorkWithSdkAdapter.VerifyActionMatches(
                observed,
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "",
                parameters: new[] { "ParamA", "ParamB" },
                out string mismatchDetail);

            Assert.False(matches);
            Assert.NotNull(mismatchDetail);
            Assert.Contains("Parameters mismatch", mismatchDetail);
        }

        [Fact]
        public void VerifyActionMatches_ConditionMismatch_ReturnsFalseWithDetail()
        {
            var observed = new JObject
            {
                ["name"] = "ExecutaTeste",
                ["caption"] = "Executa Teste",
                ["procedure"] = "procExecutaTeste",
                ["condition"] = "&IsAdmin",
                ["parameters"] = new JArray()
            };

            bool matches = WorkWithSdkAdapter.VerifyActionMatches(
                observed,
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "&IsOwner",
                parameters: Array.Empty<string>(),
                out string mismatchDetail);

            Assert.False(matches);
            Assert.NotNull(mismatchDetail);
            Assert.Contains("Condition mismatch", mismatchDetail);
        }

        [Fact]
        public void TryAddActionToXml_ControlledFixtureWithDefaultsAndNeighborTabs_PreservesDefaultsAndNeighborStructure()
        {
            var parameters = new List<string> { "TestTrnId" };
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXmlWithDefaultsAndNeighbors,
                tabCode: "General",
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procObj: null,
                condition: "False",
                parameters: parameters,
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.True(added, errorMessage);
            Assert.Null(errorCode);
            Assert.NotNull(newXml);

            // Verify defaults are preserved verbatim on Selection and neighbor tab (not materialized to concrete 10)
            Assert.Contains("&lt;default&gt;", newXml);
            Assert.DoesNotContain("page=\"10\"", newXml);

            // Verify neighbor tab Orders and its action are preserved
            Assert.Contains(@"code=""Orders""", newXml);
            Assert.Contains(@"name=""ViewOrder""", newXml);

            // Verify list actions accurately parses both tabs
            bool listed = WorkWithSdkAdapter.TryListActionsFromXml(newXml, out JArray tabs, out string listError);
            Assert.True(listed, listError);
            Assert.Equal(2, tabs.Count);

            var generalTab = tabs.FirstOrDefault(t => string.Equals(t["code"]?.ToString(), "General")) as JObject;
            Assert.NotNull(generalTab);
            var generalActions = (JArray)generalTab["actions"];
            Assert.Equal(3, generalActions.Count);
            Assert.NotNull(generalActions.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), "ExecutaTeste")));

            var ordersTab = tabs.FirstOrDefault(t => string.Equals(t["code"]?.ToString(), "Orders")) as JObject;
            Assert.NotNull(ordersTab);
            var ordersActions = (JArray)ordersTab["actions"];
            Assert.Single(ordersActions);
            Assert.Equal("ViewOrder", ordersActions[0]["name"]?.ToString());
        }

        [Fact]
        public void TryAddActionToXml_PreservesRawWhitespaceWithoutReformattingUnmutatedElements()
        {
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXml,
                tabCode: "General",
                actionName: "CustomAction",
                caption: "Custom",
                procObj: null,
                condition: null,
                parameters: null,
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.True(added, errorMessage);
            Assert.Null(errorCode);

            // Existing tab element and original actions formatting are preserved without line collapsing
            Assert.Contains(@"<tab code=""General"" name=""General"" type=""Tabular"">", newXml);
            Assert.Contains(@"<action name=""Update"" caption=""Update"" />", newXml);
        }
    }
}
