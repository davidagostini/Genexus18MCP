using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Encapsulates the execution context for a single Worker command.
    /// Eliminates the brittle 6-positional-argument signature across worker handlers.
    /// </summary>
    public sealed class CommandContext
    {
        public JObject Request { get; set; }
        public string Method { get; set; }
        public string Action { get; set; }
        public string Target { get; set; }
        public string Payload { get; set; }
        public JObject Args { get; set; }
        public string ToolName { get; set; }

        public CommandContext(JObject request, string method, string action, string target, string payload, JObject args, string toolName = null)
        {
            Request = request;
            Method = method ?? string.Empty;
            Action = action ?? string.Empty;
            Target = target ?? string.Empty;
            Payload = payload;
            Args = args ?? request ?? new JObject();
            ToolName = toolName;
        }

        public string ArgStr(string key, string fallback = null)
        {
            if (Args == null || !Args.TryGetValue(key, out var token) || token == null || token.Type == JTokenType.Null)
                return fallback;
            string str = token.ToString();
            return string.IsNullOrEmpty(str) ? fallback : str;
        }

        public int? ArgInt(string key)
        {
            if (Args == null || !Args.TryGetValue(key, out var token) || token == null || token.Type == JTokenType.Null)
                return null;
            return token.ToObject<int?>();
        }

        public bool ArgBool(string key, bool fallback = false)
        {
            if (Args == null || !Args.TryGetValue(key, out var token) || token == null || token.Type == JTokenType.Null)
                return fallback;
            return token.ToObject<bool?>() ?? fallback;
        }
    }

    /// <summary>
    /// Deep Command Handler Registry for GxMcp.Worker.
    /// Replaces monolithic eager initialization with lazy, modular command resolution.
    /// Supports direct canonical tool name dispatch ({tool, arguments}) and legacy ({module, action}).
    /// </summary>
    public sealed class CommandHandlerRegistry
    {
        public delegate string CommandHandlerFunc(CommandContext context);

        private sealed class LazyHandlerEntry
        {
            private readonly Func<object> _factory;
            private readonly Func<object, CommandContext, string> _invoker;
            private readonly object _lock = new object();
            private object _instance;
            private bool _instantiated;

            public LazyHandlerEntry(Func<object> factory, Func<object, CommandContext, string> invoker)
            {
                _factory = factory ?? throw new ArgumentNullException(nameof(factory));
                _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            }

            public string Invoke(CommandContext context)
            {
                if (!_instantiated)
                {
                    lock (_lock)
                    {
                        if (!_instantiated)
                        {
                            _instance = _factory();
                            _instantiated = true;
                        }
                    }
                }
                return _invoker(_instance, context);
            }
        }

        private readonly ConcurrentDictionary<string, CommandHandlerFunc> _directHandlers =
            new ConcurrentDictionary<string, CommandHandlerFunc>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, LazyHandlerEntry> _lazyHandlers =
            new ConcurrentDictionary<string, LazyHandlerEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, CommandHandlerFunc> _toolHandlers =
            new ConcurrentDictionary<string, CommandHandlerFunc>(StringComparer.OrdinalIgnoreCase);

        public void Register(string methodOrModule, CommandHandlerFunc handler)
        {
            if (string.IsNullOrWhiteSpace(methodOrModule)) throw new ArgumentNullException(nameof(methodOrModule));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _directHandlers[methodOrModule] = handler;
        }

        public void RegisterLazy<TService>(string methodOrModule, Func<TService> factory, Func<TService, CommandContext, string> invoker)
            where TService : class
        {
            if (string.IsNullOrWhiteSpace(methodOrModule)) throw new ArgumentNullException(nameof(methodOrModule));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (invoker == null) throw new ArgumentNullException(nameof(invoker));

            _lazyHandlers[methodOrModule] = new LazyHandlerEntry(
                () => factory(),
                (inst, ctx) => invoker((TService)inst, ctx));
        }

        public void RegisterTool(string toolName, CommandHandlerFunc handler)
        {
            if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentNullException(nameof(toolName));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _toolHandlers[toolName] = handler;
        }

        public bool TryDispatch(CommandContext context, out string result)
        {
            result = null;
            if (context == null) return false;

            // 1. Direct tool dispatch by canonical tool name (e.g. genexus_read, genexus_edit)
            if (!string.IsNullOrEmpty(context.ToolName) && _toolHandlers.TryGetValue(context.ToolName, out var toolHandler))
            {
                result = toolHandler(context);
                return result != null;
            }

            // Also check if Method or Action itself contains a tool name
            if (!string.IsNullOrEmpty(context.Method) && _toolHandlers.TryGetValue(context.Method, out var mToolHandler))
            {
                result = mToolHandler(context);
                return result != null;
            }

            // 2. Direct method/module handler lookup
            if (!string.IsNullOrEmpty(context.Method))
            {
                if (_directHandlers.TryGetValue(context.Method, out var directHandler))
                {
                    string handlerResult = directHandler(context);
                    if (handlerResult != null)
                    {
                        result = handlerResult;
                        return true;
                    }
                }

                // 3. Lazy handler lookup
                if (_lazyHandlers.TryGetValue(context.Method, out var lazyHandler))
                {
                    string lazyResult = lazyHandler.Invoke(context);
                    if (lazyResult != null)
                    {
                        result = lazyResult;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}