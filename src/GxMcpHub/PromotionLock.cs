using System.Security;

namespace GxMcpHub;

// The promotion lock. Whoever promotes the MCP keeps <workspaceRoot>\GxMcpHub\runtime\promotion.lock open without
// sharing, from before the profiles are rewritten until the Gateways are active again. The Hub never creates,
// writes or deletes that file: it only asks whether it is held, and only when it is about to start a backend
// process (nobody listens on the endpoint). A Hub that merely connects to a Gateway that already listens never
// asks. If the holder dies the operating system closes its handle, so a lock cannot be left behind.
internal static class PromotionLock
{
    internal const string Code = "promotion-in-progress";

    // What a caller and the backend status get: the code and a sentence, no path.
    internal const string Refusal =
        Code + ": há uma promoção do MCP em andamento e o backend não foi iniciado; repita quando ela terminar.";

    private const int ErrorSharingViolation = 32;
    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(60);
    private static readonly object WarningGate = new();
    private static string? _lastWarning;
    private static DateTime _lastWarningUtc;

    public static string PathFor(HubConfig config) =>
        Path.Combine(config.WorkspaceRoot, "GxMcpHub", "runtime", "promotion.lock");

    // Held means: the file exists and another process has it open in a way that refuses even a reader that shares
    // everything. A missing file, a file that opens, and any other failure all mean "no promotion": a folder
    // permission problem must never leave a Hub unable to start its backend.
    public static bool IsHeld(string path)
    {
        try
        {
            using var probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == ErrorSharingViolation)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or SecurityException)
        {
            Warn(ex);
            return false;
        }
    }

    private static void Warn(Exception ex)
    {
        var warning = ex.GetType().Name + ": " + ex.Message;
        lock (WarningGate)
        {
            var now = DateTime.UtcNow;
            if (warning == _lastWarning && now - _lastWarningUtc < WarningInterval)
            {
                return;
            }

            _lastWarning = warning;
            _lastWarningUtc = now;
        }

        Console.Error.WriteLine(
            "[hub] a trava de promoção não pôde ser consultada e foi tratada como livre: " + warning);
    }
}
