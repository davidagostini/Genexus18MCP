using System.Collections.Generic;
using System.Xml.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpLayoutTests
    {
        [Fact]
        public void InvalidationFailure_PreservesPersistenceReceiptAndSkipsUnmutatedCalls()
        {
            var receipt = JObject.Parse("{snapshot:{path:'saved'},rollbackVerified:true}");
            int calls = 0;
            System.Action fail = () => { calls++; throw new System.InvalidOperationException("index unavailable"); };
            WwpActionService.FinalizeLayoutReadCaches(false, receipt, fail);
            Assert.Equal(0, calls);
            WwpActionService.FinalizeLayoutReadCaches(true, receipt, fail);
            Assert.Equal(1, calls);
            Assert.Equal("index unavailable", receipt["readCacheInvalidationError"].Value<string>());
            Assert.Equal("saved", receipt["snapshot"]["path"].Value<string>());
            Assert.True(receipt["rollbackVerified"].Value<bool>());
        }

        [Fact]
        public void ParentGate_UsesNativeWebPanelClassSharedByWebComponent()
        {
            var native = Assembly.Load("Artech.Genexus.Common");
            Assert.True(WwpActionService.IsLayoutParentType(native.GetType("Artech.Genexus.Common.Objects.WebPanel", true)));
            Assert.False(WwpActionService.IsLayoutParentType(native.GetType("Artech.Genexus.Common.Objects.Transaction", true)));
            Assert.False(WwpActionService.IsLayoutParentType(native.GetType("Artech.Genexus.Common.Objects.Procedure", true)));
            Assert.False(WwpActionService.IsLayoutParentType(null));
        }

        [Fact]
        public void MutationFinalization_ClearsPublicPatternAndFormPayloadsButPreviewDoesNot()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var set = typeof(ObjectService).GetMethod("SetReadCache", flags);
            var get = typeof(ObjectService).GetMethod("TryGetReadCache", flags);
            string prefix = System.Guid.NewGuid().ToString("N");
            foreach (string part in new[] { "patterninstance", "webform", "events" })
            {
                string key = prefix + "|" + part + "|raw";
                set.Invoke(null, new object[] { key, "old layout source" });
                WwpActionService.InvalidateLayoutReadCaches(false);
                Assert.True((bool)get.Invoke(null, new object[] { key, null }));
            }
            WwpActionService.InvalidateLayoutReadCaches(true);
            foreach (string part in new[] { "patterninstance", "webform", "events" })
                Assert.False((bool)get.Invoke(null, new object[] { prefix + "|" + part + "|raw", null }));
        }

        [Fact]
        public void ProjectionReceipt_RequiresEveryNativeCallbackAndParentSave()
        {
            var result = new WwpProjectionHelper.ProjectionResult
            {
                LifecycleAttempted = true, ShouldBuild = true, BeforeStartBuild = true,
                AfterImportResources = true, UpdateParentObject = true, AfterEndBuild = true
            };
            Assert.False(WwpActionService.LayoutProjectionReceipt(result)["confirmed"].Value<bool>());
            result.ParentSaved = true;
            Assert.True(WwpActionService.LayoutProjectionReceipt(result)["confirmed"].Value<bool>());
            result.AfterEndBuild = false;
            result.Failure = "Callback failed";
            var receipt = WwpActionService.LayoutProjectionReceipt(result);
            Assert.False(receipt["confirmed"].Value<bool>());
            Assert.Equal("Callback failed", receipt["failure"].Value<string>());
        }

        // Mirrors SourcePart's lazy getter and serialization of its backing field.
        private sealed class LazySource : ISource
        {
            private readonly string persisted;
            private string materialized;
            internal int SetterCalls;
            internal LazySource(string persisted) { this.persisted = persisted; }
            public string Source
            {
                get => materialized ?? (materialized = persisted);
                set { SetterCalls++; materialized = value; }
            }
            internal string Serialize() => materialized ?? string.Empty;
        }

        [Fact]
        public void SnapshotPreparation_MaterializesEveryLazySourceWithoutChangingIt()
        {
            var events = new LazySource("Event Start\n  &Value = 1\nEndEvent");
            var rules = new LazySource("parm(&Value);");
            Assert.Equal(string.Empty, events.Serialize());
            WwpActionService.PrepareLayoutSnapshotSources(new ISource[] { events, rules });
            Assert.Equal("Event Start\n  &Value = 1\nEndEvent", events.Serialize());
            Assert.Equal("parm(&Value);", rules.Serialize());
            Assert.Equal(0, events.SetterCalls);
            Assert.Equal(0, rules.SetterCalls);
        }

        private sealed class NativeElement
        {
            public string Name { get; set; }
            public string Type { get; set; }
        }

        [Fact]
        public void NativeVariableIdentity_UsesChildNameRatherThanSpecificationType()
        {
            Assert.True(WwpActionService.IsLayoutVariableElement(new NativeElement { Name = "variable", Type = "TableVariable" }));
            Assert.False(WwpActionService.IsLayoutVariableElement(new NativeElement { Name = "attribute", Type = "TableAttribute" }));
        }

        private const string Empty = "<instance><WPRoot Template='Empty'><table name='TableContent' defaultType='Responsive' childrenOrderedList='old'/></WPRoot></instance>";
        private static JObject Args(string children) => new JObject
        {
            ["tablePath"] = "TableContent", ["children"] = JArray.Parse(children)
        };

        [Fact]
        public void Plan_AppendsTypedNestedChildrenWithoutRewritingBaseline()
        {
            var doc = XDocument.Parse(Empty);
            doc.Root.Element("WPRoot").Element("table").Attribute("childrenOrderedList").Remove();
            var before = new XDocument(doc);
            var args = Args("[{type:'table',name:'Fields',children:[{type:'variable',name:'Application'},{type:'userAction',name:'Save',caption:'Save'}]}]");
            Assert.Null(WwpActionService.PlanLayout(doc, args)["error"]);
            Assert.Equal("Responsive", (string)doc.Root.Element("WPRoot").Element("table").Attribute("defaultType"));
            Assert.Null(doc.Root.Element("WPRoot").Element("table").Attribute("childrenOrderedList"));
            Assert.True(WwpActionService.VerifyLayoutPattern(before, doc, args));
        }

        [Theory]
        [InlineData("table")]
        [InlineData("userAction")]
        public void Plan_RejectsOrderedAdditionsToExistingOrderListWithoutMutation(string type)
        {
            var doc = XDocument.Parse(Empty);
            var result = WwpActionService.PlanLayout(doc, Args("[{type:'" + type + "',name:'NewChild'}]"));
            Assert.Equal("WwpOrderedContainerUnsupported", result["code"].Value<string>());
            Assert.True(XNode.DeepEquals(XDocument.Parse(Empty), doc));
        }

        [Fact]
        public void Plan_VariableAdditionPreservesExistingOrderList()
        {
            var doc = XDocument.Parse(Empty);
            var args = Args("[{type:'variable',name:'Application'}]");
            Assert.Null(WwpActionService.PlanLayout(doc, args)["error"]);
            Assert.Equal("old", (string)doc.Root.Element("WPRoot").Element("table").Attribute("childrenOrderedList"));
            Assert.True(WwpActionService.VerifyLayoutPattern(XDocument.Parse(Empty), doc, args));
        }

        [Theory]
        [InlineData("[{type:'variable',name:'Application',basicType:'VarChar'}]")]
        [InlineData("[{type:'variable',name:'Application',domain:'Other'}]")]
        [InlineData("[{type:'attribute',name:'Application'}]")]
        [InlineData("[{type:'table',name:'Fields',defaultType:'Regular'}]")]
        [InlineData("[{type:'table',name:'Fields',childrenOrderedList:'x'}]")]
        [InlineData("[{type:'variable',name:'Application'},{type:'variable',name:'Application'}]")]
        [InlineData("[{type:'variable',name:'TableContent'}]")]
        [InlineData("[null]")]
        public void Plan_RejectsRetypingRawMetadataUnsupportedAndDuplicateChildren(string children)
        {
            var doc = XDocument.Parse(Empty);
            Assert.NotNull(WwpActionService.PlanLayout(doc, Args(children))["error"]);
            Assert.True(XNode.DeepEquals(XDocument.Parse(Empty), doc));
        }

        [Fact]
        public void PersistedPattern_RejectsMissingChildOrUnrelatedMutation()
        {
            var args = Args("[{type:'variable',name:'Application'}]");
            var after = XDocument.Parse(Empty);
            Assert.Null(WwpActionService.PlanLayout(after, args)["error"]);
            after.Root.Element("WPRoot").SetAttributeValue("Template", "Other");
            Assert.False(WwpActionService.VerifyLayoutPattern(XDocument.Parse(Empty), after, args));
            Assert.False(WwpActionService.VerifyLayoutPattern(XDocument.Parse(Empty), XDocument.Parse(Empty), args));
        }

        [Theory]
        [InlineData("var:7", true)]
        [InlineData("att:7", false)]
        [InlineData("var:8", false)]
        public void Projection_RequiresActualVariableIdentity(string binding, bool expected)
        {
            string before = "<Form><gxTextBlock ControlName='Original' Caption='Keep'/></Form>";
            string after = "<Form><gxTextBlock ControlName='Original' Caption='Keep'/><gxEdit ControlName='Application' AttId='" + binding + "'/></Form>";
            var check = WwpActionService.VerifyLayoutProjection(before, after,
                JArray.Parse("[{type:'variable',name:'Application'}]"), new Dictionary<string, string> { ["Application"] = "var:7" });
            Assert.Equal(expected, check["confirmed"].Value<bool>());
            if (!expected) Assert.Equal("variableControlMissingOrAmbiguous", check["failureStage"].Value<string>());
        }

        [Fact]
        public void Projection_RejectsChangedPreexistingControl()
        {
            var check = WwpActionService.VerifyLayoutProjection("<Form><gxEdit ControlName='Old' AttId='var:1'/></Form>",
                "<Form><gxEdit ControlName='Old' AttId='att:1'/><gxEdit ControlName='New' AttId='var:2'/></Form>",
                JArray.Parse("[{type:'variable',name:'New'}]"), new Dictionary<string, string> { ["New"] = "var:2" });
            Assert.False(check["confirmed"].Value<bool>());
        }

        [Theory]
        [InlineData("<gxEdit ControlName='Two'/><gxEdit ControlName='One'/>")]
        [InlineData("<gxEdit ControlName='One'><Property>changed</Property></gxEdit><gxEdit ControlName='Two'/>")]
        public void Projection_RejectsPreexistingOrderOrPropertyChanges(string controls)
        {
            var check = WwpActionService.VerifyLayoutProjection(
                "<Form><gxEdit ControlName='One'/><gxEdit ControlName='Two'/></Form>",
                "<Form>" + controls + "<gxEdit ControlName='New' AttId='var:2'/></Form>",
                JArray.Parse("[{type:'variable',name:'New'}]"), new Dictionary<string, string> { ["New"] = "var:2" });
            Assert.False(check["confirmed"].Value<bool>());
        }

        [Theory]
        [InlineData("<td>Original</td>", "<td>Changed</td>")]
        [InlineData("<td>Original</td>", "")]
        [InlineData("<gxLabel Caption='Original'/>", "<gxLabel Caption='Changed'/>")]
        [InlineData("<div><b>Original</b></div>", "<div><i>Original</i></div>")]
        [InlineData("<!--keep--><td/>", "<td/>")]
        public void Projection_RejectsUnnamedContentLossOrMutation(string before, string after)
        {
            var check = WwpActionService.VerifyLayoutProjection("<Form>" + before + "</Form>",
                "<Form>" + after + "<gxEdit ControlName='New' AttId='var:2'/></Form>",
                JArray.Parse("[{type:'variable',name:'New'}]"), new Dictionary<string, string> { ["New"] = "var:2" });
            Assert.False(check["confirmed"].Value<bool>());
        }

        [Fact]
        public void Projection_PreservesUnnamedContentAndAllowsNewAnonymousLayoutWrappers()
        {
            const string preserved = "<td>Original</td><!--keep--><gxLabel Caption='Original'/>";
            var check = WwpActionService.VerifyLayoutProjection("<Form>" + preserved + "</Form>",
                "<Form>" + preserved + "<tr><td><gxEdit ControlName='New' AttId='var:2'/></td></tr></Form>",
                JArray.Parse("[{type:'variable',name:'New'}]"), new Dictionary<string, string> { ["New"] = "var:2" });
            Assert.True(check["confirmed"].Value<bool>());
            Assert.True(check["unnamedContentPreserved"].Value<bool>());
        }

        [Fact]
        public void NativeLayoutProjection_AcceptsRegeneratedStructuralIdsAndNativeBindings()
        {
            const string before = "<layout id='11111111-1111-1111-1111-111111111111'><table controlName='Content' tableType='Responsive' id='1'><row><cell><data attribute='var:1' id='33333333-3333-3333-3333-333333333333'/></cell></row></table></layout>";
            const string additions = "<row><cell><data attribute='var:7'/></cell></row><row><cell><action controlName='BtnSave' onClickEvent='&apos;DoSave&apos;' caption='Save'/></cell></row>";
            string after = before.Replace("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222")
                .Replace("</table>", additions + "</table>");
            var children = JArray.Parse("[{type:'variable',name:'Application'},{type:'userAction',name:'Save'}]");
            var bindings = new Dictionary<string, string> { ["Application"] = "var:7" };
            Assert.True(WwpActionService.VerifyLayoutProjection(before, after, children, bindings)["confirmed"].Value<bool>());
            foreach (string changed in new[] { after.Replace("33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444"), after.Replace("id='1'", "id='2'"), after.Replace("Responsive", "Fixed"),
                after.Replace("var:1", "var:2"), after.Replace("DoSave", "DoOther"), after.Replace("BtnSave", "Save") })
                Assert.False(WwpActionService.VerifyLayoutProjection(before, changed, children, bindings)["confirmed"].Value<bool>());
        }

        [Fact]
        public void Events_AllowsOnlyExactEmptyGeneratedMarkersInNewAction()
        {
            const string start = "/* Generated by DVelop Work With Plus Pattern [Start] - Do not change */";
            const string end = "/* Generated by DVelop Work With Plus Pattern [End] - Do not change */";
            string before = "Event Start\n\t" + start + "\n\t" + end + "\nEndEvent\n\nEvent 'Existing'\n &Value = 1\nEndEvent\n";
            string stub = "Event 'DoSave'\n\n\t" + start + "\n\n\t" + end + "\n\nEndEvent\n\n";
            var children = JArray.Parse("[{type:'userAction',name:'Save'}]");
            string after = before.Replace("Event 'Existing'", stub + "Event 'Existing'");
            Assert.True(WwpActionService.VerifyLayoutEvents(before, after, children));
            Assert.False(WwpActionService.VerifyLayoutEvents(before, after.Replace(stub, stub.Replace(start, start + "\n &Value = 2")), children));
            Assert.False(WwpActionService.VerifyLayoutEvents(before, after.Replace(stub, stub.Replace(end, "/* Other comment */")), children));
            Assert.False(WwpActionService.VerifyLayoutEvents(before, after.Replace("&Value = 1", "&Value = 2"), children));
        }

        [Fact]
        public void Events_AllowsOnlyAnEmptyNewActionStub()
        {
            string original = "Event Start\n  &Value = 1\nEndEvent\n";
            var children = JArray.Parse("[{type:'userAction',name:'Save'}]");
            Assert.True(WwpActionService.VerifyLayoutEvents(original, original + "Event 'DoSave'\nEndEvent\n", children));
            Assert.False(WwpActionService.VerifyLayoutEvents(original, original + "Event 'DoSave'\n  &Value = 2\nEndEvent\n", children));
            Assert.False(WwpActionService.VerifyLayoutEvents(original, original.Replace("= 1", "= 2"), children));
        }
    }
}
