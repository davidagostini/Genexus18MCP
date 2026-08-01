using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests
{
    // issue #60 — the save+specify args (validationMode, rollbackOnFailure) must be
    // forwarded from the gateway routers to the worker, or the worker's
    // SaveSpecifyOrchestrator never sees them. These tests pin the forwarding on the
    // write tools that advertise the params in tool_definitions.json.
    public class SpecifyValidationRouterTests
    {
        [Fact]
        public void Edit_FullMode_ForwardsValidationModeAndRollback()
        {
            var router = new ObjectRouter();
            var msg = router.ConvertToolCall("genexus_edit",
                JObject.Parse("{\"name\":\"Customer\",\"part\":\"Source\",\"content\":\"new\",\"validationMode\":\"specify\",\"rollbackOnFailure\":true}"));
            Assert.NotNull(msg);
            var jo = JObject.FromObject(msg!);
            Assert.Equal("Write", jo["module"]?.ToString());
            Assert.Equal("specify", jo["validationMode"]?.ToString());
            Assert.Equal(true, jo["rollbackOnFailure"]?.ToObject<bool>());
        }

        [Fact]
        public void Variable_Add_ForwardsValidationModeAndRollback()
        {
            var router = new OperationsRouter();
            var msg = router.ConvertToolCall("genexus_variable",
                JObject.Parse("{\"action\":\"add\",\"name\":\"MyPanel\",\"varName\":\"&X\",\"validationMode\":\"specify\",\"rollbackOnFailure\":true}"));
            Assert.NotNull(msg);
            var jo = JObject.FromObject(msg!);
            Assert.Equal("specify", jo["validationMode"]?.ToString());
            Assert.Equal(true, jo["rollbackOnFailure"]?.ToObject<bool>());
        }

        [Fact]
        public void Properties_Set_ForwardsValidationModeAndRollback()
        {
            var router = new OperationsRouter();
            var msg = router.ConvertToolCall("genexus_properties",
                JObject.Parse("{\"action\":\"set\",\"name\":\"Customer\",\"propertyName\":\"Nullable\",\"value\":\"Yes\",\"validationMode\":\"specify\",\"rollbackOnFailure\":true}"));
            Assert.NotNull(msg);
            var jo = JObject.FromObject(msg!);
            Assert.Equal("Property", jo["module"]?.ToString());
            Assert.Equal("specify", jo["validationMode"]?.ToString());
            Assert.Equal(true, jo["rollbackOnFailure"]?.ToObject<bool>());
        }

        [Fact]
        public void Structure_UpdateVisual_ForwardsValidationModeAndRollback()
        {
            var router = new OperationsRouter();
            var msg = router.ConvertToolCall("genexus_structure",
                JObject.Parse("{\"action\":\"update_visual\",\"name\":\"Customer\",\"payload\":\"{}\",\"validationMode\":\"specify\",\"rollbackOnFailure\":true}"));
            Assert.NotNull(msg);
            var jo = JObject.FromObject(msg!);
            Assert.Equal("specify", jo["validationMode"]?.ToString());
            Assert.Equal(true, jo["rollbackOnFailure"]?.ToObject<bool>());
        }

        [Fact]
        public void Create_Object_ForwardsValidationModeAndRollback()
        {
            var router = new OperationsRouter();
            var msg = router.ConvertToolCall("genexus_create",
                JObject.Parse("{\"action\":\"object\",\"name\":\"NewPanel\",\"type\":\"WebPanel\",\"validationMode\":\"specify\",\"rollbackOnFailure\":true}"));
            Assert.NotNull(msg);
            var jo = JObject.FromObject(msg!);
            Assert.Equal("specify", jo["validationMode"]?.ToString());
            Assert.Equal(true, jo["rollbackOnFailure"]?.ToObject<bool>());
        }
    }
}
