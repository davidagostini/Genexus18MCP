using System.Xml.Linq;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpActionServiceTests
    {
        [Fact]
        public void AddGridAction_WritesTypedAttributesAndNeverAddsSecurity()
        {
            var doc = XDocument.Parse("<instance><grid><actionGroup name='Actions'/></grid></instance>");
            JObject result = WwpActionService.Apply(doc, "add_grid_action", new JObject
            {
                ["group"] = "Actions", ["actionName"] = "Approve", ["description"] = "Approve order",
                ["selection"] = "multiple", ["enabledWhen"] = "Status = 1", ["confirmation"] = "Continue?"
            }, null);

            Assert.Null(result["error"]);
            XElement action = doc.Root.Element("grid").Element("actionGroup").Element("userAction");
            Assert.Equal("Approve", (string)action.Attribute("name"));
            Assert.Equal("True", (string)action.Attribute("multiRowSelection"));
            Assert.Null(action.Attribute("SecFuntionKey"));
            Assert.Null(action.Attribute("addSecurityToCall"));
            Assert.Equal("Continue?", (string)action.Attribute("confirmMessage"));
        }

        [Fact]
        public void MoveAndRemoveAction_KeepRequestedOrder()
        {
            var doc = XDocument.Parse("<instance><grid><actionGroup name='A'><userAction name='One'/><userAction name='Two'/></actionGroup><actionGroup name='B'/></grid></instance>");
            WwpActionService.Apply(doc, "move_action", new JObject
            { ["group"] = "A", ["actionName"] = "Two", ["newGroup"] = "B", ["position"] = 0 }, null);
            Assert.Equal("Two", (string)doc.Descendants("actionGroup").Last().Element("userAction").Attribute("name"));

            WwpActionService.Apply(doc, "remove_action", new JObject
            { ["group"] = "A", ["actionName"] = "One" }, null);
            Assert.Empty(doc.Descendants("actionGroup").First().Elements("userAction"));
        }

        [Fact]
        public void AddTab_BuildsCanonicalResponsiveTableAndTypedChildren()
        {
            var doc = XDocument.Parse("<instance><WPRoot><tabs><tab ControlName='One' title='One'/><tab ControlName='Three' title='Three'/></tabs></WPRoot></instance>");
            JObject result = WwpActionService.ApplyTabXml(doc, "add_tab", new JObject
            {
                ["controlName"] = "Two",
                ["title"] = "Second",
                ["position"] = 1,
                ["children"] = new JArray
                {
                    new JObject { ["type"] = "variable", ["name"] = "Choice", ["basicType"] = "VarChar", ["length"] = 40, ["description"] = "Choice" },
                    new JObject { ["type"] = "userAction", ["name"] = "Send", ["caption"] = "Send now" }
                }
            });

            Assert.Null(result["error"]);
            XElement[] tabs = doc.Descendants("tab").ToArray();
            Assert.Equal(new[] { "One", "Two", "Three" }, tabs.Select(t => (string)t.Attribute("ControlName")));
            XElement table = tabs[1].Element("table");
            Assert.Equal("Responsive", (string)table.Attribute("type"));
            Assert.Equal("Choice", (string)table.Element("variable").Attribute("name"));
            Assert.Equal("40", (string)table.Element("variable").Attribute("basicCLength"));
            Assert.Equal("Send", (string)table.Element("userAction").Attribute("name"));
            Assert.Null(table.Attribute("childrenOrderedList"));
        }

        [Fact]
        public void MoveAndRemoveTab_PreserveOrderAndReportMissingSeparately()
        {
            var doc = XDocument.Parse("<instance><tabs><tab ControlName='One'/><tab ControlName='Two'/><tab ControlName='Three'/></tabs></instance>");
            JObject moved = WwpActionService.ApplyTabXml(doc, "move_tab",
                new JObject { ["controlName"] = "Three", ["position"] = 0 });
            Assert.Null(moved["error"]);
            Assert.Equal(new[] { "Three", "One", "Two" }, doc.Descendants("tab").Select(t => (string)t.Attribute("ControlName")));

            JObject removed = WwpActionService.ApplyTabXml(doc, "remove_tab",
                new JObject { ["controlName"] = "One" });
            Assert.Null(removed["error"]);
            Assert.Equal(new[] { "Three", "Two" }, doc.Descendants("tab").Select(t => (string)t.Attribute("ControlName")));

            JObject missing = WwpActionService.ApplyTabXml(doc, "remove_tab",
                new JObject { ["controlName"] = "Missing" });
            Assert.Equal("TabNotFound", (string)missing["code"]);
            JObject invalid = WwpActionService.ApplyTabXml(doc, "rename_tab",
                new JObject { ["controlName"] = "Two" });
            Assert.Equal("UnknownWwpActionOperation", (string)invalid["code"]);
        }
    }
}
