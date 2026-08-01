using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class SearchRouter : IMcpModuleRouter
    {
        public string ModuleName => "Search";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_query":
                case "genexus_search":
                    string q = args?["query"]?.ToString() ?? args?["filter"]?.ToString() ?? "";
                    return new
                    {
                        module = "Search",
                        action = "Query",
                        target = q,
                        limit = args?["limit"]?.ToObject<int?>() ?? 50,
                        typeFilter = args?["typeFilter"]?.ToString() ?? args?["type"]?.ToString(),
                        domainFilter = args?["domainFilter"]?.ToString(),
                        exactMatch = args?["exactMatch"]?.ToObject<bool?>() ?? false,
                        inline_read_top = args?["inline_read_top"]?.ToObject<int?>() ?? 0,
                        // v2.6.8: temporal sort + bounds + stable cursor for query.
                        sort = args?["sort"]?.ToString(),
                        since = args?["since"]?.ToString(),
                        modifiedBefore = args?["modifiedBefore"]?.ToString(),
                        cursor = args?["cursor"]?.ToString(),
                    };
                case "genexus_search_source":
                    return new
                    {
                        module = "Search",
                        action = "SearchSource",
                        target = "",
                        callee = args?["callee"]?.ToString(),
                        pattern = args?["pattern"]?.ToString(),
                        typeFilter = args?["typeFilter"]?.ToString() ?? args?["type"]?.ToString(),
                        caseSensitive = args?["caseSensitive"]?.ToObject<bool?>() ?? false,
                        includeComments = args?["includeComments"]?.ToObject<bool?>() ?? false,
                        maxResults = args?["maxResults"]?.ToObject<int?>() ?? 50,
                        scope = args?["scope"],
                        argMatches = args?["argMatches"],
                        // Item 22: fields=[source,caption,description,parmNames]
                        fields = args?["fields"],
                        // issue #34: forward the scan-narrowing / resume knobs. Without these
                        // the worker never sees objectName, so the documented O(objects) fast
                        // path (and the Timeout resumeHint's startIndex/timeoutMs) were dead —
                        // every call fell back to a full O(KB) scan.
                        objectName = args?["objectName"]?.ToString(),
                        startIndex = args?["startIndex"]?.ToObject<int?>() ?? 0,
                        cursor = args?["cursor"]?.ToString(),
                        timeoutMs = args?["timeoutMs"]?.ToObject<int?>() ?? 30000
                    };
                case "genexus_list_objects":
                    return new
                    {
                        module = "List",
                        action = "Objects",
                        target = args?["filter"]?.ToString() ?? "",
                        limit = args?["limit"]?.ToObject<int?>() ?? 100,
                        offset = args?["offset"]?.ToObject<int?>() ?? 0,
                        parent = args?["parent"]?.ToString(),
                        parentPath = args?["parentPath"]?.ToString(),
                        typeFilter = args?["typeFilter"]?.ToString() ?? args?["type"]?.ToString(),
                        verbose = args?["verbose"]?.ToObject<bool?>() ?? false,
                        inline_read_top = args?["inline_read_top"]?.ToObject<int?>() ?? 0,
                        // v2.3.8 (Task 2.2): targeted discovery filters.
                        nameFilter = args?["nameFilter"]?.ToString(),
                        descriptionFilter = args?["descriptionFilter"]?.ToString(),
                        pathPrefix = args?["pathPrefix"]?.ToString(),
                        // v2.6.8: lifecycle sort + temporal filters + stable cursor.
                        sort = args?["sort"]?.ToString(),
                        since = args?["since"]?.ToString(),
                        modifiedBefore = args?["modifiedBefore"]?.ToString(),
                        cursor = args?["cursor"]?.ToString(),
                    };
                default:
                    return null;
            }
        }
    }
}
