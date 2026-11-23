using System.Text.Json;

namespace GxMcpHub;

internal static class Doctor
{
    public static async Task<int> RunAsync(
        LoadedHubConfig loaded,
        bool startBackends,
        CancellationToken cancellationToken)
    {
        await using var hub = new HubService(loaded);
        await hub.RefreshAsync(startBackends, notify: false, cancellationToken);
        Console.WriteLine(hub.StatusJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var failedRequiredBackend = loaded.Config.Backends.Any(
            backend => backend.Enabled &&
                       hub.StatusJson()["backends"]?.AsArray()
                           .OfType<System.Text.Json.Nodes.JsonObject>()
                           .Any(status =>
                               status["id"]?.GetValue<string>() == backend.Id &&
                               status["online"]?.GetValue<bool>() != true) == true);

        return failedRequiredBackend ? 1 : 0;
    }
}
