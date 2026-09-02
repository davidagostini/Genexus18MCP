using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Pipelines;

namespace GxMcp.Gateway.Tests
{
    public class McpMiddlewarePipelineTests
    {
        [Fact]
        public async Task Pipeline_ExecutesMiddlewares_InOrder()
        {
            var executionOrder = new System.Collections.Generic.List<string>();

            var pipeline = new McpMiddlewarePipeline();
            pipeline.Use(new TestStepMiddleware("Step1", executionOrder));
            pipeline.Use(new TestStepMiddleware("Step2", executionOrder));

            var context = new McpPipelineContext(new JObject
            {
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_read",
                    ["arguments"] = new JObject { ["name"] = "Customer" }
                }
            });

            var res = await pipeline.ExecuteAsync(context, ctx =>
            {
                executionOrder.Add("Terminal");
                return Task.FromResult<JObject?>(new JObject { ["status"] = "Success" });
            });

            Assert.NotNull(res);
            Assert.Equal("Success", res["status"]?.ToString());
            Assert.Equal(5, executionOrder.Count);
            Assert.Equal("Step1-Before", executionOrder[0]);
            Assert.Equal("Step2-Before", executionOrder[1]);
            Assert.Equal("Terminal", executionOrder[2]);
            Assert.Equal("Step2-After", executionOrder[3]);
            Assert.Equal("Step1-After", executionOrder[4]);
        }

        [Fact]
        public async Task DryRunMiddleware_AttachesDryRunMeta_WhenDryRunRequested()
        {
            var pipeline = new McpMiddlewarePipeline();
            pipeline.Use(new DryRunMiddleware());

            var context = new McpPipelineContext(new JObject
            {
                ["params"] = new JObject
                {
                    ["name"] = "genexus_edit",
                    ["arguments"] = new JObject
                    {
                        ["name"] = "Proc1",
                        ["dryRun"] = true
                    }
                }
            });

            var res = await pipeline.ExecuteAsync(context, ctx =>
            {
                return Task.FromResult<JObject?>(new JObject { ["status"] = "ok" });
            });

            Assert.NotNull(res);
            Assert.True((bool)res["_meta"]?["dryRun"]!);
        }

        [Fact]
        public async Task ResponseCompactionMiddleware_StripsEmptyFields_WhenCompactTrue()
        {
            var pipeline = new McpMiddlewarePipeline();
            pipeline.Use(new ResponseCompactionMiddleware());

            var context = new McpPipelineContext(new JObject
            {
                ["params"] = new JObject
                {
                    ["name"] = "genexus_list_objects",
                    ["arguments"] = new JObject
                    {
                        ["compact"] = true
                    }
                }
            });

            var res = await pipeline.ExecuteAsync(context, ctx =>
            {
                return Task.FromResult<JObject?>(new JObject
                {
                    ["results"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "Customer",
                            ["description"] = null,
                            ["extra"] = ""
                        }
                    }
                });
            });

            Assert.NotNull(res);
            var item = res["results"]?[0] as JObject;
            Assert.NotNull(item);
            Assert.Equal("Customer", item["name"]?.ToString());
            Assert.Null(item["description"]);
            Assert.Null(item["extra"]);
        }

        private class TestStepMiddleware : IMcpMiddleware
        {
            private readonly string _name;
            private readonly System.Collections.Generic.List<string> _order;

            public TestStepMiddleware(string name, System.Collections.Generic.List<string> order)
            {
                _name = name;
                _order = order;
            }

            public async Task<JObject?> InvokeAsync(McpPipelineContext context, McpPipelineNextDelegate next)
            {
                _order.Add($"{_name}-Before");
                var res = await next().ConfigureAwait(false);
                _order.Add($"{_name}-After");
                return res;
            }
        }
    }
}
