using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class AnalysisContext
    {
        public string Target { get; set; } = string.Empty;
        public string TypeFilter { get; set; }
        public string CodeSnippet { get; set; }
        public JObject Args { get; set; } = new JObject();
    }

    public interface IAnalysisModeHandler
    {
        string Mode { get; }
        string Handle(AnalysisContext context);
    }

    public interface IAnalysisEngine
    {
        void RegisterHandler(IAnalysisModeHandler handler);
        string Execute(string mode, AnalysisContext context);
        bool SupportsMode(string mode);
        IEnumerable<string> SupportedModes { get; }
    }

    public class AnalysisEngine : IAnalysisEngine
    {
        private readonly ConcurrentDictionary<string, IAnalysisModeHandler> _handlers =
            new ConcurrentDictionary<string, IAnalysisModeHandler>(StringComparer.OrdinalIgnoreCase);

        public void RegisterHandler(IAnalysisModeHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers[handler.Mode] = handler;
        }

        public bool SupportsMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return false;
            return _handlers.ContainsKey(mode.Trim());
        }

        public IEnumerable<string> SupportedModes => _handlers.Keys.OrderBy(k => k);

        public string Execute(string mode, AnalysisContext context)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                return McpResponse.Err(
                    code: "MissingAnalysisMode",
                    message: "Analysis mode is required.",
                    hint: $"Supported modes: {string.Join(", ", SupportedModes)}");
            }

            string normalizedMode = mode.Trim();
            if (_handlers.TryGetValue(normalizedMode, out var handler))
            {
                try
                {
                    return handler.Handle(context ?? new AnalysisContext());
                }
                catch (Exception ex)
                {
                    return McpResponse.Err(
                        code: "AnalysisExecutionFailed",
                        message: $"Handler for mode '{normalizedMode}' threw: {ex.Message}",
                        target: context?.Target);
                }
            }

            return McpResponse.Err(
                code: "UnknownAnalysisMode",
                message: $"Mode '{normalizedMode}' is not recognized.",
                hint: $"Available modes: {string.Join(", ", SupportedModes)}",
                target: context?.Target);
        }
    }
}
