using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ResourceSubscriptionTests
    {
        [Fact]
        public void McpRouter_ResourcesSubscribe_ReturnsSuccess()
        {
            var req = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "resources/subscribe",
                ["params"] = new JObject
                {
                    ["uri"] = "genexus://objects/Customer/part/Source"
                }
            };

            var res = McpRouter.Handle(req);
            Assert.NotNull(res);

            var jobj = JObject.FromObject(res);
            Assert.Equal("complete", jobj["resultType"]?.ToString());
            Assert.True(jobj["subscribed"]?.Value<bool>());
            Assert.Equal("genexus://objects/Customer/part/Source", jobj["uri"]?.ToString());
        }

        [Fact]
        public void McpRouter_ResourcesUnsubscribe_ReturnsSuccess()
        {
            var req = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "resources/unsubscribe",
                ["params"] = new JObject
                {
                    ["uri"] = "genexus://kb/health"
                }
            };

            var res = McpRouter.Handle(req);
            Assert.NotNull(res);

            var jobj = JObject.FromObject(res);
            Assert.Equal("complete", jobj["resultType"]?.ToString());
            Assert.False(jobj["subscribed"]?.Value<bool>());
            Assert.Equal("genexus://kb/health", jobj["uri"]?.ToString());
        }
    }
}
