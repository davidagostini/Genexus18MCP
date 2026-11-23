using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GxMcpHub;

internal sealed class ProcessSupervisor : IAsyncDisposable
{
    private readonly BackendConfig _backend;
    private Process? _process;

    public ProcessSupervisor(BackendConfig backend)
    {
        _backend = backend;
    }

    internal const string ProfileRevisionVariable = "GXMCP_HUB_PROFILE_REVISION";

    // First 12 hex digits of the SHA-256 of the profile JSON this Hub loaded. The Hub is the only authority on
    // which file it read, so it is added to the Gateway environment after the profile's own variables and cannot
    // be overridden from the profile. Kept for packages that still report it; the applied-versus-pending answer
    // is the Hub's own (gxhub_status backends[].gateway), from StartedFromProfileRevision below.
    public string? ProfileRevision { get; set; }

    // The revision the process this Hub started was started from. It never changes while that process lives.
    public string? StartedFromProfileRevision { get; private set; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        IntPtr process,
        IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    public bool StartedByHub => _process is { HasExited: false };

    // Whether the process this Hub started was put by the system in a job object: the job of the client application
    // that launched the Hub, which a child inherits. A Gateway in such a job can die when the client closes it
    // (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE), whatever this Hub decides. Null when it could not be told.
    public bool? StartedInJob => StartedByHub ? _startedInJob : null;

    private bool? _startedInJob;

    // How the process this Hub started was started (see StartModes); null when it has none. "outside-job" means the
    // detached launcher created it: no job, no channels of the Hub, no parent once the launcher left.
    public string? StartMode => StartedByHub ? _startMode : null;

    private string? _startMode;

    // The Hub-wide decision to leave the job of the client, and how. Null (a backend used on its own, the self-tests
    // that do not ask for it) is the usual start.
    internal OutsideJobStarter? OutsideJob { get; set; }

    // True when the process is in any job (a job of its own or an inherited one).
    internal static bool? IsInJob(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return IsProcessInJob(process.Handle, IntPtr.Zero, out var inJob) ? inJob : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or
                                       DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
    public int? ProcessId => StartedByHub ? _process!.Id : null;

    // When this Hub started the process that is alive now.
    public DateTime? StartedUtc => StartedByHub ? _startedUtc : null;

    private DateTime _startedUtc;

    // How many backend processes this Hub process has started so far, over every backend. The self-tests read it to
    // prove that something was started exactly once, or not at all.
    private static long _totalStarts;

    internal static long TotalStarts => Interlocked.Read(ref _totalStarts);

    internal static IReadOnlyDictionary<string, string> BuildStartupEnvironment(
        StartupConfig startup,
        string? profileRevision)
    {
        // Windows compares environment variable names without case, and the JSON loader hands over a dictionary that
        // does compare it, so a profile can repeat a name in another case. The copy constructor would throw on that;
        // assigning key by key lets the last one win, as ProcessStartInfo.Environment does.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in startup.Environment)
        {
            environment[pair.Key] = pair.Value;
        }

        if (!string.IsNullOrWhiteSpace(profileRevision))
        {
            environment[ProfileRevisionVariable] = profileRevision;
        }

        return environment;
    }

    // The synchronous form, for what has no async context (the self-tests, the test modes).
    public void Start() => StartAsync(CancellationToken.None).GetAwaiter().GetResult();

    // Starts the process of the backend. When the start is to leave the job of the client, the detached launcher does
    // it; any failure of that path ends in the usual start, said on stderr and in the start mode.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_backend.Startup is null || StartedByHub)
        {
            return;
        }

        string? fallbackReason = null;
        if (OutsideJob is { } outsideJob && await outsideJob.ShouldLeaveJobAsync(cancellationToken))
        {
            var watch = Stopwatch.StartNew();
            var launched = await outsideJob.TryStartAsync(_backend.Id, _backend.Startup, ProfileRevision, cancellationToken);
            var failure = launched.Failure ?? "unknown";
            if (launched.Process is { } process)
            {
                if (TryAdoptLaunchedProcess(process, watch.ElapsedMilliseconds))
                {
                    return;
                }

                failure = "adopt-failed";
            }

            fallbackReason = failure;
            Console.Error.WriteLine(
                $"[{_backend.Id}] início fora do job não concluído ({failure}); " +
                "o processo será iniciado pela árvore do Hub.");
        }

        StartProcess(
            BuildStartInfo(_backend.Startup, ProfileRevision),
            fallbackReason is null ? StartModes.HubTree : StartModes.HubTreeFallback,
            fallbackReason);
    }

    // Only the self-tests set this: it makes the adoption of a process that passed every check fail, to prove that the
    // process is stopped and not left behind.
    internal static Func<Process, bool>? AdoptionFailureForTests { get; set; }

    // The process the detached launcher started: the Hub never created it, so it is not a child of the Hub and its
    // output is not read (the Gateway writes to its own log files). From here on it is handled like any other
    // process this Hub started: stopped or let go on the way out, watched for its exit.
    private bool TryAdoptLaunchedProcess(Process process, long elapsedMilliseconds)
    {
        // The handler first: a process that has already ended raises the event as soon as it is enabled. One that
        // cannot be watched (it ended, or the system refuses) is not adopted: the start falls back.
        try
        {
            process.Exited += (_, _) => LogExit(process);
            process.EnableRaisingEvents = true;
            if (AdoptionFailureForTests?.Invoke(process) == true)
            {
                throw new InvalidOperationException("falha de adoção sintética (autoteste)");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process was checked as the one the launcher started: it is stopped (through the handle it was checked
            // with), not left on the port with nobody to own it while the Hub starts another one.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception killEx) when (killEx is InvalidOperationException or Win32Exception or
                                               NotSupportedException or AggregateException)
            {
                // Gone already, or not reachable: nothing to stop.
            }

            process.Dispose();
            return false;
        }

        _process?.Dispose();
        _process = null;
        _process = process;
        _startedUtc = DateTime.UtcNow;
        _startedInJob = IsInJob(process);
        _startMode = StartModes.OutsideJob;
        Interlocked.Increment(ref _totalStarts);
        StartedFromProfileRevision = ProfileRevision;
        Console.Error.WriteLine($"[{_backend.Id}] " + StartLine(process.Id, StartModes.OutsideJob, null, elapsedMilliseconds));
        return true;
    }

    // The handler reads the process it belongs to, never the field: the field may already hold a newer process.
    private void LogExit(Process process)
    {
        try
        {
            Console.Error.WriteLine($"[{_backend.Id}] processo finalizado com código {process.ExitCode}.");
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"[{_backend.Id}] processo finalizado.");
        }
    }

    internal static ProcessStartInfo BuildStartInfo(StartupConfig startup, string? profileRevision)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = startup.Command,
            Arguments = startup.Arguments,
            WorkingDirectory = startup.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            // The child gets a pipe of its own as standard input, which the Hub never writes to and only closes
            // with the process. Without it the child inherits the Hub's standard input, which is the MCP channel
            // of the client: besides never being the child's to read, a backend started while the Hub waits for
            // the next request (gxhub_refresh, or a call that starts an offline backend) can hang during its own
            // startup, because Windows serialises operations on that shared pipe behind the Hub's pending read.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var pair in BuildStartupEnvironment(startup, profileRevision))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    // The line every start leaves on stderr, one format for the three ways (the client may keep stderr, and the start
    // mode is what tells whether the Gateway is bound to the job of the client):
    //   [native] iniciado pelo hub, PID 123 mode=hub-tree
    //   [native] iniciado pelo hub, PID 123 mode=outside-job ms=305
    //   [native] iniciado pelo hub, PID 123 mode=hub-tree-fallback reason=wmi-refused-2
    // No path, and nothing after the last value, so that a pattern for mode= or reason= takes the whole of it.
    internal static string StartLine(int processId, string startMode, string? fallbackReason, long? elapsedMilliseconds) =>
        $"iniciado pelo hub, PID {processId} mode={startMode}" +
        (fallbackReason is null ? string.Empty : $" reason={fallbackReason}") +
        (elapsedMilliseconds is long elapsed ? $" ms={elapsed}" : string.Empty);

    private void StartProcess(ProcessStartInfo startInfo, string startMode, string? fallbackReason)
    {
        // A process this Hub started earlier and that has already exited (the Gateway was replaced by a promotion or
        // crashed): its handle is released before the backend is started again on demand.
        _process?.Dispose();
        _process = null;

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.Error.WriteLine($"[{_backend.Id}:out] {e.Data}");
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.Error.WriteLine($"[{_backend.Id}:err] {e.Data}");
            }
        };
        process.Exited += (_, _) => LogExit(process);

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Não foi possível iniciar o backend '{_backend.Id}'.");
        }

        _process = process;
        _startedUtc = DateTime.UtcNow;
        _startedInJob = IsInJob(process);
        _startMode = startMode;
        Interlocked.Increment(ref _totalStarts);
        StartedFromProfileRevision = ProfileRevision;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Console.Error.WriteLine($"[{_backend.Id}] " + StartLine(_process.Id, startMode, fallbackReason, null));
    }

    // Stops the process this Hub started, with whatever it started in turn. Besides the Hub shutdown and
    // gxhub_refresh, it is used for a process that is not the Gateway of the backend: one that lost the port to
    // another process, or one that never opened the port within its deadline.
    public void Stop(string? reason = null)
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                if (reason is not null)
                {
                    Console.Error.WriteLine(
                        $"[{_backend.Id}] encerrando o processo iniciado pelo hub, PID {_process.Id}: {reason}.");
                }

                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{_backend.Id}] falha ao encerrar processo do hub: {ex.Message}");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    // Lets go of the process this Hub started without stopping it: other Hubs are using the Gateway, or it could not
    // be told that none is. From here on the process belongs to nobody, like a Gateway started by a promotion.
    public void Detach(string reason)
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                Console.Error.WriteLine(
                    $"[{_backend.Id}] Gateway mantido em execução ao encerrar o Hub, PID {_process.Id}: {reason}. " +
                    "Este Hub só libera o processo.");
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
