using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class OpenApiSdtField
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "Character";
        public bool Required { get; set; } = false;
        public string Description { get; set; } = string.Empty;
    }

    public class OpenApiSdtBlueprint
    {
        public string Name { get; set; } = string.Empty;
        public bool IsCollection { get; set; } = false;
        public string ItemType { get; set; } = string.Empty;
        public List<OpenApiSdtField> Fields { get; set; } = new List<OpenApiSdtField>();
    }

    public class OpenApiEndpointBlueprint
    {
        public string OperationId { get; set; } = string.Empty;
        public string HttpMethod { get; set; } = "POST";
        public string Path { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string RequestBodyType { get; set; } = string.Empty;
        public List<ApiIntrospectService.Parm> Parameters { get; set; } = new List<ApiIntrospectService.Parm>();
    }

    public class OpenApiImportBlueprint
    {
        public bool Success { get; set; } = true;
        public string ErrorMessage { get; set; } = string.Empty;
        public string ApiTitle { get; set; } = string.Empty;
        public string ApiVersion { get; set; } = "1.0.0";
        public string Description { get; set; } = string.Empty;
        public List<OpenApiSdtBlueprint> SdtsToCreate { get; set; } = new List<OpenApiSdtBlueprint>();
        public List<OpenApiEndpointBlueprint> EndpointsToCreate { get; set; } = new List<OpenApiEndpointBlueprint>();
    }

    public static class ApiOpenApiService
    {
        public static JObject ExportOpenApi(
            List<ApiIntrospectService.HttpEndpoint> endpoints,
            string title = "GeneXus KB API",
            string version = "1.0.0",
            string description = "REST APIs exposed by GeneXus Procedures and API Objects")
        {
            var doc = new JObject
            {
                ["openapi"] = "3.0.3",
                ["info"] = new JObject
                {
                    ["title"] = string.IsNullOrWhiteSpace(title) ? "GeneXus KB API" : title,
                    ["version"] = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                    ["description"] = description ?? string.Empty
                },
                ["paths"] = new JObject(),
                ["components"] = new JObject
                {
                    ["schemas"] = new JObject()
                }
            };

            var pathsObj = (JObject)doc["paths"];
            var schemasObj = (JObject)doc["components"]["schemas"];

            if (endpoints != null)
            {
                foreach (var ep in endpoints)
                {
                    string rawPath = ep.Path ?? "/" + ep.Name;
                    string pathKey = rawPath.StartsWith("/") ? rawPath : "/" + rawPath;

                    if (!pathsObj.ContainsKey(pathKey))
                    {
                        pathsObj[pathKey] = new JObject();
                    }

                    var pathItem = (JObject)pathsObj[pathKey];
                    string methodKey = (ep.HttpMethod ?? "POST").ToLowerInvariant();

                    var op = new JObject
                    {
                        ["operationId"] = ep.Name,
                        ["summary"] = $"Executes GeneXus procedure {ep.Name}",
                        ["responses"] = new JObject
                        {
                            ["200"] = new JObject
                            {
                                ["description"] = "Successful execution",
                                ["content"] = new JObject
                                {
                                    ["application/json"] = new JObject
                                    {
                                        ["schema"] = new JObject { ["type"] = "object" }
                                    }
                                }
                            }
                        }
                    };

                    var paramsArr = new JArray();
                    string requestBodySchemaRef = null;

                    if (ep.Parms != null)
                    {
                        foreach (var p in ep.Parms)
                        {
                            if (string.Equals(p.Direction, "in", StringComparison.OrdinalIgnoreCase))
                            {
                                if (p.Type != null && p.Type.StartsWith("SDT:", StringComparison.OrdinalIgnoreCase))
                                {
                                    string sdtName = p.Type.Substring(4);
                                    requestBodySchemaRef = $"#/components/schemas/{sdtName}";
                                    if (!schemasObj.ContainsKey(sdtName))
                                    {
                                        schemasObj[sdtName] = new JObject
                                        {
                                            ["type"] = "object",
                                            ["description"] = $"GeneXus SDT: {sdtName}"
                                        };
                                    }
                                }
                                else
                                {
                                    bool isPath = pathKey.Contains("{" + p.Name + "}");
                                    paramsArr.Add(new JObject
                                    {
                                        ["name"] = p.Name,
                                        ["in"] = isPath ? "path" : "query",
                                        ["required"] = isPath,
                                        ["schema"] = MapGeneXusTypeToOpenApiSchema(p.Type)
                                    });
                                }
                            }
                        }
                    }

                    if (paramsArr.Count > 0)
                    {
                        op["parameters"] = paramsArr;
                    }

                    if (!string.IsNullOrEmpty(requestBodySchemaRef))
                    {
                        op["requestBody"] = new JObject
                        {
                            ["required"] = true,
                            ["content"] = new JObject
                            {
                                ["application/json"] = new JObject
                                {
                                    ["schema"] = new JObject { ["$ref"] = requestBodySchemaRef }
                                }
                            }
                        };
                    }

                    pathItem[methodKey] = op;
                }
            }

            return doc;
        }

        public static OpenApiImportBlueprint ImportOpenApi(string rawJsonOrYaml)
        {
            var result = new OpenApiImportBlueprint();
            if (string.IsNullOrWhiteSpace(rawJsonOrYaml))
            {
                result.Success = false;
                result.ErrorMessage = "OpenAPI specification content is empty.";
                return result;
            }

            try
            {
                var doc = JObject.Parse(rawJsonOrYaml);
                result.ApiTitle = doc["info"]?["title"]?.ToString() ?? "Imported API";
                result.ApiVersion = doc["info"]?["version"]?.ToString() ?? "1.0.0";
                result.Description = doc["info"]?["description"]?.ToString();

                // 1. Parse schemas -> SDTs (OpenAPI 3.0 components.schemas OR Swagger 2.0 definitions)
                var schemas = (doc["components"]?["schemas"] ?? doc["definitions"]) as JObject;
                if (schemas != null)
                {
                    foreach (var prop in schemas.Properties())
                    {
                        string schemaName = prop.Name;
                        var schemaObj = prop.Value as JObject;
                        if (schemaObj == null) continue;

                        string type = schemaObj["type"]?.ToString() ?? "object";
                        if (type == "array")
                        {
                            string itemRef = schemaObj["items"]?["$ref"]?.ToString() ?? "";
                            string itemTypeName = itemRef.Split('/').LastOrDefault() ?? "Character";
                            result.SdtsToCreate.Add(new OpenApiSdtBlueprint
                            {
                                Name = schemaName,
                                IsCollection = true,
                                ItemType = itemTypeName
                            });
                        }
                        else
                        {
                            var sdt = new OpenApiSdtBlueprint { Name = schemaName, IsCollection = false };
                            var properties = schemaObj["properties"] as JObject;
                            var required = (schemaObj["required"] as JArray)?.Select(r => r.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();

                            if (properties != null)
                            {
                                foreach (var fieldProp in properties.Properties())
                                {
                                    var fObj = fieldProp.Value as JObject;
                                    string gxType = "Character";

                                    string refVal = fObj?["$ref"]?.ToString();
                                    if (!string.IsNullOrEmpty(refVal))
                                    {
                                        string refName = refVal.Split('/').LastOrDefault();
                                        gxType = $"SDT:{refName}";
                                    }
                                    else
                                    {
                                        string fType = fObj?["type"]?.ToString() ?? "string";
                                        gxType = MapOpenApiTypeToGeneXus(fType);
                                    }

                                    sdt.Fields.Add(new OpenApiSdtField
                                    {
                                        Name = fieldProp.Name,
                                        Type = gxType,
                                        Required = required.Contains(fieldProp.Name),
                                        Description = fObj?["description"]?.ToString() ?? string.Empty
                                    });
                                }
                            }
                            result.SdtsToCreate.Add(sdt);
                        }
                    }
                }

                // 2. Parse paths -> Endpoints
                var paths = doc["paths"] as JObject;
                if (paths != null)
                {
                    foreach (var pathProp in paths.Properties())
                    {
                        string path = pathProp.Name;
                        var pathObj = pathProp.Value as JObject;
                        if (pathObj == null) continue;

                        foreach (var methodProp in pathObj.Properties())
                        {
                            string method = methodProp.Name.ToUpperInvariant();
                            if (method != "GET" && method != "POST" && method != "PUT" && method != "DELETE" && method != "PATCH")
                                continue;

                            var opObj = methodProp.Value as JObject;
                            string opId = opObj?["operationId"]?.ToString() ?? $"{method}_{path.Replace("/", "_").Trim('_')}";
                            string summary = opObj?["summary"]?.ToString() ?? "";

                            var endpoint = new OpenApiEndpointBlueprint
                            {
                                OperationId = opId,
                                HttpMethod = method,
                                Path = path,
                                Summary = summary
                            };

                            var parameters = opObj?["parameters"] as JArray;
                            if (parameters != null)
                            {
                                foreach (var p in parameters)
                                {
                                    string pName = p["name"]?.ToString() ?? "";
                                    string pType = p["schema"]?["type"]?.ToString() ?? p["type"]?.ToString() ?? "string";
                                    endpoint.Parameters.Add(new ApiIntrospectService.Parm
                                    {
                                        Name = pName,
                                        Direction = "in",
                                        Type = MapOpenApiTypeToGeneXus(pType)
                                    });
                                }
                            }

                            // Read requestBody for POST/PUT/PATCH
                            var reqBody = opObj?["requestBody"] as JObject;
                            var jsonSchema = reqBody?["content"]?["application/json"]?["schema"] as JObject;
                            if (jsonSchema != null)
                            {
                                string bodyRef = jsonSchema["$ref"]?.ToString();
                                if (!string.IsNullOrEmpty(bodyRef))
                                {
                                    string schemaName = bodyRef.Split('/').LastOrDefault();
                                    endpoint.RequestBodyType = $"SDT:{schemaName}";
                                    endpoint.Parameters.Add(new ApiIntrospectService.Parm
                                    {
                                        Name = $"&{schemaName}",
                                        Direction = "in",
                                        Type = $"SDT:{schemaName}"
                                    });
                                }
                            }

                            result.EndpointsToCreate.Add(endpoint);
                        }
                    }
                }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to parse OpenAPI document: " + ex.Message;
                return result;
            }
        }

        private static JObject MapGeneXusTypeToOpenApiSchema(string gxType)
        {
            string clean = (gxType ?? "Character").ToLowerInvariant();
            if (clean.StartsWith("num") || clean == "int")
                return new JObject { ["type"] = "integer" };
            if (clean == "boolean" || clean == "bool")
                return new JObject { ["type"] = "boolean" };
            if (clean.StartsWith("sdt:"))
            {
                string sdtName = gxType.Substring(4);
                return new JObject { ["$ref"] = $"#/components/schemas/{sdtName}" };
            }
            return new JObject { ["type"] = "string" };
        }

        private static string MapOpenApiTypeToGeneXus(string openApiType)
        {
            return (openApiType ?? "").ToLowerInvariant() switch
            {
                "integer" or "int32" or "int64" or "number" => "Numeric",
                "boolean" => "Boolean",
                "date" => "Date",
                "date-time" => "DateTime",
                _ => "Character"
            };
        }
    }
}
