using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GxMcpHub;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // First of all, before any process is started: the channels of whoever launched the Hub are not passed on.
        StandardHandles.StopInheriting();

        // The launcher and the probe do their one job and leave, before anything else of the Hub: no console setup, no
        // profile, no presence, no server.
        if (args.Length == 2 && args[0] == DetachedLauncher.Switch)
        {
            return DetachedLauncher.Run(args[1]);
        }

        // Only the self-tests start the launcher with the delay: the Hub never writes it.
        if (args.Length == 4 && args[0] == DetachedLauncher.Switch && args[2] == DetachedLauncher.DelayAfterStartSwitch &&
            int.TryParse(args[3], out var delayAfterStart))
        {
            return DetachedLauncher.Run(args[1], delayAfterStart);
        }

        if (args.Length == 1 && args[0] == JobProbe.Switch)
        {
            return JobProbe.RunChild();
        }

        if (OutsideJobSelfTestModes.IsMode(args))
        {
            return await OutsideJobSelfTestModes.RunAsync(args);
        }

        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true
        });

        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                CliOptions.PrintHelp();
                return 0;
            }

            if (options.SelfTestSweep)
            {
                return SelfTestListener.RunSweepReport(options.SelfTestSweepScan);
            }

            if (options.SelfTestListenerPort is int listenerPort)
            {
                if (options.SelfTestJobReport)
                {
                    return await SelfTestListener.RunJobReportAsync(listenerPort, options.SelfTestListenerSeconds);
                }

                if (options.SelfTestLeaveChild)
                {
                    return SelfTestListener.RunLeaveChild(listenerPort, options.SelfTestListenerSeconds);
                }

                if (options.SelfTestPresenceChildPort is int presenceChildPort)
                {
                    return await SelfTestListener.RunPresenceAsync(
                        listenerPort,
                        options.SelfTestListenerSeconds,
                        presenceChildPort);
                }

                return options.SelfTestWrapper
                    ? await SelfTestListener.RunWrapperAsync(listenerPort, options.SelfTestListenerSeconds)
                    : await SelfTestListener.RunAsync(listenerPort, options.SelfTestListenerSeconds);
            }

            if (options.SelfTest)
            {
                return SelfTests.Run(options.SelfTestOnly);
            }

            if (string.IsNullOrWhiteSpace(options.ConfigPath))
            {
                Console.Error.WriteLine("Informe --config <perfil.json>.");
                CliOptions.PrintHelp(Console.Error);
                return 2;
            }

            var loaded = HubConfigLoader.Load(options.ConfigPath);
            if (options.Doctor)
            {
                return await Doctor.RunAsync(loaded, options.StartBackends, CancellationToken.None);
            }

            await using var hub = new HubService(loaded);
            await hub.StartAsync(CancellationToken.None);
            hub.StartBackgroundReconcile();

            var server = new StdioMcpServer(hub);
            await server.RunAsync(CancellationToken.None);
            return 0;
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine("Configuração inválida: " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Falha fatal no GxMcpHub: " + ex);
            return 1;
        }
    }
}

internal sealed record CliOptions(
    string? ConfigPath,
    bool Doctor,
    bool StartBackends,
    bool SelfTest,
    bool ShowHelp,
    int? SelfTestListenerPort,
    int SelfTestListenerSeconds,
    bool SelfTestWrapper,
    IReadOnlyList<string>? SelfTestOnly,
    int? SelfTestPresenceChildPort,
    bool SelfTestLeaveChild,
    bool SelfTestSweep,
    bool SelfTestSweepScan,
    bool SelfTestJobReport)
{
    public static CliOptions Parse(string[] args)
    {
        string? config = null;
        var doctor = false;
        var start = false;
        var selfTest = false;
        var help = false;
        int? listenerPort = null;
        var listenerSeconds = 0;
        var wrapper = false;
        string[]? selfTestOnly = null;
        int? presenceChildPort = null;
        var leaveChild = false;
        var sweep = false;
        var sweepScan = false;
        var jobReport = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    if (++i >= args.Length)
                    {
                        throw new ConfigurationException("--config requer um caminho.");
                    }
                    config = args[i];
                    break;
                case "--doctor":
                    doctor = true;
                    break;
                case "--start-backends":
                    start = true;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--self-test-only":
                    // Interno: roda só os autotestes cujo nome contém um dos fragmentos, separados por "|".
                    if (++i >= args.Length)
                    {
                        throw new ConfigurationException("--self-test-only requer os fragmentos de nome.");
                    }
                    selfTest = true;
                    selfTestOnly = args[i].Split(
                        '|',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case SelfTestListener.Switch:
                case SelfTestListener.WrapperSwitch:
                    // Interno aos autotestes: um processo filho que só escuta uma porta loopback por alguns segundos,
                    // ou um que inicia esse processo e fica vivo (no lugar de um script que lança o Gateway).
                    wrapper = args[i] == SelfTestListener.WrapperSwitch;
                    if (i + 2 >= args.Length ||
                        !int.TryParse(args[++i], out var port) || port is < 1 or > 65535 ||
                        !int.TryParse(args[++i], out listenerSeconds))
                    {
                        throw new ConfigurationException($"{SelfTestListener.Switch} requer porta e segundos.");
                    }
                    listenerPort = port;
                    break;
                case SelfTestListener.SweepSwitch:
                    // Interno aos autotestes: diz o que a varredura de identificadores herdáveis do Main fez neste
                    // processo e, com "scan", força uma segunda varredura pela reserva e a mede.
                    sweep = true;
                    if (i + 1 < args.Length && args[i + 1] == "scan")
                    {
                        sweepScan = true;
                        i++;
                    }
                    break;
                case SelfTestListener.JobReportSwitch:
                    // Interno aos autotestes: um "Hub" que inicia, pelo ProcessSupervisor de verdade, um processo que
                    // escuta a porta e diz se o sistema o pôs num job object.
                    if (i + 2 >= args.Length ||
                        !int.TryParse(args[++i], out var jobPort) || jobPort is < 1 or > 65535 ||
                        !int.TryParse(args[++i], out listenerSeconds))
                    {
                        throw new ConfigurationException(
                            $"{SelfTestListener.JobReportSwitch} requer porta e segundos.");
                    }
                    listenerPort = jobPort;
                    jobReport = true;
                    break;
                case SelfTestListener.LeaveChildSwitch:
                    // Interno aos autotestes: um "Hub" que inicia um processo que escuta a porta, do jeito que o Hub
                    // inicia um backend, e sai sem esperar por ele.
                    if (i + 2 >= args.Length ||
                        !int.TryParse(args[++i], out var leavePort) || leavePort is < 1 or > 65535 ||
                        !int.TryParse(args[++i], out listenerSeconds))
                    {
                        throw new ConfigurationException(
                            $"{SelfTestListener.LeaveChildSwitch} requer porta e segundos.");
                    }
                    listenerPort = leavePort;
                    leaveChild = true;
                    break;
                case SelfTestListener.PresenceSwitch:
                    // Interno aos autotestes: um processo que registra presença num endpoint, como um Hub conectado,
                    // e opcionalmente inicia um filho que escuta outra porta (0 = nenhum).
                    if (i + 3 >= args.Length ||
                        !int.TryParse(args[++i], out var presencePort) || presencePort is < 1 or > 65535 ||
                        !int.TryParse(args[++i], out listenerSeconds) ||
                        !int.TryParse(args[++i], out var childPort) || childPort is < 0 or > 65535)
                    {
                        throw new ConfigurationException(
                            $"{SelfTestListener.PresenceSwitch} requer porta, segundos e porta do filho.");
                    }
                    listenerPort = presencePort;
                    presenceChildPort = childPort;
                    break;
                case "-h":
                case "--help":
                    help = true;
                    break;
                default:
                    throw new ConfigurationException($"Argumento desconhecido: {args[i]}");
            }
        }

        return new CliOptions(
            config,
            doctor,
            start,
            selfTest,
            help,
            listenerPort,
            listenerSeconds,
            wrapper,
            selfTestOnly,
            presenceChildPort,
            leaveChild,
            sweep,
            sweepScan,
            jobReport);
    }

    public static void PrintHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine($"GxMcpHub {HubInfo.Version}");
        writer.WriteLine("  --config <perfil.json>       Inicia o MCP STDIO.");
        writer.WriteLine("  --doctor [--start-backends]  Valida caminhos e endpoints.");
        writer.WriteLine("  --self-test                  Executa testes internos sem abrir KB.");
    }
}

// What the self-tests start in place of a Gateway when they need a real child process that owns a loopback port:
// it accepts and drops connections for a bounded time and does nothing else.
//
// The --self-test-* switches below (listener, wrapper, presence, leave-child, sweep, job-report) exist only for the
// self-tests and the end-to-end proofs. They are not part of the contract of the Hub, they are not in --help, and
// nothing in the operation (profiles, scripts, the promotion kit) may use them.
internal static class SelfTestListener
{
    public const string Switch = "--self-test-listener";
    public const string WrapperSwitch = "--self-test-wrapper";
    public const string PresenceSwitch = "--self-test-presence";
    public const string PresenceReady = "presence-ready ";
    public const string LeaveChildSwitch = "--self-test-leave-child";
    public const string LeaveChildReady = "leave-child-ready ";
    public const string SweepSwitch = "--self-test-sweep";
    public const string JobReportSwitch = "--self-test-job-report";
    public const string JobReportReady = "job-report ";
    private const int MaxSeconds = 300;

    // In place of a script that launches the Gateway: it starts the listener as a process of its own and stays alive
    // for the given time, whatever happens to that process.
    public static async Task<int> RunWrapperAsync(int port, int seconds)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Executável do Hub indisponível.");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Pipes of its own for the three channels, as the Hub gives to what it starts. This process has not
            // passed on the channels it got from the Hub either, so it could not hand them down.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var assemblyPath = typeof(SelfTestListener).Assembly.Location;
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(assemblyPath))
        {
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add(Switch);
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add(seconds.ToString());
        using var listener = System.Diagnostics.Process.Start(startInfo)
                             ?? throw new InvalidOperationException("O processo que escuta não pôde ser iniciado.");
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxSeconds)));
        try
        {
            if (!listener.HasExited)
            {
                listener.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        return 0;
    }

    // In place of another Hub connected to an endpoint: it registers a presence there, says so on standard output
    // and stays alive. With a child port it first starts, the way the Hub starts a backend, a process that listens on
    // that port: the presence is taken before, so that a handle handed down to the child would show.
    public static async Task<int> RunPresenceAsync(int port, int seconds, int childPort)
    {
        using var presence = new HubPresence();
        if (!presence.TryEnter(new Uri($"http://127.0.0.1:{port}/mcp")))
        {
            return 3;
        }

        var childId = 0;
        if (childPort > 0)
        {
            using var child = StartListenerChild(childPort, seconds);
            childId = child.Id;
        }

        Console.Out.WriteLine(PresenceReady + childId);
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxSeconds)));
        return 0;
    }

    // What the pass over the inheritable handles that Main made did in this process, and, with scan, what the
    // fallback costs here: the same pass forced to probe every handle value up to its ceiling.
    public static int RunSweepReport(bool scan)
    {
        var main = StandardHandles.LastSweep;
        Console.Out.WriteLine(Describe("main", main));
        if (scan)
        {
            Console.Out.WriteLine(Describe("scan", StandardHandles.StopInheritingAll(forceScan: true)));
        }

        Console.Out.Flush();
        return 0;

        static string Describe(string name, HandleSweep sweep) =>
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"sweep {name} path={sweep.Path} cleared={sweep.Cleared} probes={sweep.Probes} " +
                $"elapsedMs={sweep.ElapsedMilliseconds:0.000}");
    }

    // In place of a Hub started inside the job of its client: it starts, with the real ProcessSupervisor (the code
    // that decides StartedInJob), a process that listens on the port, says what it decided and the process id, and
    // stays alive for the given time.
    public static async Task<int> RunJobReportAsync(int port, int seconds)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Executável do Hub indisponível.");
        var assemblyPath = typeof(SelfTestListener).Assembly.Location;
        var prefix = Path.GetFileNameWithoutExtension(processPath)
                         .Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(assemblyPath)
            ? $"\"{assemblyPath}\" "
            : string.Empty;
        var backend = new BackendConfig
        {
            Id = "job",
            Enabled = true,
            Startup = new StartupConfig
            {
                Command = processPath,
                Arguments = $"{prefix}{Switch} {port} {seconds}",
                WorkingDirectory = Environment.CurrentDirectory
            }
        };
        await using var supervisor = new ProcessSupervisor(backend);
        supervisor.Start();
        var reported = supervisor.StartedInJob is bool inJob ? (inJob ? "true" : "false") : "null";
        Console.Out.WriteLine($"{JobReportReady}{reported} {supervisor.ProcessId}");
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxSeconds)));
        return 0;
    }

    // In place of a Hub that leaves while the Gateway it started lives on: it starts a process that listens on the
    // port, the way the Hub starts a backend, says so and returns without waiting for it. Whoever launched it can
    // then tell whether the channels it gave it are still held open by what it left behind.
    public static int RunLeaveChild(int port, int seconds)
    {
        using var child = StartListenerChild(port, seconds);
        Console.Out.WriteLine(LeaveChildReady + child.Id);
        Console.Out.Flush();
        return 0;
    }

    // The way the Hub starts a backend (ProcessSupervisor.BuildStartInfo): pipes of its own for the three channels.
    private static System.Diagnostics.Process StartListenerChild(int port, int seconds)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Executável do Hub indisponível.");
        var assemblyPath = typeof(SelfTestListener).Assembly.Location;
        var prefix = Path.GetFileNameWithoutExtension(processPath)
                         .Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(assemblyPath)
            ? $"\"{assemblyPath}\" "
            : string.Empty;
        var startInfo = ProcessSupervisor.BuildStartInfo(
            new StartupConfig
            {
                Command = processPath,
                Arguments = $"{prefix}{Switch} {port} {seconds}",
                WorkingDirectory = Environment.CurrentDirectory
            },
            null);
        return System.Diagnostics.Process.Start(startInfo)
               ?? throw new InvalidOperationException("O processo filho não pôde ser iniciado.");
    }

    public static async Task<int> RunAsync(int port, int seconds)
    {
        // The self-tests that count how many processes a start made give this variable (through the profile): each
        // listener says it started, whether or not it then gets the port.
        if (Environment.GetEnvironmentVariable("GXHUB_TEST_START_LOG") is { Length: > 0 } startLog)
        {
            try
            {
                using var log = new FileStream(startLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                log.Write(System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId + "\n"));
            }
            catch (IOException)
            {
                // Only a test aid.
            }
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxSeconds)));
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            listener.Stop();
        }
    }
}
