using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpGridColumnPlanTests
    {
        private const string Xml = "<instance baseline='keep'><level><selection><table><grid childrenOrderedList='keep'><gridAttribute attribute='11111111-1111-1111-1111-111111111111-Machine' description='Machine'/><action name='Keep'/><gridAttribute attribute='22222222-2222-2222-2222-222222222222-Processing' description='Old' defaultDescription='Baseline' export='True'><binding value='keep'/></gridAttribute></grid><filter value='keep'/></table></selection></level><!-- protected --></instance>";
        private static JObject Move() => new JObject { ["gridPath"] = "/instance/level/selection/table/grid", ["attribute"] = "Processing", ["before"] = "Machine", ["caption"] = "Processamento" };
        private static JObject Add() => new JObject { ["gridPath"] = "/instance/level/selection/table/grid", ["variable"] = "ReinicioHistorico", ["caption"] = "Reinício", ["basicType"] = "VarChar", ["length"] = 80, ["before"] = "Machine" };
        [Fact]
        public void MovePreservesBindingMetadataActionsAndOriginalSnapshot()
        {
            var before = XDocument.Parse(Xml); var after = new XDocument(before);
            var diff = WwpActionService.ApplyGridColumnXml(after, "move_grid_column", Move());
            Assert.Null(diff["error"]); Assert.Equal(0, diff["newIndex"]!.Value<int>());
            var column = after.Descendants("grid").Single().Elements().First();
            Assert.Equal("Processamento", (string)column.Attribute("description"));
            Assert.Equal("Baseline", (string)column.Attribute("defaultDescription"));
            Assert.Equal("keep", (string)column.Element("binding").Attribute("value"));
            Assert.True(XNode.DeepEquals(XDocument.Parse(Xml), before));
            Assert.True(WwpActionService.VerifyGridColumnXml(before, after, new XDocument(after), "move_grid_column", Move())["matches"]!.Value<bool>());
        }
        [Fact]
        public void ForwardMoveKeepsOtherChildrenAndPlacesColumnBeforeAnchor()
        {
            var doc = XDocument.Parse(Xml);
            var args = Move(); args["attribute"] = "Machine"; args["before"] = "Processing"; args.Remove("caption");
            var diff = WwpActionService.ApplyGridColumnXml(doc, "move_grid_column", args);
            Assert.Null(diff["error"]);
            var children = doc.Descendants("grid").Single().Elements().ToList();
            Assert.Equal("action", children[0].Name.LocalName);
            Assert.EndsWith("-Machine", (string)children[1].Attribute("attribute"));
            Assert.EndsWith("-Processing", (string)children[2].Attribute("attribute"));
            Assert.Equal("Baseline", (string)children[2].Attribute("defaultDescription"));
        }
        [Fact]
        public void CaptionOnlyKeepsPosition()
        {
            var doc = XDocument.Parse(Xml); var args = Move(); args.Remove("before");
            var result = WwpActionService.ApplyGridColumnXml(doc, "move_grid_column", args);
            Assert.Equal(result["oldIndex"], result["newIndex"]);
        }
        [Theory]
        [InlineData("/instance//grid", "InvalidGridPath")]
        [InlineData("/instance/level/selection/table/grid[2147483648]", "GridNotFound")]
        [InlineData("/instance/level/selection/table", "InvalidGridPath")]
        [InlineData("/instance/level/selection/table/grid[1]", "GridNotFound")]
        public void InvalidPathNeverMutates(string path, string code)
        {
            var doc = XDocument.Parse(Xml); var args = Move(); args["gridPath"] = path;
            Assert.Equal(code, WwpActionService.ApplyGridColumnXml(doc, "move_grid_column", args)["code"]?.ToString());
            Assert.True(XNode.DeepEquals(XDocument.Parse(Xml), doc));
        }
        [Fact]
        public void AmbiguousGridRequiresIndex()
        {
            var doc = XDocument.Parse(Xml); doc.Descendants("table").Single().Add(new XElement("grid"));
            Assert.Equal("AmbiguousGrid", WwpActionService.ApplyGridColumnXml(doc, "move_grid_column", Move())["code"]?.ToString());
            var args = Move(); args["gridPath"] += "[0]";
            Assert.Null(WwpActionService.ApplyGridColumnXml(doc, "move_grid_column", args)["error"]);
        }
        [Fact]
        public void BindingSuffixCannotResolveUnrelatedIdentity()
        {
            var doc = XDocument.Parse(Xml); var args = Move(); args["attribute"] = "sing";
            Assert.Equal("GridColumnNotFound", WwpActionService.ApplyGridColumnXml(doc, "move_grid_column", args)["code"]?.ToString());
        }
        [Fact]
        public void AddUsesPresentationTypeWithoutTableOrBaselineMutation()
        {
            var before = XDocument.Parse(Xml); var after = new XDocument(before);
            Assert.Null(WwpActionService.ApplyGridColumnXml(after, "add_grid_variable", Add())["error"]);
            XElement variable = after.Descendants("gridVariable").Single();
            Assert.Equal("Basic", (string)variable.Attribute("dataType"));
            Assert.Equal("80", (string)variable.Attribute("basicCLength"));
            Assert.Equal("True", (string)variable.Attribute("readOnly"));
            Assert.DoesNotContain(variable.Attributes(), a => a.Name.LocalName.StartsWith("default"));
            var persisted = new XDocument(after); persisted.Descendants("gridVariable").Single().SetAttributeValue("defaultReadOnly", "True");
            Assert.True(WwpActionService.VerifyGridColumnXml(before, after, persisted, "add_grid_variable", Add())["matches"]!.Value<bool>());
            persisted.Root.SetAttributeValue("baseline", "changed");
            Assert.NotNull(WwpActionService.VerifyGridColumnXml(before, after, persisted, "add_grid_variable", Add())["error"]);
        }
        [Theory]
        [InlineData("basicType", "Numeric")]
        [InlineData("length", "0")]
        [InlineData("variable", "&Bad")]
        public void InvalidVariableRequestIsPure(string key, string value)
        {
            var doc = XDocument.Parse(Xml); var args = Add(); args[key] = value;
            Assert.NotNull(WwpActionService.ApplyGridColumnXml(doc, "add_grid_variable", args)["error"]);
            Assert.True(XNode.DeepEquals(XDocument.Parse(Xml), doc));
        }
        [Fact]
        public void VerificationDetectsMovedMetadataOrCommentChange()
        {
            var before = XDocument.Parse(Xml); var after = new XDocument(before);
            WwpActionService.ApplyGridColumnXml(after, "move_grid_column", Move());
            var actual = new XDocument(after); actual.Descendants("binding").Single().SetAttributeValue("value", "changed");
            Assert.NotNull(WwpActionService.VerifyGridColumnXml(before, after, actual, "move_grid_column", Move())["error"]);
            actual = new XDocument(after); actual.Root.Nodes().OfType<XComment>().Single().Remove();
            Assert.NotNull(WwpActionService.VerifyGridColumnXml(before, after, actual, "move_grid_column", Move())["error"]);
        }
        [Fact]
        public void ExistingVariableElsewhereCannotBeRebound()
        {
            var doc = XDocument.Parse(Xml); doc.Root.Add(new XElement("variable", new XAttribute("name", "ReinicioHistorico")));
            Assert.Equal("VariableAlreadyExists", WwpActionService.ApplyGridColumnXml(doc, "add_grid_variable", Add())["code"]?.ToString());
        }
        [Fact]
        public void NativeAndXmlPathUseSameIndexedTypeIdentity()
        {
            var root = new NativeElement("instance");
            var first = new NativeElement("grid"); var second = new NativeElement("grid");
            root.Children.Add(first); root.Children.Add(second);
            var method = typeof(WwpActionService).GetMethod("ResolveGridPath", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(typeof(NativeElement));
            var type = new Func<NativeElement, string>(e => e.Type);
            var children = new Func<NativeElement, List<NativeElement>>(e => e.Children);
            Assert.Same(second, method.Invoke(null, new object[] { root, "/instance/grid[1]", type, children }));
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { root, "/instance/grid", type, children }));
            Assert.Contains("exactly one", error.InnerException.Message);
        }
        private sealed class NativeElement
        {
            internal string Type;
            internal List<NativeElement> Children = new List<NativeElement>();
            internal NativeElement(string type) { Type = type; }
        }

        [Fact]
        public void CaptionPreservesExistingAttributeCasing()
        {
            var doc = XDocument.Parse(Xml.Replace("description='Old'", "Description='Old'"));
            Assert.Null(WwpActionService.ApplyGridColumnXml(doc, "move_grid_column", Move())["error"]);
            var column = doc.Descendants("gridAttribute").First();
            Assert.Single(column.Attributes().Where(a => a.Name.LocalName.Equals("description", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal("Processamento", (string)column.Attribute("Description"));
        }
    }
}
