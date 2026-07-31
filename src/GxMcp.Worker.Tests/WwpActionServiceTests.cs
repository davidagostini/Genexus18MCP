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
    }
}
