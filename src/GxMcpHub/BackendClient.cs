using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace GxMcpHub;

internal sealed class BackendClient : IDisposable
{
    private readonly BackendConfig _config;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private long _nextId;
    private string? _sessionId;

    public BackendClient(
        BackendConfig config,
        Uri endpoint,
        int connectTimeoutSeconds,
        int callTimeoutSeconds,
        HttpMessageHandler? messageHandler = null)
    {
        _config = config;
        Endpoint = endpoint;
        _http = new HttpClient(messageHandler ?? new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds)
            })
        {
            Timeout = TimeSpan.FromSeconds(callTimeoutSeconds)
        };
    }

    public Uri Endpoint { get; }
    public JsonObject? ServerInfo { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        _sessionId = null;
        ServerInfo = null;

        var response = await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = NextId(),
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = _config.ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "GxMcpHub",
                    ["version"] = HubInfo.Version
                }
            }
        }, cancellationToken);

        ThrowIfRpcError(response);
        ServerInfo = response["result"]?["serverInfo"]?.DeepClone() as JsonObject;

        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
            ["params"] = new JsonObject()
        }, cancellationToken, allowEmptyResponse: true);
    }

    public async Task<IReadOnlyList<JsonObject>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var response = await SendWithSessionRecoveryAsync(
            () => new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = NextId(),
                ["method"] = "tools/list",
                ["params"] = new JsonObject()
            },
            cancellationToken);
        ThrowIfRpcError(response);

        var tools = response["result"]?["tools"] as JsonArray;
        if (tools is null)
        {
            throw new BackendRpcException(-32603, "Resposta tools/list sem result.tools.");
        }

        return tools.OfType<JsonObject>()
            .Select(tool => (JsonObject)tool.DeepClone())
            .ToArray();
    }

    public async Task<JsonNode?> CallToolAsync(
        string toolName,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var response = await SendWithSessionRecoveryAsync(
            () => new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = NextId(),
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = toolName,
                    ["arguments"] = arguments.DeepClone()
                }
            },
            cancellationToken);
        ThrowIfRpcError(response);
        return response["result"]?.DeepClone();
    }

    private async Task<JsonObject> SendWithSessionRecoveryAsync(
        Func<JsonObject> payloadFactory,
        CancellationToken cancellationToken)
    {
        var observedSessionId = _sessionId;
        try
        {
            var response = await SendAsync(payloadFactory(), cancellationToken);
            if (!IsExpiredSessionResponse(response))
            {
                return response;
            }
        }
        catch (BackendRpcException ex) when (IsExpiredSessionMessage(ex.Message))
        {
            // O backend também pode devolver a expiração como erro HTTP.
        }

        Console.Error.WriteLine(
            $"[{_config.Id}] sessão MCP do backend expirou; reconectando e repetindo a chamada uma vez.");
        await RefreshExpiredSessionAsync(observedSessionId, cancellationToken);
        return await SendAsync(payloadFactory(), cancellationToken);
    }

    private async Task RefreshExpiredSessionAsync(
        string? observedSessionId,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_sessionId) &&
                !string.Equals(_sessionId, observedSessionId, StringComparison.Ordinal))
            {
                return;
            }

            await InitializeCoreAsync(cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<JsonObject> SendAsync(
        JsonObject payload,
        CancellationToken cancellationToken,
        bool allowEmptyResponse = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _config.ProtocolVersion);
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            request.Headers.TryAddWithoutValidation("MCP-Session-Id", _sessionId);
        }

        if (!string.IsNullOrWhiteSpace(_config.BearerTokenEnvVar))
        {
            var token = Environment.GetEnvironmentVariable(_config.BearerTokenEnvVar);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new BackendRpcException(
                    -32001,
                    $"A variável de token '{_config.BearerTokenEnvVar}' não está definida.");
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("MCP-Session-Id", out var values))
        {
            _sessionId = values.FirstOrDefault() ?? _sessionId;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new BackendRpcException(
                (int)response.StatusCode,
                $"HTTP {(int)response.StatusCode} de {Endpoint}: {Truncate(body, 500)}");
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            if (allowEmptyResponse)
            {
                return new JsonObject();
            }
            throw new BackendRpcException(-32603, $"Resposta vazia de {Endpoint}.");
        }

        try
        {
            return JsonNode.Parse(body) as JsonObject
                   ?? throw new BackendRpcException(-32700, "Resposta JSON não é um objeto.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new BackendRpcException(-32700, $"Resposta JSON inválida: {ex.Message}");
        }
    }

    private static void ThrowIfRpcError(JsonObject response)
    {
        if (response["error"] is not JsonObject error)
        {
            return;
        }

        var code = error["code"]?.GetValue<int>() ?? -32603;
        var message = error["message"]?.GetValue<string>() ?? "Erro JSON-RPC sem mensagem.";
        throw new BackendRpcException(code, message);
    }

    private static bool IsExpiredSessionResponse(JsonObject response) =>
        response["error"] is JsonObject error &&
        IsExpiredSessionMessage(error["message"]?.GetValue<string>());

    private static bool IsExpiredSessionMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.Contains("Unknown or expired MCP session", StringComparison.OrdinalIgnoreCase);

    private long NextId() => Interlocked.Increment(ref _nextId);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Dispose()
    {
        _sessionGate.Dispose();
        _http.Dispose();
    }
}

internal sealed class BackendRpcException : Exception
{
    public BackendRpcException(int code, string message) : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}
