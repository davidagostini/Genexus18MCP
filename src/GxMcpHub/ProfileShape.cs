namespace GxMcpHub;

// What the automatic reload may apply on its own: a change confined to backends[].startup (command, arguments,
// working directory, environment, startup timeout), which is what a promotion rewrites. Any other difference between
// the loaded profile and the one on disk (endpoint, tool prefix, read-only tools, policy, timeouts, the list of
// backends, a display name) waits for gxhub_refresh: in a read-only profile, widening readOnlyTools on disk must
// not come into force in ten seconds without a deliberate act.
internal static class ProfileShape
{
    public static bool DiffersOnlyInStartup(HubConfig current, HubConfig next) =>
        OutsideStartup(current).SequenceEqual(OutsideStartup(next), StringComparer.Ordinal);

    // Every value of a profile that is not under backends[].startup, in a fixed order.
    private static IEnumerable<string> OutsideStartup(HubConfig config)
    {
        yield return "profileName=" + config.ProfileName;
        yield return "workspaceRoot=" + NormalizePath(config.WorkspaceRoot);
        yield return "knowledgeBasePath=" + NormalizePath(config.KnowledgeBasePath);
        yield return "connectTimeoutSeconds=" + config.ConnectTimeoutSeconds;
        yield return "callTimeoutSeconds=" + config.CallTimeoutSeconds;
        yield return "writePolicy.mode=" + config.WritePolicy.Mode.ToLowerInvariant();
        yield return "writePolicy.writerBackend=" + (config.WritePolicy.WriterBackend ?? string.Empty).ToLowerInvariant();
        yield return "writePolicy.hideBlockedTools=" + config.WritePolicy.HideBlockedTools;
        yield return "backends=" + config.Backends.Count;
        foreach (var backend in config.Backends)
        {
            yield return "id=" + backend.Id;
            yield return "displayName=" + backend.DisplayName;
            yield return "kind=" + backend.Kind;
            yield return "enabled=" + backend.Enabled;
            yield return "toolPrefix=" + backend.ToolPrefix;
            yield return "protocolVersion=" + backend.ProtocolVersion;
            yield return "endpoint=" + backend.Endpoint;
            yield return "bearerTokenEnvVar=" + backend.BearerTokenEnvVar;
            yield return "useRemoteReadOnlyAnnotations=" + backend.UseRemoteReadOnlyAnnotations;
            yield return "readOnlyTools=" + backend.ReadOnlyTools.Count + ":" + string.Join("\n", backend.ReadOnlyTools);
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
}
