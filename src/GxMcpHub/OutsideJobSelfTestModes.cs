using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GxMcpHub;

// What the self-tests and the end-to-end proofs of the start outside the job of the client run as processes of their
// own. Like the other --self-test-* switches, these are not part of the contract of the Hub, are not in --help, and
// nothing in the operation may use them.
//
//   --self-test-outside-job-report <port> <seconds> <policy> [wrapper | envdump:<file>]
//       A "Hub": starts, with the real ProcessSupervisor and the real decision (policy auto, always or never), a
//       process that listens on the port (or the one behind "wrapper", or the env dump of "envdump"), and says how it
//       started, whether the system put it in a job, its process id and how long the start took. Stays alive for the
//       given seconds.
//   --self-test-wmi-create <sta|mta> <show|-> <flags|-> <directory|-> <command line...>
//       Creates a process through the WMI call of the Hub, on a thread of the given apartment, with the given startup
//       information (a dash leaves it out), to measure what each part of it does (the visible console window, above
//       all, and whether the call needs an STA thread).
//   --self-test-env-dump <file> <seconds> <NAME;NAME...>
//       Writes what this process sees (directory, the named variables, the length of PATH, whether it is in a job) to
//       the file and stays alive for the given seconds: what a process started through the launcher gets.
internal static class OutsideJobSelfTestModes
{
    internal const string ReportSwitch = "--self-test-outside-job-report";
    internal const string ReportReady = "outside-job-report ";
    internal const string WmiCreateSwitch = "--self-test-wmi-create";
    internal const string WmiCreateReady = "wmi-create ";
    internal const string EnvDumpSwitch = "--self-test-env-dump";
    private const int MaxSeconds = 300;

    internal static bool IsMode(string[] args) =>
        args.Length > 0 && args[0] is ReportSwitch or WmiCreateSwitch or EnvDumpSwitch;

    internal static async Task<int> RunAsync(string[] args)
    {
        switch (args[0])
        {
            case ReportSwitch:
                return await ReportAsync(args);
            case WmiCreateSwitch:
                return WmiCreate(args);
            default:
                return await EnvDumpAsync(args);
        }
    }

    private static async Task<int> ReportAsync(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[1], out var port) || port is < 1 or > 65535 ||
            !int.TryParse(args[2], out var seconds))
        {
            Console.Error.WriteLine($"{ReportSwitch} requer porta, segundos e a política.");
            return 2;
        }

        var policy = OutsideJobPolicySetting.Parse(args[3]);
        var extra = args.Length > 4 ? args[4] : string.Empty;
        var wrapper = extra == "wrapper";
        var (file, prefix) = OutsideJobStarter.DefaultHubCommand();
        var childArguments = extra.StartsWith("envdump:", StringComparison.Ordinal)
            ? $"{EnvDumpSwitch} \"{extra["envdump:".Length..]}\" {seconds} " +
              "GXHUB_TEST_FROM_PROFILE;GXHUB_TEST_SECRET;GXMCP_HUB_PROFILE_REVISION;PATH"
            : $"{(wrapper ? SelfTestListener.WrapperSwitch : SelfTestListener.Switch)} {port} {seconds}";
        var backend = new BackendConfig
        {
            Id = "oj",
            Enabled = true,
            Startup = new StartupConfig
            {
                Command = file,
                Arguments = prefix + childArguments,
                WorkingDirectory = Environment.CurrentDirectory,
                Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GXHUB_TEST_FROM_PROFILE"] = "perfil"
                }
            }
        };
        var starter = new OutsideJobStarter(policy);
        await using var supervisor = new ProcessSupervisor(backend)
        {
            OutsideJob = starter,
            ProfileRevision = "0123456789AB"
        };
        var watch = Stopwatch.StartNew();
        await supervisor.StartAsync(CancellationToken.None);
        var elapsed = watch.ElapsedMilliseconds;
        string Flag(bool? value) => value is bool known ? (known ? "true" : "false") : "null";
        Console.Out.WriteLine(
            $"{ReportReady}mode={supervisor.StartMode ?? "none"} inJob={Flag(supervisor.StartedInJob)} " +
            $"pid={supervisor.ProcessId?.ToString() ?? "0"} ms={elapsed} child={Flag(starter.ChildWouldBeInJob)} " +
            $"policy={OutsideJobPolicySetting.Name(starter.Policy)}");
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxSeconds)));
        return 0;
    }

    private static int WmiCreate(string[] args)
    {
        if (args.Length < 6 || args[1] is not ("sta" or "mta"))
        {
            Console.Error.WriteLine($"{WmiCreateSwitch} requer sta ou mta, show, flags, diretório e a linha de comando.");
            return 2;
        }

        ushort? show = args[2] == "-" ? null : ushort.Parse(args[2]);
        uint? flags = args[3] == "-" ? null : Convert.ToUInt32(args[3], args[3].StartsWith("0x") ? 16 : 10);
        var directory = args[4] == "-" ? null : args[4];
        var commandLine = string.Join(' ', args.Skip(5));
        // GXHUB_TEST_WMI_REPEAT: quantas chamadas seguidas, cada uma numa thread nova (o que o Hub faz a cada início).
        var repeat = int.TryParse(Environment.GetEnvironmentVariable("GXHUB_TEST_WMI_REPEAT"), out var times) ? Math.Max(1, times) : 1;
        var exit = 0;
        for (var attempt = 1; attempt <= repeat; attempt++)
        {
            WmiCreateOutcome outcome = default;
            var thread = new Thread(() => outcome = WmiProcessCreator.Create(commandLine, directory, show, flags));
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(args[1] == "sta" ? ApartmentState.STA : ApartmentState.MTA);
            }

            thread.Start();
            thread.Join();
            Console.Out.WriteLine($"{WmiCreateReady}attempt={attempt} ok={outcome.Ok} pid={outcome.ProcessId} code={outcome.Code ?? "-"}");
            Console.Out.Flush();
            exit = outcome.Ok ? exit : 1;
        }

        return exit;
    }

    private static async Task<int> EnvDumpAsync(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out var seconds))
        {
            Console.Error.WriteLine($"{EnvDumpSwitch} requer arquivo, segundos e os nomes.");
            return 2;
        }

        var variables = new JsonObject();
        foreach (var name in args[3].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            variables[name] = Environment.GetEnvironmentVariable(name);
        }

        using var self = Process.GetCurrentProcess();
        var inJob = ProcessSupervisor.IsInJob(self);
        var info = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["cwd"] = Environment.CurrentDirectory,
            ["vars"] = variables,
            ["pathLength"] = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Length,
            ["inJob"] = inJob,
            ["session"] = self.SessionId,
            ["user"] = Environment.UserName
        };
        File.WriteAllText(args[1], info.ToJsonString(), new System.Text.UTF8Encoding(false));
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxSeconds)));
        return 0;
    }
}
