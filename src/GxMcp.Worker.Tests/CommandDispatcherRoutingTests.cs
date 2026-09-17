using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public sealed class CommandDispatcherRoutingTests
    {
        private static JObject observedArgs;

        [Fact]
        public void Nested_tool_args_do_not_receive_worker_routing_module_action_or_target()
        {
            observedArgs = null;
            var dispatcher = (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
            var tableField = typeof(CommandDispatcher).GetField("_commandTable", BindingFlags.NonPublic | BindingFlags.Instance);
            var table = (IDictionary)Activator.CreateInstance(tableField.FieldType);
            var handlerType = tableField.FieldType.GetGenericArguments()[1];
            table.Add("object", Delegate.CreateDelegate(
                handlerType,
                typeof(CommandDispatcherRoutingTests).GetMethod(nameof(Observe), BindingFlags.NonPublic | BindingFlags.Static)));
            tableField.SetValue(dispatcher, table);

            var request = new JObject
            {
                ["method"] = "object",
                ["action"] = "ExportTextBatch",
                ["target"] = "Procedure:Customer",
                ["params"] = new JObject
                {
                    ["module"] = "Object",
                    ["action"] = "ExportTextBatch",
                    ["target"] = "Procedure:Customer",
                    ["params"] = new JObject { ["outputPath"] = "C:\\temp\\text" }
                }
            };

            JObject response = JObject.Parse(dispatcher.Dispatch(request.ToString()));

            Assert.Equal("RoutingProbe", response["code"]?.ToString());
            Assert.NotNull(observedArgs);
            Assert.Null(observedArgs["module"]);
            Assert.Null(observedArgs["action"]);
            Assert.Null(observedArgs["target"]);
            Assert.Equal("C:\\temp\\text", observedArgs["outputPath"]?.ToString());
        }

        private static string Observe(JObject request, string method, string action, string target, string payload, JObject args)
        {
            observedArgs = args;
            return "{\"status\":\"ok\",\"code\":\"RoutingProbe\"}";
        }
    }
}
