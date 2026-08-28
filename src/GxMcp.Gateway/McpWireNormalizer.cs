using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Deep wire envelope normalizer ensuring JSON-RPC 2.0 and MCP protocol standard conformity.
    /// </summary>
    public static class McpWireNormalizer
    {
        public static JObject CreateResponse(JToken? id, JToken result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result
            };
        }

        public static JObject CreateError(JToken? id, int code, string message, JToken? data = null)
        {
            var err = new JObject
            {
                ["code"] = code,
                ["message"] = message
            };
            if (data != null) err["data"] = data;

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = err
            };
        }

        public static JObject CreateToolResult(JToken? id, string textContent, bool isError = false)
        {
            var contentArr = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = textContent ?? string.Empty
                }
            };

            var result = new JObject
            {
                ["content"] = contentArr,
                ["isError"] = isError
            };

            return CreateResponse(id, result);
        }
    }
}
