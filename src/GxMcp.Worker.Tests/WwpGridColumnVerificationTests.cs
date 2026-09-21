using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpGridColumnVerificationTests
    {
        private const string A = "<gxColumn ColAttId='att:1' ColTitle='Machine' Width='20'/>";
        private const string B = "<gxColumn ColAttId='att:2' ColTitle='Old' Width='15'/>";
        private const string Moved = "<gxColumn ColAttId='att:2' ColTitle='Processing' Width='15'/>";
        private static string Form(string columns) => "<GxMultiForm><gxGrid id='Grid1'>" + columns + "</gxGrid><gxButton id='Export'/></GxMultiForm>";
        private static JObject MoveArgs() => new JObject { ["before"] = "Machine", ["caption"] = "Processing" };
        private static JObject Verify(string before, string after, JObject args = null) =>
            WwpActionService.VerifyGridColumnProjection(before, after, "move_grid_column", args ?? MoveArgs(), "att:2", "att:1");

        [Fact]
        public void Move_RequiresExactBindingOrderCaptionAndPreservesOtherControls()
        {
            var result = Verify(Form(A + B), Form(Moved + A));
            Assert.True(result["confirmed"].Value<bool>());
            Assert.True(result["unrelatedWebFormConfirmed"].Value<bool>());
        }

        [Fact]
        public void Move_WrongOrderFailsEvenWhenCaptionAndBindingExist()
        {
            var result = Verify(Form(A + B), Form(A + Moved));
            Assert.False(result["confirmed"].Value<bool>());
            Assert.False(result["orderConfirmed"].Value<bool>());
        }

        [Fact]
        public void Move_RejectsSubstringBindingAndUnknownGridShape()
        {
            Assert.False(Verify(Form(A + B), Form(Moved.Replace("att:2", "att:20") + A))["confirmed"].Value<bool>());
            Assert.False(Verify(Form(A + B), Form(Moved + A).Replace("gxGrid", "table"))["confirmed"].Value<bool>());
        }

        [Fact]
        public void Move_RejectsDuplicateIdentity()
        {
            Assert.False(Verify(Form(A + B), Form(Moved + Moved + A))["confirmed"].Value<bool>());
        }

        [Theory]
        [InlineData("Width='15'", "Width='16'")]
        [InlineData("id='Export'", "id='Other'")]
        [InlineData("ColTitle='Machine'", "ColTitle='Changed'")]
        public void Move_RejectsUnrelatedChanges(string find, string replacement)
        {
            var result = Verify(Form(A + B), Form(Moved + A).Replace(find, replacement));
            Assert.False(result["confirmed"].Value<bool>());
            Assert.False(result["unrelatedWebFormConfirmed"].Value<bool>());
        }

        [Fact]
        public void CaptionOnly_PreservesColumnPosition()
        {
            var args = new JObject { ["caption"] = "Processing" };
            Assert.True(Verify(Form(A + B), Form(A + Moved), args)["confirmed"].Value<bool>());
            Assert.False(Verify(Form(A + B), Form(Moved + A), args)["confirmed"].Value<bool>());
        }

        [Fact]
        public void CaptionExpressionCannotMasqueradeAsConfirmedLiteral()
        {
            var result = Verify(Form(A + B), Form(Moved.Replace("Width=", "ColTitleExpression='SomeExpression' Width=") + A));
            Assert.False(result["captionConfirmed"].Value<bool>());
        }

        [Fact]
        public void Move_CannotCrossGridIdentity()
        {
            string before = "<GxMultiForm><gxGrid id='One'>" + B + "</gxGrid><gxGrid id='Two'>" + A + "</gxGrid></GxMultiForm>";
            string after = "<GxMultiForm><gxGrid id='One'/><gxGrid id='Two'>" + Moved + A + "</gxGrid></GxMultiForm>";
            Assert.False(Verify(before, after)["confirmed"].Value<bool>());
        }

        [Fact]
        public void MissingOrMalformedProjectionIsNotConfirmed()
        {
            Assert.False(Verify(Form(A + B), "<broken")["confirmed"].Value<bool>());
            Assert.False(Verify(Form(A + B), null)["confirmed"].Value<bool>());
        }

        [Fact]
        public void XmlAttributeOrderAndIndentationAreNotSemanticChanges()
        {
            var after = Form(Moved.Replace("ColAttId='att:2' ColTitle='Processing'", "ColTitle='Processing' ColAttId='att:2'") + "\r\n" + A);
            Assert.True(Verify(Form(A + B), after)["confirmed"].Value<bool>());
        }

        [Fact]
        public void AddVariable_RequiresExactNewBindingAndPreservesExistingForm()
        {
            var args = new JObject { ["caption"] = "Restart", ["before"] = "Machine" };
            const string added = "<gxColumn ColAttId='var:3' ColTitle='Restart'/>";
            var result = WwpActionService.VerifyGridColumnProjection(Form(A), Form(added + A), "add_grid_variable", args, "var:3", "att:1");
            Assert.True(result["confirmed"].Value<bool>());
            result = WwpActionService.VerifyGridColumnProjection(Form(A), Form(A + added), "add_grid_variable", args, "var:3", "att:1");
            Assert.False(result["confirmed"].Value<bool>());
        }

        private static JObject Variable(string name, int id) => new JObject
        {
            ["name"] = name, ["id"] = id, ["basicType"] = "VarChar", ["length"] = 100,
            ["decimals"] = 0, ["definition"] = "<variable name='" + name + "' custom='keep'/>"
        };
        private static JObject VariableArgs() => new JObject { ["variable"] = "Restart", ["basicType"] = "VarChar", ["length"] = 100 };

        [Fact]
        public void NewVariable_ConfirmsDeclarationAndEveryExistingDefinition()
        {
            var before = new JArray(Variable("Existing", 1));
            var after = new JArray(Variable("Existing", 1), Variable("Restart", 2));
            Assert.True(WwpActionService.VerifyGridColumnVariables(before, after, "add_grid_variable", VariableArgs())["confirmed"].Value<bool>());
            after[0]["definition"] = "changed";
            Assert.False(WwpActionService.VerifyGridColumnVariables(before, after, "add_grid_variable", VariableArgs())["existingVariablesConfirmed"].Value<bool>());
        }

        [Fact]
        public void NewVariable_RejectsWrongTypeDuplicateOrUnexpectedExtra()
        {
            var before = new JArray(Variable("Existing", 1));
            var after = new JArray(Variable("Existing", 1), Variable("Restart", 2));
            after[1]["basicType"] = "Numeric";
            Assert.False(WwpActionService.VerifyGridColumnVariables(before, after, "add_grid_variable", VariableArgs())["confirmed"].Value<bool>());
            after.Add(Variable("Restart", 3));
            Assert.False(WwpActionService.VerifyGridColumnVariables(before, after, "add_grid_variable", VariableArgs())["confirmed"].Value<bool>());
            after[1] = Variable("Restart", 2);
            after[2] = Variable("Extra", 3);
            Assert.False(WwpActionService.VerifyGridColumnVariables(before, after, "add_grid_variable", VariableArgs())["confirmed"].Value<bool>());
        }

        [Fact]
        public void Move_CannotChangeAnyVariable()
        {
            var before = new JArray(Variable("Existing", 1));
            Assert.True(WwpActionService.VerifyGridColumnVariables(before, (JArray)before.DeepClone(), "move_grid_column", new JObject())["confirmed"].Value<bool>());
            Assert.False(WwpActionService.VerifyGridColumnVariables(before, new JArray(), "move_grid_column", new JObject())["confirmed"].Value<bool>());
        }

        [Fact]
        public void Events_RequireExactProtectedAndCustomContentIncludingLineEndings()
        {
            const string source = "Event Load\r\n // protected and custom\r\nEndevent\r\n";
            Assert.True(WwpActionService.VerifyGridColumnEvents(source, source)["confirmed"].Value<bool>());
            Assert.False(WwpActionService.VerifyGridColumnEvents(source, source.Replace("custom", "changed"))["confirmed"].Value<bool>());
            Assert.False(WwpActionService.VerifyGridColumnEvents(source, source.Replace("\r\n", "\n"))["confirmed"].Value<bool>());
            Assert.False(WwpActionService.VerifyGridColumnEvents(null, null)["confirmed"].Value<bool>());
        }
    }
}
