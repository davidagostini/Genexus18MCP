using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XmlReader = System.Xml.XmlReader;
using XmlReaderSettings = System.Xml.XmlReaderSettings;
using DtdProcessing = System.Xml.DtdProcessing;
using XmlException = System.Xml.XmlException;

namespace GxMcp.Worker.Services
{
    public sealed partial class ObjectTextService
    {
        private static JObject BuildItemResult(SearchIndex.IndexEntry item, string path, JObject response)
        {
            var result = new JObject
            {
                ["name"] = item?.Name,
                ["type"] = item?.Type,
                ["path"] = path,
                ["response"] = response ?? new JObject { ["status"] = "error" }
            };
            return result;
        }

        private static JObject BuildItemResult(TextFileEntry item, string path, JObject response)
        {
            return new JObject
            {
                ["name"] = item?.Name,
                ["type"] = item?.Type,
                ["part"] = item?.Part,
                ["file"] = item?.File,
                ["path"] = path,
                ["response"] = response ?? new JObject { ["status"] = "error" }
            };
        }

        private static JObject BuildAggregateResult(string operation, string root, JArray results,
            int attempted, int succeeded, int failed)
        {
            var result = new JObject
            {
                ["operation"] = operation,
                ["attempted"] = attempted,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["results"] = results
            };
            if (!string.IsNullOrWhiteSpace(root)) result["root"] = root;
            return result;
        }

        private static string BuildCancelled(string operation, string root, JArray results,
            int succeeded, int failed, int attempted)
        {
            var result = BuildAggregateResult(operation, root, results, attempted, succeeded, failed);
            result["cancelled"] = true;
            result["remaining"] = Math.Max(0, attempted - results.Count);
            return McpResponse.Ok(code: "Cancelled", result: result);
        }

        private static JObject ParseResult(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Empty worker response."
                };
            }

            try
            {
                return JObject.Parse(raw);
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = ex.Message,
                    ["raw"] = raw
                };
            }
        }

        private static bool IsSuccess(JObject response)
        {
            string status = response?["status"]?.ToString();
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class TextFileEntry
        {
            public string Name;
            public string Type;
            public string Part;
            public string File;
            public string Module;
            public string Path;
        }
    }
}
