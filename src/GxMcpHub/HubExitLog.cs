using System.Text;

namespace GxMcpHub;

// A best-effort record of what each Hub decided about its Gateway on the way out, one line per backend, appended to
// <workspaceRoot>\GxMcpHub\runtime\hub-exit.log: UTC time, process id of the Hub, backend id, the decision, the process
// id of the process the Hub started, when the Gateway was kept how many other Hubs were connected and, last, how the
// Hub had started that process (mode=hub-tree, outside-job or hub-tree-fallback; absent when this Hub started nothing,
// the not-owner lines). No path, pin or profile value. It exists to learn from the machine itself whether the clients
// let the Hub run its normal exit at all: a client that terminates the Hub right after closing its input leaves no
// line. It never gets in the way of the exit: a failure to write is dropped. Past 256 KB the file becomes
// hub-exit.log.1, replacing the one that was there.
internal static class HubExitLog
{
    internal const long MaxBytes = 256 * 1024;

    internal static string PathFor(HubConfig config) =>
        Path.Combine(config.WorkspaceRoot, "GxMcpHub", "runtime", "hub-exit.log");

    internal static void Append(HubConfig config, int hubProcessId, string backendId, ShutdownDecision decision) =>
        Append(PathFor(config), Format(DateTime.UtcNow, hubProcessId, backendId, decision));

    // 2026-11-16T14:03:11.1230000Z hub=1234 backend=native decision=kept gateway=5678 others=1 mode=outside-job
    // 2026-11-16T14:03:11.1230000Z hub=1234 backend=native decision=stopped gateway=5678 mode=hub-tree
    // 2026-11-16T14:03:11.1230000Z hub=1234 backend=native decision=not-owner gateway=-
    internal static string Format(DateTime utcNow, int hubProcessId, string backendId, ShutdownDecision decision)
    {
        var line = new StringBuilder();
        line.Append(utcNow.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        line.Append(" hub=").Append(hubProcessId);
        line.Append(" backend=").Append(Sanitize(backendId));
        line.Append(" decision=").Append(decision.Decision);
        line.Append(" gateway=").Append(decision.GatewayProcessId is int gateway ? gateway.ToString() : "-");
        if (decision.Decision == BackendRuntime.DecisionKept)
        {
            line.Append(" others=").Append(decision.OthersConnected is int others ? others.ToString() : "?");
        }

        // The last field: a reader that takes decision=, gateway= and others= by pattern is not disturbed by it.
        if (decision.StartMode is { } startMode)
        {
            line.Append(" mode=").Append(Sanitize(startMode));
        }

        return line.ToString();
    }

    // The id comes from the profile: only what cannot carry a path or a space goes into the line.
    private static string Sanitize(string id)
    {
        var clean = new StringBuilder();
        foreach (var character in id.Take(32))
        {
            clean.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_');
        }

        return clean.Length == 0 ? "-" : clean.ToString();
    }

    internal static void Append(string path, string line, long maxBytes = MaxBytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= maxBytes)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Write(new UTF8Encoding(false).GetBytes(line + "\r\n"));
        }
        catch (Exception)
        {
            // Best effort: whatever goes wrong here is not the exit's problem.
        }
    }
}
