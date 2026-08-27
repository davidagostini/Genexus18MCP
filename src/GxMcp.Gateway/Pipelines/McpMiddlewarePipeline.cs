using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Pipelines
{
    public delegate Task<JObject?> McpPipelineNextDelegate();

    public sealed class McpPipelineContext
    {
        public JObject Request { get; set; }
        public string SessionId { get; set; }
        public string ToolName { get; set; }
        public JObject Arguments { get; set; }
        public string KbAlias { get; set; }
        public bool IsDryRun { get; set; }
        public JObject? Response { get; set; }
        public Dictionary<string, object?> Properties { get; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        public JToken? Id => Request?["id"];

        public McpPipelineContext(JObject? request, string sessionId = "")
        {
            Request = request ?? new JObject();
            SessionId = sessionId ?? string.Empty;

            var paramsObj = Request["params"] as JObject;
            ToolName = paramsObj?["name"]?.ToString() ?? Request["method"]?.ToString() ?? string.Empty;
            Arguments = (paramsObj?["arguments"] as JObject) ?? new JObject();
            KbAlias = Arguments["kb"]?.ToString() ?? string.Empty;
            IsDryRun = Arguments["dryRun"]?.ToObject<bool?>() ?? false;
        }
    }

    public interface IMcpMiddleware
    {
        Task<JObject?> InvokeAsync(McpPipelineContext context, McpPipelineNextDelegate next);
    }

    /// <summary>
    /// Deep Composable Middleware Pipeline for MCP Gateway Request Processing.
    /// Replaces monolithic procedural controller code with isolated, unit-testable middleware stages.
    /// Supports re-entrant and retry-safe middleware execution.
    /// </summary>
    public sealed class McpMiddlewarePipeline
    {
        private readonly List<IMcpMiddleware> _middlewares = new List<IMcpMiddleware>();

        public McpMiddlewarePipeline Use(IMcpMiddleware middleware)
        {
            if (middleware == null) throw new ArgumentNullException(nameof(middleware));
            _middlewares.Add(middleware);
            return this;
        }

        public async Task<JObject?> ExecuteAsync(McpPipelineContext context, Func<McpPipelineContext, Task<JObject?>> terminalHandler)
        {
            return await ExecuteMiddlewareAsync(0, context, terminalHandler).ConfigureAwait(false);
        }

        private async Task<JObject?> ExecuteMiddlewareAsync(int index, McpPipelineContext context, Func<McpPipelineContext, Task<JObject?>> terminalHandler)
        {
            if (index < _middlewares.Count)
            {
                return await _middlewares[index].InvokeAsync(context, () => ExecuteMiddlewareAsync(index + 1, context, terminalHandler)).ConfigureAwait(false);
            }
            return terminalHandler != null ? await terminalHandler(context).ConfigureAwait(false) : null;
        }
    }
}