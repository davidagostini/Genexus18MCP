using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Pipelines
{
    /// <summary>
    /// The ordered, gateway-owned stages surrounding the legacy dispatch core.
    /// Stages deliberately do not manufacture MCP responses: the core remains
    /// the single source of truth for envelopes while this pipeline makes the
    /// request lifecycle and its ordering explicit.
    /// </summary>
    public static class RequestLoopStages
    {
        public static readonly string[] Names =
        {
            "protocol", "kb-resolution", "args-validation", "idempotency",
            "semantic-cache", "worker-dispatch", "response-shaping"
        };

        public static McpMiddlewarePipeline Create()
        {
            return new McpMiddlewarePipeline()
                .Use(new ProtocolHandshakeMiddleware())
                .Use(new KbResolutionMiddleware())
                .Use(new ArgsValidationMiddleware())
                .Use(new IdempotencyStageMiddleware())
                .Use(new SemanticCacheMiddleware())
                .Use(new WorkerDispatchMiddleware())
                .Use(new ResponseShapingMiddleware());
        }
    }

    public abstract class RequestLoopStageMiddleware : IMcpMiddleware
    {
        private readonly string _propertyKey;

        protected RequestLoopStageMiddleware(string stageName)
        {
            StageName = stageName;
            _propertyKey = "requestLoop.stage." + stageName;
        }

        protected string StageName { get; }

        public Task<JObject?> InvokeAsync(McpPipelineContext context, McpPipelineNextDelegate next)
        {
            context.Properties[_propertyKey] = true;
            return next();
        }
    }

    public sealed class ProtocolHandshakeMiddleware : RequestLoopStageMiddleware
    {
        public ProtocolHandshakeMiddleware() : base("protocol") { }
    }

    public sealed class KbResolutionMiddleware : RequestLoopStageMiddleware
    {
        public KbResolutionMiddleware() : base("kb-resolution") { }
    }

    public sealed class ArgsValidationMiddleware : RequestLoopStageMiddleware
    {
        public ArgsValidationMiddleware() : base("args-validation") { }
    }

    public sealed class IdempotencyStageMiddleware : RequestLoopStageMiddleware
    {
        public IdempotencyStageMiddleware() : base("idempotency") { }
    }

    public sealed class SemanticCacheMiddleware : RequestLoopStageMiddleware
    {
        public SemanticCacheMiddleware() : base("semantic-cache") { }
    }

    public sealed class WorkerDispatchMiddleware : RequestLoopStageMiddleware
    {
        public WorkerDispatchMiddleware() : base("worker-dispatch") { }
    }

    public sealed class ResponseShapingMiddleware : RequestLoopStageMiddleware
    {
        public ResponseShapingMiddleware() : base("response-shaping") { }
    }
}
