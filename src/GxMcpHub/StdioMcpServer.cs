using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace GxMcpHub;

internal sealed class StdioMcpServer
{
    private const string ProtocolVersion = "2025-11-25";
    private readonly HubService _hub;
    private readonly SemaphoreSlim _stdoutGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private long _requestSequence;
    // The Hub can change its tool list on its own (profile reloaded, Gateway replaced). A notification is only
    // valid after the client's initialize was answered; before that the first tools/list already has the list.
    private volatile bool _clientInitialized;

    private readonly TextReader _input;
    private readonly TextWriter _output;

    // The transport is the process standard input and output; the streams are parameters only so that the
    // self-tests can drive the same code.
    public StdioMcpServer(HubService hub, TextReader? input = null, TextWriter? output = null)
    {
        _hub = hub;
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        _hub.ToolsChanged += OnToolsChanged;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var sequence = Interlocked.Increment(ref _requestSequence);
            var task = ProcessLineAsync(line, cancellationToken);
            _requests[sequence] = task;
            _ = task.ContinueWith(
                completedTask => _requests.TryRemove(sequence, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        if (_requests.Count > 0)
        {
            await Task.WhenAll(_requests.Values);
        }
        _hub.ToolsChanged -= OnToolsChanged;
        _stdoutGate.Dispose();
    }

    private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject? request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception ex)
        {
            await WriteAsync(ErrorResponse(null, -32700, "Parse error: " + ex.Message), cancellationToken);
            return;
        }

        if (request is null)
        {
            await WriteAsync(ErrorResponse(null, -32600, "Invalid Request"), cancellationToken);
            return;
        }

        var id = request["id"]?.DeepClone();
        var isNotification = request["id"] is null;
        var method = request["method"]?.GetValue<string>();

        try
        {
            JsonObject? response = method switch
            {
                "initialize" => SuccessResponse(id, InitializeResult(request)),
                "ping" => SuccessResponse(id, new JsonObject()),
                "tools/list" => SuccessResponse(id, ToolsListResult()),
                "tools/call" => SuccessResponse(
                    id,
                    await HandleToolCallAsync(request["params"] as JsonObject, cancellationToken)),
                "resources/list" => SuccessResponse(id, new JsonObject { ["resources"] = new JsonArray() }),
                "prompts/list" => SuccessResponse(id, new JsonObject { ["prompts"] = new JsonArray() }),
                "notifications/initialized" or "notifications/cancelled" => null,
                _ => isNotification ? null : ErrorResponse(id, -32601, "Method not found: " + method)
            };

            if (response is not null)
            {
                await WriteAsync(response, cancellationToken);
            }

            if (method == "initialize")
            {
                _clientInitialized = true;
            }
        }
        catch (Exception ex)
        {
            if (!isNotification)
            {
                await WriteAsync(ErrorResponse(id, -32603, ex.Message), cancellationToken);
            }
        }
    }

    private JsonObject InitializeResult(JsonObject request)
    {
        var clientVersion = request["params"]?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrWhiteSpace(clientVersion) ? ProtocolVersion : clientVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = true }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "GxMcpHub",
                ["version"] = HubInfo.Version
            },
            ["instructions"] =
                $"Perfil '{_hub.Config.ProfileName}' para a KB '{_hub.Config.KnowledgeBasePath}'. " +
                $"Política: {_hub.Config.WritePolicy.Mode}; escritor: " +
                $"{_hub.Config.WritePolicy.WriterBackend ?? "nenhum"}. " +
                "Comece com gxhub_status. Tools de backend usam prefixos para evitar colisões."
        };
    }

    private JsonObject ToolsListResult()
    {
        var array = new JsonArray();
        foreach (var tool in _hub.GetExposedToolDefinitions())
        {
            array.Add(tool);
        }
        return new JsonObject { ["tools"] = array };
    }

    private async Task<JsonNode?> HandleToolCallAsync(
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        if (string.IsNullOrWhiteSpace(name))
        {
            return HubService.ToolText("tools/call sem params.name.", isError: true);
        }

        return await _hub.CallAsync(name, arguments, cancellationToken);
    }

    private void OnToolsChanged()
    {
        if (!_clientInitialized)
        {
            return;
        }

        _ = NotifyToolsChangedAsync();
    }

    // Raised from a tool call or from the periodic reconciliation, so it can race the end of the transport: a
    // notification that can no longer be written is dropped, never an unobserved failure.
    private async Task NotifyToolsChangedAsync()
    {
        try
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/tools/list_changed"
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException)
        {
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _stdoutGate.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteLineAsync(message.ToJsonString());
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _stdoutGate.Release();
        }
    }

    private static JsonObject SuccessResponse(JsonNode? id, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result?.DeepClone()
    };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };
}
