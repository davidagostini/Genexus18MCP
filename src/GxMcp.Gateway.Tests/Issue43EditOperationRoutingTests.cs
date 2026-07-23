using System;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #43 #1 (CRITICAL data-loss): genexus_edit with operation=Append / Insert_After but
    // WITHOUT mode=patch used to fall through to the else-branch, which routes to the Write module
    // (full-part replace with `content`) and silently DISCARDS `operation`. The whole Source part
    // (~888 lines) was overwritten with just the payload. These tests pin the routing so an
    // explicit operation can never reach a full-replace Write.
    public class Issue43EditOperationRoutingTests
    {
        private static JObject Route(string tool, JObject args)
        {
            var routed = new ObjectRouter().ConvertToolCall(tool, args);
            Assert.NotNull(routed);
            return JObject.FromObject(routed!);
        }

        [Theory]
        [InlineData("Append")]
        [InlineData("Insert_After")]
        [InlineData("Replace")]
        public void EditOperation_WithoutMode_RoutesToPatchNotFullWrite(string operation)
        {
            var args = new JObject
            {
                ["name"] = "ProcArqHomologadosUniGra",
                ["part"] = "Source",
                // NOTE: no "mode" — the exact shape that caused the data loss.
                ["operation"] = operation,
                ["context"] = "// anchor line",
                ["content"] = "// new lines"
            };
            var jo = Route("genexus_edit", args);
            Assert.Equal("Patch", jo["module"]!.ToString());
            Assert.Equal("Apply", jo["action"]!.ToString());
            Assert.Equal(operation, jo["operation"]!.ToString());
        }

        [Fact]
        public void EditOperation_WithModePatch_StillRoutesToPatch()
        {
            var args = new JObject
            {
                ["name"] = "ProcArqHomologadosUniGra",
                ["part"] = "Source",
                ["mode"] = "patch",
                ["operation"] = "Append",
                ["content"] = "// appended"
            };
            var jo = Route("genexus_edit", args);
            Assert.Equal("Patch", jo["module"]!.ToString());
            Assert.Equal("Append", jo["operation"]!.ToString());
        }

        [Fact]
        public void EditFullMode_NoOperation_StillRoutesToFullWrite()
        {
            // mode=full with only content (and no operation) is a legitimate whole-part rewrite
            // and must keep routing to the Write module unchanged.
            var args = new JObject
            {
                ["name"] = "MyProc",
                ["part"] = "Source",
                ["mode"] = "full",
                ["content"] = "whole new source"
            };
            var jo = Route("genexus_edit", args);
            Assert.Equal("Write", jo["module"]!.ToString());
        }

        [Fact]
        public void EditNoModeNoOperation_RoutesToFullWrite()
        {
            var args = new JObject
            {
                ["name"] = "MyProc",
                ["part"] = "Source",
                ["content"] = "whole new source"
            };
            var jo = Route("genexus_edit", args);
            Assert.Equal("Write", jo["module"]!.ToString());
        }

        [Theory]
        [InlineData("full")]
        [InlineData("ops")]
        public void EditOperation_WithConflictingMode_ThrowsUsageError(string mode)
        {
            var args = new JObject
            {
                ["name"] = "MyProc",
                ["part"] = "Source",
                ["mode"] = mode,
                ["operation"] = "Append",
                ["content"] = "// appended"
            };
            var ex = Assert.Throws<UsageException>(() => new ObjectRouter().ConvertToolCall("genexus_edit", args));
            Assert.Contains("operation", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
