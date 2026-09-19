using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public sealed class ToolSchemaCompatibilityTests
{
    [Fact]
    public void Apply_PreservesLegacyNameAliasForAtomicCreateVariables()
    {
        var definitions = JArray.Parse("""
            [{
              "name": "genexus_create",
              "inputSchema": {
                "properties": {
                  "variables": {
                    "items": {
                      "required": ["varName"],
                      "properties": {
                        "name": { "type": "string" },
                        "varName": { "type": "string" }
                      }
                    }
                  }
                }
              }
            }]
            """);

        ToolSchemaCompatibility.Apply(definitions);

        Assert.Null(definitions[0]?["inputSchema"]?["properties"]?["variables"]?["items"]?["required"]);
        Assert.NotNull(definitions[0]?["inputSchema"]?["properties"]?["variables"]?["items"]?["properties"]?["name"]);
        Assert.NotNull(definitions[0]?["inputSchema"]?["properties"]?["variables"]?["items"]?["properties"]?["varName"]);
    }
}
