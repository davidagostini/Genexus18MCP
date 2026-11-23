using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GxMcpHub;

// The presence of this Hub on the endpoints it is connected to. For each endpoint the Hub keeps a file of its own open
// without sharing, in a folder named after the endpoint: the same hash as the startup lease, in another folder on
// purpose, because whoever promotes watches backend-startup\*.lock and would take a presence for a start in progress.
//
// Another Hub is connected to an endpoint when that folder holds a presence file, other than this one's, that cannot
// be opened. A file that opens belongs to a Hub that is gone (the system let go of it when the process ended): it is
// deleted and does not count. The Hub that started a Gateway asks this on its way out, to leave the Gateway running
// when someone else is using it.
internal sealed class HubPresence : IDisposable
{
    internal const string FolderName = "backend-presence";
    internal const string Extension = ".presence";
    private const int ErrorSharingViolation = 32;
    private const int EnterAttempts = 6;
    private const int ProbeAttempts = 3;
    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(60);
    private static int _instances;
    private static long _lastWarningTicks;

    private readonly object _gate = new();
    private readonly Dictionary<string, FileStream> _held = new(StringComparer.Ordinal);
    private readonly string _root;
    private long _entries;
    private bool _disposed;

    public HubPresence(string? root = null)
    {
        _root = root ?? Path.Combine(Path.GetTempPath(), "GxMcpHub", FolderName);
        Identity = NewIdentity();
    }

    // An identifier of the TEMP folder this Hub works in, to compare Hubs of different sessions without showing the
    // path: Hubs see each other (startup lease and presence) only when it is the same. SHA-256, in UTF-8, of the full
    // path (Path.GetFullPath, which also expands a TEMP spelled with 8.3 short names) without the trailing separator
    // and in upper case; first 12 hex digits, in lower case. Another spelling of the same folder gives the same
    // identifier.
    internal static string TempRootId()
    {
        var path = Path.GetTempPath();
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or
                                       System.Security.SecurityException)
        {
            // The path as it is.
        }

        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();
    }

    // <pid>-<creation instant of this process>: a process id alone is reused by the system. A second presence in the
    // same process, which only the self-tests create, gets a number of its own.
    public string Identity { get; }

    // How many times a presence file was taken. The self-tests read it to prove that a reconnection kept the one it had.
    internal long Entries => Interlocked.Read(ref _entries);

    internal string FolderFor(Uri endpoint) => Path.Combine(_root, KeyOf(endpoint));

    internal string FileFor(Uri endpoint) => Path.Combine(FolderFor(endpoint), Identity + Extension);

    public bool IsHeld(Uri endpoint)
    {
        lock (_gate)
        {
            return _held.ContainsKey(KeyOf(endpoint));
        }
    }

    // Takes the presence of this Hub on the endpoint and keeps it until Leave. False when the file could not be taken:
    // the Hub then works as it did before, without being seen by the others.
    public bool TryEnter(Uri endpoint)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var key = KeyOf(endpoint);
            if (_held.ContainsKey(key))
            {
                return true;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(FolderFor(endpoint));
                    // Without sharing: nobody else opens it while this Hub lives. Deleted with the handle, so a Hub
                    // that leaves, or is killed, leaves no file behind.
                    _held[key] = new FileStream(
                        FileFor(endpoint),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.DeleteOnClose);
                    Interlocked.Increment(ref _entries);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The folder was removed by a Hub that had just left it, or someone had the file open for an
                    // instant. Anything that lasts is reported and the Hub goes on without presence.
                    if (attempt >= EnterAttempts)
                    {
                        Warn("a presença deste Hub não pôde ser registrada", ex);
                        return false;
                    }

                    Thread.Sleep(20);
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException
                                               or System.Security.SecurityException)
                {
                    Warn("a presença deste Hub não pôde ser registrada", ex);
                    return false;
                }
            }
        }
    }

    public void Leave(Uri endpoint)
    {
        FileStream? stream;
        lock (_gate)
        {
            if (!_held.Remove(KeyOf(endpoint), out stream))
            {
                return;
            }
        }

        Release(stream, FolderFor(endpoint));
    }

    // Lets go of the presence on every endpoint that is not in the list: the ones a reloaded profile no longer has.
    public void RetainOnly(IEnumerable<Uri> endpoints)
    {
        var keep = new HashSet<string>(endpoints.Select(KeyOf), StringComparer.Ordinal);
        List<(FileStream Stream, string Folder)> released = new();
        lock (_gate)
        {
            foreach (var key in _held.Keys.Where(key => !keep.Contains(key)).ToArray())
            {
                if (_held.Remove(key, out var stream))
                {
                    released.Add((stream, Path.Combine(_root, key)));
                }
            }
        }

        foreach (var (stream, folder) in released)
        {
            Release(stream, folder);
        }
    }

    // How many other Hubs are connected to the endpoint. Null when it cannot be told: the folder or one of the files
    // could not be examined. Whoever decides on that answer must take "cannot be told" for "someone may be there".
    public int? CountOthers(Uri endpoint)
    {
        var folder = FolderFor(endpoint);
        var own = Identity + Extension;
        string[] files;
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            files = Directory.GetFiles(folder, "*" + Extension);
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var count = 0;
        var unknown = false;
        foreach (var file in files)
        {
            if (Path.GetFileName(file).Equals(own, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (Probe(file))
            {
                case ProbeResult.Held:
                    count++;
                    break;
                case ProbeResult.Unknown:
                    unknown = true;
                    break;
            }
        }

        return unknown ? null : count;
    }

    private static ProbeResult Probe(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A reader that shares everything is only refused when someone holds the file without sharing.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                }

                // It opened: the Hub that created it is gone.
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another Hub is looking at the same file; one of the two removes it.
                }

                return ProbeResult.Gone;
            }
            catch (FileNotFoundException)
            {
                return ProbeResult.Gone;
            }
            catch (DirectoryNotFoundException)
            {
                return ProbeResult.Gone;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == ErrorSharingViolation)
            {
                return ProbeResult.Held;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file on its way out answers "access denied" for an instant; one that keeps answering it cannot
                // be examined.
                if (attempt >= ProbeAttempts)
                {
                    return ProbeResult.Unknown;
                }

                Thread.Sleep(20);
            }
        }
    }

    public void Dispose()
    {
        List<(FileStream Stream, string Folder)> released = new();
        lock (_gate)
        {
            _disposed = true;
            foreach (var pair in _held)
            {
                released.Add((pair.Value, Path.Combine(_root, pair.Key)));
            }

            _held.Clear();
        }

        foreach (var (stream, folder) in released)
        {
            Release(stream, folder);
        }
    }

    private static void Release(FileStream stream, string folder)
    {
        try
        {
            stream.Dispose();
        }
        catch (IOException)
        {
        }

        try
        {
            // Only succeeds when no other Hub has a file there.
            Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string KeyOf(Uri endpoint) => BackendStartupLease.HashOf(new[] { endpoint });

    private static string NewIdentity()
    {
        using var self = Process.GetCurrentProcess();
        var identity = $"{Environment.ProcessId}-{self.StartTime.ToUniversalTime().Ticks}";
        var instance = Interlocked.Increment(ref _instances);
        return instance == 1 ? identity : $"{identity}-{instance}";
    }

    // Said at most once a minute, with the reason and never a path.
    private static void Warn(string what, Exception ex)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if (now - last < WarningInterval.Ticks || Interlocked.CompareExchange(ref _lastWarningTicks, now, last) != last)
        {
            return;
        }

        Console.Error.WriteLine($"[hub] {what} ({ex.GetType().Name}); os outros Hubs não o enxergam conectado.");
    }

    private enum ProbeResult
    {
        Held,
        Gone,
        Unknown
    }
}
