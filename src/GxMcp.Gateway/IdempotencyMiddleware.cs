using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class IdempotencyMiddleware : GxMcp.Gateway.Pipelines.IMcpMiddleware
    {
        private static readonly HashSet<string> WriteTools = new HashSet<string>
        {
            "genexus_edit", "genexus_create_object", "genexus_delete_object", "genexus_refactor",
            "genexus_forge", "genexus_import_object"
        };

        private readonly IdempotencyCache _cache;
        private readonly string _kbPath;

        public IdempotencyMiddleware(IdempotencyCache cache, string kbPath)
        {
            _cache = cache;
            _kbPath = kbPath;
        }

        public async Task<JObject?> InvokeAsync(GxMcp.Gateway.Pipelines.McpPipelineContext context, GxMcp.Gateway.Pipelines.McpPipelineNextDelegate next)
        {
            var toolCall = new JObject
            {
                ["name"] = context.ToolName,
                ["arguments"] = context.Arguments
            };

            var res = await Invoke(toolCall, async tc =>
            {
                var n = await next().ConfigureAwait(false);
                return n ?? new JObject();
            }).ConfigureAwait(false);

            return res;
        }

        public async Task<JObject> Invoke(JObject toolCall, Func<JObject, Task<JObject>> next)
        {
            var tool = toolCall["name"]?.ToString() ?? "";
            if (!WriteTools.Contains(tool)) return await next(toolCall).ConfigureAwait(false);

            var args = toolCall["arguments"] as JObject ?? new JObject();
            var key = args["idempotencyKey"]?.ToString();
            if (string.IsNullOrEmpty(key)) return await next(toolCall).ConfigureAwait(false);
            ValidateKey(key);

            var dryRun = args["dryRun"]?.ToObject<bool?>() ?? false;
            if (dryRun) return await next(toolCall).ConfigureAwait(false);

            var hash = HashPayload(args);

            bool computed = false;
            var result = await _cache.GetOrCompute(_kbPath, tool, key, hash,
                async () =>
                {
                    computed = true;
                    var raw = await next(toolCall).ConfigureAwait(false);
                    if ((bool?)raw["isError"] == true)
                        throw new ErrorNotCacheable(raw);
                    return raw;
                }).ConfigureAwait(false);

            if (!computed)
            {
                var clone = (JObject)result.DeepClone();
                clone["meta"] = clone["meta"] as JObject ?? new JObject();
                ((JObject)clone["meta"]!)["idempotent"] = true;
                return clone;
            }
            return result;
        }

        private static void ValidateKey(string key)
        {
            if (key.Length < 1 || key.Length > 128)
                throw new UsageException("usage_error", "idempotencyKey length must be 1..128");
            foreach (var c in key)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                    throw new UsageException("usage_error",
                        "idempotencyKey charset must be [A-Za-z0-9_-]");
        }

        private static string HashPayload(JObject args)
        {
            var sorted = JsonCanonicalize(args, isRoot: true);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sorted)));
        }

        private static string JsonCanonicalize(JToken t, bool isRoot = false)
        {
            if (t is JObject o)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                bool first = true;
                foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (isRoot && (p.Name == "idempotencyKey" || p.Name == "dryRun"))
                        continue;

                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonCanonicalize(p.Name)).Append(':').Append(JsonCanonicalize(p.Value, isRoot: false));
                }
                sb.Append('}');
                return sb.ToString();
            }
            if (t is JArray a)
            {
                var sb = new StringBuilder();
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonCanonicalize(a[i], isRoot: false));
                }
                sb.Append(']');
                return sb.ToString();
            }
            if (t is JValue v) return JsonConvert.SerializeObject(v.Value);
            return JsonConvert.SerializeObject(t.ToString());
        }

        private static string JsonCanonicalize(string s) => JsonConvert.SerializeObject(s);
    }
}
