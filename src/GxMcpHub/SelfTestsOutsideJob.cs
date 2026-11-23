using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace GxMcpHub;

// 0.4.0: o Gateway que um Hub inicia fora do job do cliente. Os testes que não precisam do serviço WMI de verdade usam
// um "WMI" que inicia o lançador pelo caminho comum; os que provam o job e o WMI rodam o Hub de teste dentro de jobs
// criados aqui (um KILL_ON_JOB_CLOSE puro e um com SILENT_BREAKAWAY_OK), nunca em job de cliente.
internal static partial class SelfTests
{
    // Anota o que reprovou e diz no stderr, para que uma falha não seja só "FALHOU".
    private sealed class Checks
    {
        private readonly string _test;
        private readonly List<string> _failed = new();

        public Checks(string test)
        {
            _test = test;
        }

        public bool Ok => _failed.Count == 0;

        public void Add(string label, bool ok)
        {
            if (!ok)
            {
                _failed.Add(label);
            }
        }

        public bool Done()
        {
            if (!Ok)
            {
                Console.Error.WriteLine($"[self-test] {_test}: reprovou {string.Join("; ", _failed)}.");
            }

            return Ok;
        }
    }

    // O que o serviço WMI faz, para os testes que não precisam do serviço: o lançador iniciado pelo caminho comum.
    private static WmiCreateOutcome StartLauncherNormally(string commandLine, string? directory)
    {
        var (file, prefix) = OutsideJobStarter.DefaultHubCommand();
        var encoded = commandLine[(commandLine.LastIndexOf(' ') + 1)..];
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = prefix + DetachedLauncher.Switch + " " + encoded,
            WorkingDirectory = directory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return process is null
            ? new WmiCreateOutcome(false, 0, "wmi-error")
            : new WmiCreateOutcome(true, process.Id, null);
    }

    private static OutsideJobStarter NewStarter(
        string root,
        OutsideJobPolicy policy,
        Func<string, string?, WmiCreateOutcome> wmi,
        Func<bool?>? hubInJob = null,
        Func<CancellationToken, Task<bool?>>? probe = null) =>
        new(policy, hubInJob, probe, wmi, Path.Combine(root, "GxMcpHub", LaunchSpec.LaunchFolderName))
        {
            Budget = TimeSpan.FromSeconds(15),
            Grace = TimeSpan.FromMilliseconds(300),
            LauncherExitGrace = TimeSpan.FromMilliseconds(500),
            LateAnswerCleanup = TimeSpan.FromMilliseconds(300),
            ResultPoll = TimeSpan.FromMilliseconds(20)
        };

    // Um processo que só escuta a porta, no lugar de um Gateway (ou de um lançador que nunca responde). O número vai
    // para a lista de quem o criou, que o encerra no fim mesmo que o objeto já tenha sido descartado.
    private static Process StartStandIn(int port, int seconds, List<int> created)
    {
        var (file, prefix) = SelfTestChildCommand();
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = $"{prefix}{SelfTestListener.Switch} {port} {seconds}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        created.Add(process.Id);
        return process;
    }

    // Os processos que o serviço WMI cria não são filhos de ninguém: achados pelo número, conferidos pelo instante de
    // criação e pelo arquivo que executam, e só então encerrados.
    private static void KillOurs(int processId, DateTime notBeforeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited &&
                process.StartTime.ToUniversalTime() >= notBeforeUtc - TimeSpan.FromSeconds(1) &&
                string.Equals(
                    process.MainModule?.FileName,
                    SelfTestChildCommand().Command,
                    StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or
                                       NotSupportedException)
        {
            // Já saiu, ou não é nosso para alcançar.
        }
    }

    private static string EncodeSpec(JsonObject json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString()));

    // O lançador de verdade, iniciado pelo caminho comum, com o que ele diz de volta.
    private static (int ExitCode, LaunchResult? Result) RunLauncherProcess(string encoded, string? resultPath)
    {
        var (file, prefix) = SelfTestChildCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = prefix + DetachedLauncher.Switch + " " + encoded,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(30000);
        Task.WaitAll(new Task[] { output, error }, 5000);
        return (process.ExitCode, resultPath is null ? null : LaunchResultFile.TryRead(resultPath));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Política, especificação, lançador e sonda
    // ---------------------------------------------------------------------------------------------------------------

    private static bool OutsideJobPolicyTest()
    {
        var checks = new Checks("política");
        var auto = new string?[] { null, string.Empty, "  ", "auto", "AUTO", "talvez", "2", "sempre" };
        var always = new[] { "always", "ALWAYS", "1", "true", "TRUE", "on", "yes", " Yes " };
        var never = new[] { "never", "NEVER", "0", "false", "off", "no", " No " };
        checks.Add("auto", auto.All(value => OutsideJobPolicySetting.Parse(value) == OutsideJobPolicy.Auto));
        checks.Add("always", always.All(value => OutsideJobPolicySetting.Parse(value) == OutsideJobPolicy.Always));
        checks.Add("never", never.All(value => OutsideJobPolicySetting.Parse(value) == OutsideJobPolicy.Never));
        checks.Add(
            "nomes",
            OutsideJobPolicySetting.Name(OutsideJobPolicy.Auto) == "auto" &&
            OutsideJobPolicySetting.Name(OutsideJobPolicy.Always) == "always" &&
            OutsideJobPolicySetting.Name(OutsideJobPolicy.Never) == "never");

        var previous = Environment.GetEnvironmentVariable(OutsideJobPolicySetting.Variable);
        try
        {
            Environment.SetEnvironmentVariable(OutsideJobPolicySetting.Variable, "never");
            var fromNever = OutsideJobPolicySetting.FromEnvironment() == OutsideJobPolicy.Never;
            Environment.SetEnvironmentVariable(OutsideJobPolicySetting.Variable, "yes");
            var fromYes = OutsideJobPolicySetting.FromEnvironment() == OutsideJobPolicy.Always;
            Environment.SetEnvironmentVariable(OutsideJobPolicySetting.Variable, null);
            var fromNothing = OutsideJobPolicySetting.FromEnvironment() == OutsideJobPolicy.Auto;
            checks.Add("ambiente", fromNever && fromYes && fromNothing);
            checks.Add("variável", OutsideJobPolicySetting.Variable == "GXMCP_HUB_START_OUTSIDE_JOB");
        }
        finally
        {
            Environment.SetEnvironmentVariable(OutsideJobPolicySetting.Variable, previous);
        }

        return checks.Done();
    }

    private static bool LaunchSpecRoundTripTest()
    {
        var checks = new Checks("especificação");
        var resultPath = Path.Combine(Path.GetTempPath(), "GxMcpHub", "launch", "abc.result");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GXMCP_LOG_DIR"] = @"C:\logs com espaço\ção",
            ["ASPAS"] = "dizem \"olá\" e 'oi'",
            ["VAZIA"] = string.Empty,
            ["GXMCP_HUB_PROFILE_REVISION"] = "ABCDEF123456",
            ["GXMCP_EXPECTED_KB_PATH"] = @"C:\Synthetic\KB"
        };
        var spec = new LaunchSpec(
            @"C:\Program Files\dotnet\dotnet.exe",
            "\"C:\\a b\\x.dll\" --porta 5010 \"ção\"",
            @"C:\KB com espaço",
            environment,
            resultPath);
        var encoded = spec.Encode();
        var decoded = LaunchSpec.Decode(encoded);
        checks.Add(
            "ida e volta",
            decoded.File == spec.File &&
            decoded.Arguments == spec.Arguments &&
            decoded.WorkingDirectory == spec.WorkingDirectory &&
            decoded.ResultPath == spec.ResultPath &&
            decoded.Environment.Count == environment.Count &&
            environment.All(pair => decoded.Environment.TryGetValue(pair.Key, out var value) && value == pair.Value));

        var bare = LaunchSpec.Decode(
            new LaunchSpec("x.exe", string.Empty, null, new Dictionary<string, string>(), resultPath).Encode());
        checks.Add("sem argumentos, diretório e ambiente", bare.Arguments.Length == 0 && bare.WorkingDirectory is null &&
                                                            bare.Environment.Count == 0);
        checks.Add("linha curta", encoded.Length < 2000 && LaunchSpec.MaxEncodedLength == 8000);
        checks.Add(
            "só caracteres de base64 (viaja numa linha de comando)",
            encoded.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '='));

        var launch = Path.Combine(Path.GetTempPath(), "GxMcpHub", "launch");
        checks.Add("caminho válido", LaunchSpec.IsValidResultPath(resultPath));
        checks.Add(
            "caminhos inválidos",
            !LaunchSpec.IsValidResultPath(null) &&
            !LaunchSpec.IsValidResultPath(string.Empty) &&
            !LaunchSpec.IsValidResultPath(@"launch\a.result") &&
            !LaunchSpec.IsValidResultPath(Path.Combine(launch, "a.txt")) &&
            !LaunchSpec.IsValidResultPath(Path.Combine(Path.GetTempPath(), "GxMcpHub", "outra", "a.result")) &&
            !LaunchSpec.IsValidResultPath(Path.Combine(Path.GetTempPath(), "Outro", "launch", "a.result")) &&
            !LaunchSpec.IsValidResultPath(Path.Combine(launch, "..", "launch", "a.result")) &&
            !LaunchSpec.IsValidResultPath(Path.Combine(launch, "a\0.result")));

        // Um caminho que só passa pela estrutura <algo>\GxMcpHub\launch depois de resolvido (".." e "." no meio) não é
        // o caminho normalizado que o Hub grava: é recusado mesmo caindo, no fim, numa pasta launch de verdade.
        checks.Add(
            "caminho não normalizado que resolve para a pasta launch",
            LaunchSpec.IsValidResultPath(Path.Combine(launch, "a.result")) &&
            !LaunchSpec.IsValidResultPath(Path.Combine(Path.GetTempPath(), "Outro", "..", "GxMcpHub", "launch", "a.result")) &&
            !LaunchSpec.IsValidResultPath(Path.Combine(Path.GetTempPath(), ".", "GxMcpHub", "launch", "a.result")));
        return checks.Done();
    }

    private static async Task<bool> LaunchSpecNeverCarriesTheHubEnvironmentTestAsync()
    {
        var checks = new Checks("ambiente do Hub");
        var root = NewTempRoot("GxMcpHub-spec-env-test-");
        const string secretName = "GXHUB_TEST_CLIENT_SECRET";
        var canary = "marcador-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(secretName, canary);
        try
        {
            string? captured = null;
            var starter = NewStarter(
                root,
                OutsideJobPolicy.Always,
                (commandLine, _) =>
                {
                    captured = commandLine;
                    return new WmiCreateOutcome(false, 0, "wmi-refused-2");
                });
            var startup = new StartupConfig
            {
                Command = @"C:\gateway\gx.exe",
                Arguments = "--porta 5010",
                WorkingDirectory = root,
                Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GXMCP_EXPECTED_KB_PATH"] = @"C:\kb",
                    ["GXMCP_LOG_DIR"] = @"C:\logs"
                }
            };
            var result = await starter.TryStartAsync("native", startup, "ABCDEF123456", CancellationToken.None);
            checks.Add("o WMI foi chamado e recusou", captured is not null && result.Process is null &&
                                                    result.Failure == "wmi-refused-2");
            if (captured is null)
            {
                return checks.Done();
            }

            var encoded = captured[(captured.LastIndexOf(' ') + 1)..];
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var spec = LaunchSpec.Decode(encoded);
            checks.Add(
                "só o ambiente do perfil e a revisão do perfil",
                spec.Environment.Count == 3 &&
                spec.Environment["GXMCP_EXPECTED_KB_PATH"] == @"C:\kb" &&
                spec.Environment["GXMCP_LOG_DIR"] == @"C:\logs" &&
                spec.Environment[ProcessSupervisor.ProfileRevisionVariable] == "ABCDEF123456");
            checks.Add(
                "nada do ambiente do Hub",
                !json.Contains(canary, StringComparison.Ordinal) &&
                !spec.Environment.ContainsKey(secretName) &&
                !spec.Environment.ContainsKey("PATH") &&
                !spec.Environment.ContainsKey("TEMP") &&
                !captured.Contains(canary, StringComparison.Ordinal));
            checks.Add(
                "comando, argumentos e diretório do perfil",
                spec.File == startup.Command && spec.Arguments == startup.Arguments && spec.WorkingDirectory == root);
            checks.Add(
                "linha de comando do WMI",
                captured.Contains(DetachedLauncher.Switch, StringComparison.Ordinal) &&
                captured.StartsWith("\"", StringComparison.Ordinal) && !captured.Contains('\n'));
            checks.Add(
                "resposta na pasta launch",
                LaunchSpec.IsValidResultPath(spec.ResultPath) &&
                string.Equals(
                    Path.GetDirectoryName(spec.ResultPath),
                    Path.Combine(root, "GxMcpHub", "launch"),
                    StringComparison.OrdinalIgnoreCase));

            // Sem diretório no perfil, o processo inicia no diretório do Hub (como no início de sempre), e sem revisão
            // do perfil o ambiente da especificação fica vazio.
            captured = null;
            await starter.TryStartAsync(
                "native",
                new StartupConfig { Command = @"C:\gateway\gx.exe", Arguments = string.Empty, WorkingDirectory = string.Empty },
                null,
                CancellationToken.None);
            var withoutDirectory = captured is null ? null : LaunchSpec.Decode(captured[(captured.LastIndexOf(' ') + 1)..]);
            checks.Add(
                "sem diretório no perfil: o do Hub; sem revisão: ambiente vazio",
                withoutDirectory is not null &&
                withoutDirectory.WorkingDirectory == Environment.CurrentDirectory &&
                withoutDirectory.Environment.Count == 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
            DeleteQuietly(root);
        }

        return checks.Done();
    }

    private static bool InvalidLaunchSpecTest()
    {
        var checks = new Checks("especificação inválida");
        var root = NewTempRoot("GxMcpHub-spec-invalid-test-");
        try
        {
            var resultPath = Path.Combine(root, "GxMcpHub", "launch", "x.result");
            JsonObject Valid() => new()
            {
                ["v"] = 1,
                ["file"] = @"C:\x.exe",
                ["args"] = string.Empty,
                ["cwd"] = root,
                ["env"] = new JsonObject { ["A"] = "1" },
                ["result"] = resultPath
            };

            JsonObject With(string name, JsonNode? value)
            {
                var json = Valid();
                json[name] = value;
                return json;
            }

            bool Rejected(string encoded)
            {
                try
                {
                    LaunchSpec.Decode(encoded);
                    return false;
                }
                catch (LaunchSpecException ex)
                {
                    return ex.Code == "invalid-spec";
                }
            }

            checks.Add("a especificação válida é aceita", LaunchSpec.Decode(EncodeSpec(Valid())).File == @"C:\x.exe");
            checks.Add("não é base64", Rejected("%%%"));
            checks.Add("vazia", Rejected(string.Empty));
            checks.Add("não é JSON", Rejected(Convert.ToBase64String(Encoding.UTF8.GetBytes("isto não é json"))));
            checks.Add("JSON que não é objeto", Rejected(Convert.ToBase64String(Encoding.UTF8.GetBytes("[1,2]"))));
            checks.Add("versão errada", Rejected(EncodeSpec(With("v", 2))));
            checks.Add("sem versão", Rejected(EncodeSpec(With("v", null))));
            checks.Add("sem comando", Rejected(EncodeSpec(With("file", null))));
            checks.Add("comando vazio", Rejected(EncodeSpec(With("file", " "))));
            checks.Add("comando que não é texto", Rejected(EncodeSpec(With("file", 5))));
            checks.Add("argumentos que não são texto", Rejected(EncodeSpec(With("args", 5))));
            checks.Add("diretório vazio", Rejected(EncodeSpec(With("cwd", " "))));
            checks.Add("ambiente que não é objeto", Rejected(EncodeSpec(With("env", new JsonArray("A=1")))));
            checks.Add("valor de ambiente que não é texto", Rejected(EncodeSpec(With("env", new JsonObject { ["A"] = 1 }))));
            checks.Add("nome de ambiente com =", Rejected(EncodeSpec(With("env", new JsonObject { ["A=B"] = "1" }))));
            checks.Add("nome de ambiente vazio", Rejected(EncodeSpec(With("env", new JsonObject { [""] = "1" }))));
            checks.Add("resposta relativa", Rejected(EncodeSpec(With("result", @"launch\x.result"))));
            checks.Add("resposta com outra extensão", Rejected(EncodeSpec(With("result", Path.Combine(root, "GxMcpHub", "launch", "x.txt")))));
            checks.Add("resposta fora da pasta launch", Rejected(EncodeSpec(With("result", Path.Combine(root, "GxMcpHub", "x.result")))));

            // O lançador de verdade: sem lugar válido para responder, não há o que escrever; com ele, diz por quê.
            var withoutPlace = RunLauncherProcess("%%%", resultPath);
            checks.Add("lançador: base64 inválido sai com 2 e não escreve", withoutPlace.ExitCode == 2 && withoutPlace.Result is null);
            var badPlace = RunLauncherProcess(EncodeSpec(With("result", @"launch\x.result")), resultPath);
            checks.Add("lançador: resposta inválida sai com 2 e não escreve", badPlace.ExitCode == 2 && badPlace.Result is null);
            var noCommand = RunLauncherProcess(EncodeSpec(With("file", " ")), resultPath);
            checks.Add(
                "lançador: comando vazio sai com 1 e diz invalid-spec",
                noCommand.ExitCode == 1 && noCommand.Result is { Error: "invalid-spec", ProcessId: null });
            File.Delete(resultPath);
            var badEnvironment = RunLauncherProcess(EncodeSpec(With("env", new JsonObject { ["A=B"] = "1" })), resultPath);
            checks.Add(
                "lançador: ambiente inválido sai com 1 e diz invalid-spec",
                badEnvironment.ExitCode == 1 && badEnvironment.Result is { Error: "invalid-spec" });
            File.Delete(resultPath);
            var missing = RunLauncherProcess(EncodeSpec(With("file", Path.Combine(root, "nao-existe", "gateway.exe"))), resultPath);
            checks.Add(
                "lançador: comando que não existe sai com 1 e diz file-not-found, sem caminho",
                missing.ExitCode == 1 && missing.Result is { Error: "file-not-found", ProcessId: null } &&
                !File.ReadAllText(resultPath).Contains(root, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteQuietly(root);
        }

        return checks.Done();
    }

    private static async Task<bool> LauncherProtocolTestAsync()
    {
        var checks = new Checks("lançador");
        var root = NewTempRoot("GxMcpHub-launcher-test-");
        var children = new List<int>();
        var testStart = DateTime.UtcNow;
        try
        {
            // O Gateway do teste escreve o que vê (diretório e variáveis) e fica vivo.
            var (file, prefix) = SelfTestChildCommand();
            var dumpPath = Path.Combine(root, "visto.json");
            var workingDirectory = Path.Combine(root, "diretório do Gateway");
            Directory.CreateDirectory(workingDirectory);
            var resultPath = Path.Combine(root, "GxMcpHub", "launch", "ok.result");
            var spec = new LaunchSpec(
                file,
                $"{prefix}{OutsideJobSelfTestModes.EnvDumpSwitch} \"{dumpPath}\" 60 GXHUB_TEST_FROM_SPEC;GXHUB_TEST_OVERRIDDEN",
                workingDirectory,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GXHUB_TEST_FROM_SPEC"] = "do perfil",
                    ["GXHUB_TEST_OVERRIDDEN"] = "valor da especificação"
                },
                resultPath);
            Environment.SetEnvironmentVariable("GXHUB_TEST_OVERRIDDEN", "valor do lançador");
            (int ExitCode, LaunchResult? Result) ran;
            try
            {
                ran = RunLauncherProcess(spec.Encode(), resultPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXHUB_TEST_OVERRIDDEN", null);
            }

            checks.Add("o lançador sai com 0", ran.ExitCode == 0);
            var result = ran.Result;
            checks.Add("resposta com PID e instante, sem erro", result is { ProcessId: > 0, StartUtc: not null, Error: null });
            checks.Add(
                "nenhum arquivo temporário sobrou",
                Directory.GetFiles(Path.GetDirectoryName(resultPath)!).All(name => !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
            if (result is { ProcessId: int pid, StartUtc: DateTime reported })
            {
                children.Add(pid);
                using var child = Process.GetProcessById(pid);
                checks.Add("o processo nomeado está vivo", !child.HasExited);
                // O lançador e este teste leem o mesmo instante do mesmo processo: a diferença é zero, não "perto".
                checks.Add(
                    "o instante dito é o instante de criação do processo",
                    Math.Abs((child.StartTime.ToUniversalTime() - reported).TotalSeconds) < 0.001);
                checks.Add("o Gateway não é filho do teste depois que o lançador sai", ProcessTree.IsSameOrDescendant(Environment.ProcessId, pid) != true);
            }

            checks.Add("o Gateway escreveu o que viu", await WaitUntilAsync(() => File.Exists(dumpPath), TimeSpan.FromSeconds(20)));
            if (File.Exists(dumpPath))
            {
                var seen = ReadWhenWhole(dumpPath, TimeSpan.FromSeconds(10));
                checks.Add(
                    "ambiente da especificação, que vale mais que o do lançador",
                    seen?["vars"]?["GXHUB_TEST_FROM_SPEC"]?.GetValue<string>() == "do perfil" &&
                    seen["vars"]?["GXHUB_TEST_OVERRIDDEN"]?.GetValue<string>() == "valor da especificação");
                checks.Add(
                    "diretório da especificação",
                    string.Equals(seen!["cwd"]?.GetValue<string>(), workingDirectory, StringComparison.OrdinalIgnoreCase));
            }

            // O resultado que o Hub não consegue ler não pode deixar um Gateway sem dono: o lançador o encerra.
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var launchAsFile = Path.Combine(root, "outra", "GxMcpHub", "launch");
            Directory.CreateDirectory(Path.GetDirectoryName(launchAsFile)!);
            File.WriteAllText(launchAsFile, "uma pasta que é arquivo");
            var broken = new LaunchSpec(
                file,
                $"{prefix}{SelfTestListener.Switch} {port} 60",
                root,
                new Dictionary<string, string>(),
                Path.Combine(launchAsFile, "x.result"));
            var unwritable = RunLauncherProcess(broken.Encode(), null);
            checks.Add("resposta que não pode ser gravada: o lançador sai com 1", unwritable.ExitCode == 1);
            var everListened = false;
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                if (ListenerInspector.Find(endpoint).State == ListenerLookupState.Found)
                {
                    everListened = true;
                    foreach (var id in ProcessesOn(endpoint))
                    {
                        children.Add(id);
                    }

                    break;
                }

                await Task.Delay(50);
            }

            checks.Add("resposta que não pode ser gravada: o Gateway que ele iniciou não fica vivo", !everListened);
        }
        finally
        {
            foreach (var id in children)
            {
                KillOurs(id, testStart);
            }

            DeleteQuietly(root);
        }

        return checks.Done();
    }

    private static bool JobProbeTest()
    {
        var checks = new Checks("sonda");
        checks.Add("true", JobProbe.Parse("job-probe true") == true);
        checks.Add("false com fim de linha", JobProbe.Parse("job-probe false\r\n") == false);
        checks.Add("ruído antes da resposta", JobProbe.Parse("aviso\r\njob-probe true\r\n") == true);
        checks.Add("desconhecido", JobProbe.Parse("job-probe unknown") is null);
        checks.Add("sem resposta", JobProbe.Parse(null) is null && JobProbe.Parse(string.Empty) is null);
        checks.Add("só ruído", JobProbe.Parse("true") is null && JobProbe.Parse("xx job-probe true") is null);
        checks.Add("valor estranho", JobProbe.Parse("job-probe talvez") is null);

        // A sonda de verdade, iniciada como o Hub a inicia: diz o que o sistema diz dela e sai com 0.
        var (file, prefix) = SelfTestChildCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = prefix + JobProbe.Switch,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000);
        checks.Add("a sonda de verdade sai com 0", process.ExitCode == 0);
        checks.Add("a sonda de verdade diz true ou false", JobProbe.Parse(output) is not null);
        return checks.Done();
    }

    private static async Task<bool> LeaveJobDecisionTestAsync()
    {
        var checks = new Checks("decisão de sair do job");

        async Task<(bool Leave, int Probes, bool? Child)> Decide(OutsideJobPolicy policy, bool? hubInJob, bool? probeAnswer)
        {
            var probes = 0;
            var starter = new OutsideJobStarter(
                policy,
                () => hubInJob,
                _ =>
                {
                    Interlocked.Increment(ref probes);
                    return Task.FromResult(probeAnswer);
                });
            var leave = await starter.ShouldLeaveJobAsync(CancellationToken.None);
            return (leave, probes, starter.ChildWouldBeInJob);
        }

        // A política é a primeira palavra: o Hub fora de job nunca sonda; o Hub em job pergunta uma vez.
        checks.Add("auto, Hub fora de job: não sai, não sonda, filho não ficaria em job", await Decide(OutsideJobPolicy.Auto, false, true) == (false, 0, false));
        checks.Add("auto, job que não se sabe: não sai, não sonda", await Decide(OutsideJobPolicy.Auto, null, true) == (false, 0, null));
        checks.Add("auto, Hub em job, filho de sonda em job: sai", await Decide(OutsideJobPolicy.Auto, true, true) == (true, 1, true));
        checks.Add("auto, Hub em job, filho de sonda fora do job: não sai", await Decide(OutsideJobPolicy.Auto, true, false) == (false, 1, false));
        checks.Add("auto, sonda sem resposta: não sai", await Decide(OutsideJobPolicy.Auto, true, null) == (false, 1, null));
        checks.Add("always: sai sem sondar, mesmo fora de job", await Decide(OutsideJobPolicy.Always, false, false) == (true, 0, false));
        checks.Add("always, Hub em job: sai sem sondar", await Decide(OutsideJobPolicy.Always, true, false) == (true, 0, null));
        checks.Add("never: não sai nem sonda, mesmo em job", await Decide(OutsideJobPolicy.Never, true, true) == (false, 0, null));

        // Uma sonda por processo do Hub: várias decisões, em sequência e juntas, perguntam uma vez.
        var probeCalls = 0;
        var once = new OutsideJobStarter(
            OutsideJobPolicy.Auto,
            () => true,
            async _ =>
            {
                Interlocked.Increment(ref probeCalls);
                await Task.Delay(100);
                return true;
            });
        var together = await Task.WhenAll(
            once.ShouldLeaveJobAsync(CancellationToken.None),
            once.ShouldLeaveJobAsync(CancellationToken.None),
            once.ShouldLeaveJobAsync(CancellationToken.None));
        var later = await once.ShouldLeaveJobAsync(CancellationToken.None);
        checks.Add("a sonda roda uma vez para decisões juntas e seguintes", probeCalls == 1 && together.All(leave => leave) && later);

        // A sonda que falha não é repetida a cada início, e o início é o de sempre.
        var failing = 0;
        var broken = new OutsideJobStarter(
            OutsideJobPolicy.Auto,
            () => true,
            _ =>
            {
                Interlocked.Increment(ref failing);
                throw new InvalidOperationException("sonda sintética que falha");
            });
        var first = await broken.ShouldLeaveJobAsync(CancellationToken.None);
        var second = await broken.ShouldLeaveJobAsync(CancellationToken.None);
        checks.Add("sonda que falha: não sai, uma tentativa só", !first && !second && failing == 1 && broken.ChildWouldBeInJob is null);

        // A sonda que não responde tem prazo.
        var slow = new OutsideJobStarter(
            OutsideJobPolicy.Auto,
            () => true,
            async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return true;
            })
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(300)
        };
        var watch = Stopwatch.StartNew();
        var left = await slow.ShouldLeaveJobAsync(CancellationToken.None);
        checks.Add("sonda sem resposta: prazo, não sai", !left && watch.Elapsed < TimeSpan.FromSeconds(5) && slow.ChildWouldBeInJob is null);
        return checks.Done();
    }

    // O contrato com as provas do kit: a sonda de job é preguiçosa. Só roda no ponto em que o Hub vai iniciar um processo de
    // backend, depois da guarda da trava de promoção; a partida do Hub, o status, a rodada periódica e a promoção em
    // andamento não a rodam. É o que deixa uma prova subir o Hub offline sob a trava, pô-lo num job de teste, soltar a trava
    // e fazer a primeira chamada: a sonda corre já dentro do job, e vê o job.
    private static async Task<bool> ProbeRunsOnlyWhereTheHubStartsAProcessTestAsync()
    {
        var checks = new Checks("sonda preguiçosa");
        var root = NewTempRoot("GxMcpHub-lazyprobe-test-");
        var children = new List<int>();
        var testStart = DateTime.UtcNow;

        // O Hub sobe sob a trava de promoção, com o job já atribuído desde o início ou atribuído só depois de subir.
        async Task ScenarioAsync(string name, bool inJobFromTheStart)
        {
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var profilePath = Path.Combine(root, $"perfil-{port}.json");
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var probes = 0;
            var inJob = inJobFromTheStart;
            var starter = NewStarter(
                root,
                OutsideJobPolicy.Auto,
                StartLauncherNormally,
                hubInJob: () => inJob,
                probe: _ =>
                {
                    Interlocked.Increment(ref probes);
                    return Task.FromResult<bool?>(true);
                });
            FileStream? promotion = HoldPromotionLock(root);
            try
            {
                await using var hub = NewHub(profilePath, script, outsideJob: starter);
                await hub.StartAsync(CancellationToken.None);
                var held = hub.StatusJson();
                checks.Add(
                    $"{name}: sob a trava de promoção o Hub sobe offline e sem sondar",
                    Flag(held["promotionLock"]?["held"]) == true &&
                    Flag(held["backends"]?[0]?["online"]) == false &&
                    probes == 0);

                // O Hub é posto no job (se ainda não estava) e o status e as rodadas passam: nada disso sonda, e o
                // childWouldBeInJob espera a sonda.
                inJob = true;
                _ = hub.StatusJson();
                await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                var waiting = hub.StatusJson();
                checks.Add(
                    $"{name}: com o Hub em job e a trava, o status e a rodada não sondam, e childWouldBeInJob é nulo",
                    probes == 0 &&
                    waiting.AsObject().ContainsKey("childWouldBeInJob") &&
                    waiting["childWouldBeInJob"] is null);

                promotion.Dispose();
                promotion = null;
                await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                checks.Add($"{name}: com a trava solta, a rodada periódica também não sonda (ela nunca inicia processo)", probes == 0);

                // A primeira chamada é o ponto em que o Hub inicia o processo: só aí a sonda corre, uma vez.
                var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var status = hub.StatusJson();
                var pid = status["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (pid != 0)
                {
                    children.Add(pid);
                }

                children.AddRange(ProcessesOn(endpoint));
                checks.Add(
                    $"{name}: a primeira chamada inicia o Gateway, sonda uma vez e o inicia fora do job",
                    ResultText(call) == "ok:test_tool" &&
                    probes == 1 &&
                    Text(status["backends"]?[0]?["gateway"]?["startMode"]) == "outside-job" &&
                    Flag(status["childWouldBeInJob"]) == true &&
                    Text(status["outsideJobPolicy"]) == "auto");
            }
            finally
            {
                promotion?.Dispose();
            }
        }

        try
        {
            await ScenarioAsync("Hub já em job desde a partida", inJobFromTheStart: true);
            await ScenarioAsync("Hub posto no job depois de subir", inJobFromTheStart: false);
        }
        finally
        {
            foreach (var id in children.Where(id => id > 0).Distinct())
            {
                KillOurs(id, testStart);
            }

            DeleteQuietly(root);
        }

        return checks.Done();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Quedas para o início normal
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record FallbackOutcome(
        string? Mode,
        int ProcessId,
        bool Alive,
        bool InHubTree,
        bool? InJobReported,
        bool? InJobSystem,
        string Stderr,
        double ElapsedMilliseconds,
        int WmiCalls,
        Exception? Failure,
        IReadOnlyList<int> LoggedStarts,
        int LoggedAlive);

    private static async Task<FallbackOutcome> RunFallbackAsync(
        string root,
        Func<string, string?, WmiCreateOutcome> wmi,
        Action<OutsideJobStarter>? tune = null,
        Dictionary<string, string>? environment = null,
        string? command = null)
    {
        var port = ReserveFreePort();
        var (file, prefix) = SelfTestChildCommand();
        var backend = new BackendConfig
        {
            Id = "fb",
            Enabled = true,
            Startup = new StartupConfig
            {
                Command = command ?? file,
                Arguments = $"{prefix}{SelfTestListener.Switch} {port} 60",
                WorkingDirectory = root,
                Environment = environment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            }
        };
        var calls = 0;
        var starter = NewStarter(
            root,
            OutsideJobPolicy.Always,
            (commandLine, directory) =>
            {
                Interlocked.Increment(ref calls);
                return wmi(commandLine, directory);
            });
        tune?.Invoke(starter);
        await using var supervisor = new ProcessSupervisor(backend) { OutsideJob = starter };
        using var stderr = new StderrCapture();
        var watch = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            await supervisor.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var elapsed = watch.Elapsed.TotalMilliseconds;
        var processId = supervisor.ProcessId ?? 0;
        bool? system = null;
        if (processId != 0)
        {
            using var process = Process.GetProcessById(processId);
            system = ProcessSupervisor.IsInJob(process);
        }

        // The processes that started say so in the file the profile names (GXHUB_TEST_START_LOG): how many Gateways this
        // attempt made, and how many of them are alive now. A moment is given to one that is slow to say it.
        var logged = new List<int>();
        if (backend.Startup.Environment.TryGetValue(StartLogVariable, out var startLog))
        {
            await Task.Delay(500);
            logged.AddRange(ReadStartLog(startLog));
        }

        return new FallbackOutcome(
            supervisor.StartMode,
            processId,
            supervisor.StartedByHub,
            processId != 0 && ProcessTree.IsSameOrDescendant(Environment.ProcessId, processId) == true,
            supervisor.StartedInJob,
            system,
            stderr.Text(),
            elapsed,
            calls,
            failure,
            logged,
            logged.Count(IsAlive));
    }

    // The start lines of a stderr text, as the backend id leaves them: "[id] iniciado pelo hub, PID n mode=..." without the
    // "[id] " and the line break.
    private static List<string> StartLines(string stderr) =>
        stderr.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith('[') && line.Contains("] iniciado pelo hub, PID ", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..])
            .ToList();

    // The queda is told twice: the warning of what failed, and the start line, which carries the mode and the short
    // reason as its last fields.
    private static bool IsFallback(FallbackOutcome outcome, string code) =>
        outcome.Mode == StartModes.HubTreeFallback &&
        outcome.Alive &&
        outcome.InHubTree &&
        outcome.InJobReported == outcome.InJobSystem &&
        outcome.Failure is null &&
        outcome.Stderr.Contains($"início fora do job não concluído ({code})", StringComparison.Ordinal) &&
        StartLines(outcome.Stderr).Contains($"iniciado pelo hub, PID {outcome.ProcessId} mode=hub-tree-fallback reason={code}");

    private static async Task<bool> OutsideJobFallsBackWhenWmiFailsTestAsync()
    {
        var checks = new Checks("queda: WMI");
        var root = NewTempRoot("GxMcpHub-fallback-wmi-test-");
        var standIns = new List<int>();
        var before = ProcessSupervisor.TotalStarts;
        try
        {
            var refused = await RunFallbackAsync(root, (_, _) => new WmiCreateOutcome(false, 0, "wmi-refused-2"));
            checks.Add(
                "o WMI recusa: cai na hora, sem esperar o orçamento",
                IsFallback(refused, "wmi-refused-2") && refused.WmiCalls == 1 && refused.ElapsedMilliseconds < 3000);

            var unavailable = await RunFallbackAsync(root, (_, _) => new WmiCreateOutcome(false, 0, "wmi-unavailable"));
            checks.Add("o WMI não existe: cai na hora", IsFallback(unavailable, "wmi-unavailable") && unavailable.ElapsedMilliseconds < 3000);

            var throwing = await RunFallbackAsync(root, (_, _) => throw new InvalidOperationException("WMI sintético que falha"));
            checks.Add("o WMI levanta exceção: cai na hora", IsFallback(throwing, "wmi-error") && throwing.ElapsedMilliseconds < 3000);

            // Não volta no prazo: a queda vem no prazo, e o lançador que o serviço criar depois é encerrado.
            var lateStandIn = -1;
            var lateStart = DateTime.UtcNow;
            var hung = await RunFallbackAsync(
                root,
                (_, _) =>
                {
                    Thread.Sleep(1500);
                    var standIn = StartStandIn(ReserveFreePort(), 60, standIns);
                    lateStandIn = standIn.Id;
                    return new WmiCreateOutcome(true, standIn.Id, null);
                },
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(300);
                    starter.Grace = TimeSpan.FromMilliseconds(100);
                });
            checks.Add(
                "o WMI não responde no prazo: a queda vem no fim do orçamento, nem antes nem muito depois",
                IsFallback(hung, "wmi-timeout") && hung.ElapsedMilliseconds is >= 350 and < 1400);
            checks.Add(
                "o lançador criado depois do prazo é encerrado",
                await WaitUntilAsync(() => lateStandIn > 0 && !IsAlive(lateStandIn), TimeSpan.FromSeconds(10)));

            // Nenhum arquivo de resposta sobra na pasta launch.
            var launchFolder = Path.Combine(root, "GxMcpHub", "launch");
            checks.Add(
                "nenhuma resposta sobrou",
                !Directory.Exists(launchFolder) ||
                Directory.GetFiles(launchFolder, "*" + LaunchSpec.ResultExtension).Length == 0);
            checks.Add("cada queda iniciou exatamente um processo", ProcessSupervisor.TotalStarts - before == 4);
            _ = lateStart;

            // Respostas e temporários de mais de uma hora são apagados na próxima tentativa; o que é recente, ou não é
            // resposta nem temporário, fica.
            Directory.CreateDirectory(launchFolder);
            var oldResult = Path.Combine(launchFolder, "velha.result");
            var oldTemporary = Path.Combine(launchFolder, "velha.result.tmp");
            var freshResult = Path.Combine(launchFolder, "recente.result");
            var otherFile = Path.Combine(launchFolder, "outro.txt");
            foreach (var path in new[] { oldResult, oldTemporary, freshResult, otherFile })
            {
                File.WriteAllText(path, "{}");
            }

            foreach (var path in new[] { oldResult, oldTemporary, otherFile })
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(2));
            }

            await RunFallbackAsync(root, (_, _) => new WmiCreateOutcome(false, 0, "wmi-refused-2"));
            checks.Add(
                "respostas velhas são apagadas, as recentes e os outros arquivos ficam",
                !File.Exists(oldResult) && !File.Exists(oldTemporary) && File.Exists(freshResult) && File.Exists(otherFile));
        }
        finally
        {
            KillLeftovers(standIns);
            DeleteQuietly(root);
        }

        return checks.Done();
    }

    private static async Task<bool> OutsideJobFallsBackWhenTheResultFailsTestAsync()
    {
        var checks = new Checks("queda: resultado");
        var root = NewTempRoot("GxMcpHub-fallback-result-test-");
        var standIns = new List<int>();
        try
        {
            // O lançador está vivo e nunca responde: o Hub espera o prazo e o encerra, com o que ele tenha iniciado.
            Process? silent = null;
            var noResult = await RunFallbackAsync(
                root,
                (_, _) =>
                {
                    silent = StartStandIn(ReserveFreePort(), 60, standIns);
                    return new WmiCreateOutcome(true, silent.Id, null);
                },
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(500);
                    starter.Grace = TimeSpan.FromMilliseconds(300);
                });
            checks.Add("sem resultado no prazo", IsFallback(noResult, "no-result") && noResult.ElapsedMilliseconds >= 700);
            checks.Add(
                "o lançador que não respondeu foi encerrado",
                silent is not null && await WaitUntilAsync(() => !IsAlive(standIns[0]), TimeSpan.FromSeconds(10)));

            // O lançador some sem responder: a espera acaba na hora, não no prazo.
            var gone = await RunFallbackAsync(
                root,
                (_, _) =>
                {
                    var (file, prefix) = SelfTestChildCommand();
                    using var quick = Process.Start(new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = prefix + "--help",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    })!;
                    quick.StandardOutput.ReadToEnd();
                    quick.WaitForExit(10000);
                    return new WmiCreateOutcome(true, quick.Id, null);
                },
                starter => starter.Budget = TimeSpan.FromSeconds(20));
            checks.Add("lançador que sumiu sem responder", IsFallback(gone, "no-result") && gone.ElapsedMilliseconds < 8000);

            // O "lançador" que não é o executável do Hub (outro programa, nascido neste lançamento) não é encerrado
            // quando não responde: só o lançador do próprio Hub é.
            Process? stranger = null;
            var notOurs = await RunFallbackAsync(
                root,
                (_, _) =>
                {
                    stranger = Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c ping -n 15 127.0.0.1 > nul",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })!;
                    standIns.Add(stranger.Id);
                    return new WmiCreateOutcome(true, stranger.Id, null);
                },
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(300);
                    starter.Grace = TimeSpan.FromMilliseconds(300);
                });
            checks.Add("outro programa no lugar do lançador: sem resultado, cai no normal", IsFallback(notOurs, "no-result"));
            checks.Add("o programa que não é o executável do Hub não foi encerrado", stranger is not null && IsAlive(stranger.Id));
            try
            {
                stranger?.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            // O lançador diz um PID que não existe.
            var absent = await RunFallbackAsync(
                root,
                (commandLine, _) => WriteResultFor(commandLine, 0x7FFFFF00, DateTime.UtcNow, null));
            checks.Add("PID que não existe", IsFallback(absent, "process-gone"));

            // O lançador diz um PID de um processo que já existia antes do lançamento: não é dele e não é tocado.
            var bystander = StartStandIn(ReserveFreePort(), 60, standIns);
            var bystanderId = bystander.Id;
            var bystanderStart = bystander.StartTime.ToUniversalTime();
            await Task.Delay(700);
            var elder = await RunFallbackAsync(
                root,
                (commandLine, _) => WriteResultFor(commandLine, bystanderId, bystanderStart, null),
                starter => starter.StartTolerance = TimeSpan.FromMilliseconds(300));
            checks.Add("PID de um processo que já existia", IsFallback(elder, "start-time-mismatch"));
            checks.Add("o processo que não é nosso não foi tocado", IsAlive(bystanderId));

            // O serviço devolve o número de um processo que já existia antes do lançamento, do mesmo executável do Hub, e
            // que não responde: nasceu antes deste lançamento, então não é o lançador deste Hub e não é encerrado.
            var elderLauncher = await RunFallbackAsync(
                root,
                (_, _) => new WmiCreateOutcome(true, bystanderId, null),
                starter =>
                {
                    starter.StartTolerance = TimeSpan.FromMilliseconds(300);
                    starter.Budget = TimeSpan.FromMilliseconds(300);
                    starter.Grace = TimeSpan.FromMilliseconds(300);
                });
            checks.Add("lançador que já existia antes do lançamento: sem resultado, cai no normal", IsFallback(elderLauncher, "no-result"));
            checks.Add("o processo anterior ao lançamento, do mesmo executável, não foi encerrado", IsAlive(bystanderId));

            // O processo nasceu agora, mas o instante dito não confere com o dele.
            Process? youngster = null;
            var skewed = await RunFallbackAsync(
                root,
                (commandLine, _) =>
                {
                    youngster = StartStandIn(ReserveFreePort(), 60, standIns);
                    return WriteResultFor(commandLine, youngster.Id, youngster.StartTime.ToUniversalTime() - TimeSpan.FromSeconds(10), null);
                });
            checks.Add("instante que não confere", IsFallback(skewed, "start-time-mismatch"));
            checks.Add("o processo de instante errado não foi tocado", youngster is not null && IsAlive(standIns[^1]));

            // O lançador diz que não conseguiu iniciar o comando (um comando que não existe): a queda também falha,
            // porque o início normal tem o mesmo comando.
            var missing = Path.Combine(root, "nao-existe", "gateway.exe");
            var launcherError = await RunFallbackAsync(root, StartLauncherNormally, command: missing);
            checks.Add(
                "erro do lançador (comando inexistente)",
                launcherError.Failure is Win32Exception &&
                launcherError.Mode is null &&
                !launcherError.Alive &&
                launcherError.Stderr.Contains("início fora do job não concluído (launcher-file-not-found)", StringComparison.Ordinal));

            // Especificação grande demais: nem chama o WMI.
            var big = await RunFallbackAsync(
                root,
                (_, _) => new WmiCreateOutcome(false, 0, "nao-deveria-ser-chamado"),
                environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GXHUB_TEST_GRANDE"] = new string('x', 9000)
                });
            checks.Add("especificação grande demais", IsFallback(big, "spec-too-large") && big.WmiCalls == 0);

            // O código que o lançador escreve só passa limpo: o arquivo pode ser escrito por qualquer processo do mesmo
            // usuário, e o código vai para o stderr e para a linha de início, sem caminho, espaço nem quebra de linha.
            var hostile = await RunFallbackAsync(
                root,
                (commandLine, _) => WriteResultFor(commandLine, 0, DateTime.UtcNow, "Falhou em C:\\Pasta Secreta\\x.exe\r\nlinha falsa"));
            checks.Add(
                "código do lançador com caminho, espaço e quebra de linha: só o código limpo chega",
                IsFallback(hostile, "launcher-falhou-em-c--pasta-secreta-x-exe--linha-") &&
                !hostile.Stderr.Contains("Pasta Secreta", StringComparison.Ordinal));
            checks.Add(
                "código curto: minúsculas, dígitos e hífen, no máximo 40 caracteres, nunca vazio",
                OutsideJobStarter.SafeCode("File-Not-Found") == "file-not-found" &&
                OutsideJobStarter.SafeCode("a b\tc") == "a-b-c" &&
                OutsideJobStarter.SafeCode(new string('x', 100)).Length == 40 &&
                OutsideJobStarter.SafeCode(string.Empty) == "unknown" &&
                OutsideJobStarter.SafeCode(null) == "unknown");

            // Cancelado no meio da espera, com um arquivo de resposta já no disco mas ainda ilegível (o lançador ainda o
            // escreve): a exceção sai e o arquivo não fica.
            var launchFolder = Path.Combine(root, "GxMcpHub", "launch");
            var cancelling = NewStarter(
                root,
                OutsideJobPolicy.Always,
                (commandLine, _) =>
                {
                    var spec = LaunchSpec.Decode(commandLine[(commandLine.LastIndexOf(' ') + 1)..]);
                    File.WriteAllText(spec.ResultPath, "{ ainda escrevendo");
                    var launcherStandIn = StartStandIn(ReserveFreePort(), 60, standIns);
                    return new WmiCreateOutcome(true, launcherStandIn.Id, null);
                });
            var cancelled = false;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            {
                var (cancelFile, cancelPrefix) = SelfTestChildCommand();
                try
                {
                    await cancelling.TryStartAsync(
                        "fb",
                        new StartupConfig { Command = cancelFile, Arguments = cancelPrefix + "--help", WorkingDirectory = root },
                        null,
                        cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
            }

            checks.Add(
                "cancelado no meio da espera: a exceção sai e o arquivo de resposta, ainda ilegível, não fica",
                cancelled && Directory.GetFiles(launchFolder, "*" + LaunchSpec.ResultExtension).Length == 0);
            checks.Add(
                "cancelado no meio da espera: o lançador que o Hub segurava pelo handle é encerrado",
                await WaitUntilAsync(() => !IsAlive(standIns[^1]), TimeSpan.FromSeconds(10)));

            checks.Add(
                "nenhuma resposta sobrou",
                !Directory.Exists(launchFolder) ||
                Directory.GetFiles(launchFolder, "*" + LaunchSpec.ResultExtension).Length == 0);
        }
        finally
        {
            KillLeftovers(standIns);
            DeleteQuietly(root);
        }

        return checks.Done();
    }

    // O "serviço WMI" que, em vez de criar o lançador, deixa a resposta pronta com o que o teste quer que o Hub leia.
    private static WmiCreateOutcome WriteResultFor(string commandLine, int processId, DateTime startUtc, string? error)
    {
        var spec = LaunchSpec.Decode(commandLine[(commandLine.LastIndexOf(' ') + 1)..]);
        LaunchResultFile.Write(spec.ResultPath, processId, startUtc, error);
        return new WmiCreateOutcome(true, processId, null);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Status, Stop e Detach
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<bool> StatusShowsTheStartModeTestAsync()
    {
        var checks = new Checks("status");
        var root = NewTempRoot("GxMcpHub-startmode-test-");
        var children = new List<int>();
        var testStart = DateTime.UtcNow;

        async Task<(JsonNode? Status, JsonNode? Backend, int Pid, string Stderr)> RunAsync(OutsideJobStarter starter, string name)
        {
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var profile = Path.Combine(root, name + ".json");
            WriteProfile(profile, TestProfile(root, port, "native", withStartup: true));
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            using var stderr = new StderrCapture();
            await using var hub = NewHub(profile, script, outsideJob: starter);
            await hub.StartAsync(CancellationToken.None);
            var status = hub.StatusJson();
            var backend = status["backends"]?[0];
            var pid = backend?["processId"]?.GetValue<int>() ?? 0;
            children.Add(pid);
            foreach (var id in ProcessesOn(endpoint))
            {
                children.Add(id);
            }

            return (status, backend, pid, stderr.Text());
        }

        try
        {
            // hub-tree: o início de sempre.
            var usual = await RunAsync(new OutsideJobStarter(OutsideJobPolicy.Never, () => false), "usual");
            checks.Add("hubVersion", Text(usual.Status?["hubVersion"]) == HubInfo.Version && HubInfo.Version == "0.5.0");
            checks.Add(
                "hub-tree",
                Text(usual.Backend?["gateway"]?["startMode"]) == "hub-tree" &&
                Flag(usual.Backend?["gateway"]?["startedByThisHub"]) == true &&
                Text(usual.Status?["outsideJobPolicy"]) == "never");
            checks.Add(
                "hub-tree: a linha de início diz o modo e mais nada",
                StartLines(usual.Stderr).Contains($"iniciado pelo hub, PID {usual.Pid} mode=hub-tree"));
            checks.Add("Hub fora de job: o filho não ficaria em job", Flag(usual.Status?["childWouldBeInJob"]) == false);

            // outside-job: pelo lançador (aqui, iniciado pelo caminho comum no lugar do WMI).
            var outside = await RunAsync(
                NewStarter(root, OutsideJobPolicy.Always, StartLauncherNormally, hubInJob: () => true),
                "fora");
            checks.Add(
                "outside-job",
                Text(outside.Backend?["gateway"]?["startMode"]) == "outside-job" &&
                Flag(outside.Backend?["gateway"]?["startedByThisHub"]) == true &&
                Text(outside.Status?["outsideJobPolicy"]) == "always" &&
                Flag(outside.Backend?["online"]) == true);
            checks.Add(
                "Hub em job que ainda não sondou: childWouldBeInJob nulo",
                outside.Status!.AsObject().ContainsKey("childWouldBeInJob") && outside.Status["childWouldBeInJob"] is null);
            var outsideInJob = outside.Backend?["gateway"]?["startedInJob"];
            checks.Add("startedInJob é o do sistema para o processo adotado", outsideInJob is not null);

            // hub-tree-fallback: o lançador falhou e o início foi o de sempre.
            var fellBack = await RunAsync(
                NewStarter(root, OutsideJobPolicy.Always, (_, _) => new WmiCreateOutcome(false, 0, "wmi-refused-2")),
                "queda");
            checks.Add(
                "hub-tree-fallback",
                Text(fellBack.Backend?["gateway"]?["startMode"]) == "hub-tree-fallback" &&
                Flag(fellBack.Backend?["online"]) == true);
            checks.Add(
                "hub-tree-fallback: a linha de início diz o modo e o motivo curto, sem caminho",
                StartLines(fellBack.Stderr).Contains(
                    $"iniciado pelo hub, PID {fellBack.Pid} mode=hub-tree-fallback reason=wmi-refused-2"));
            checks.Add(
                "outside-job: a linha de início diz o modo e o tempo",
                StartLines(outside.Stderr).Count(line => System.Text.RegularExpressions.Regex.IsMatch(
                    line,
                    $@"^iniciado pelo hub, PID {outside.Pid} mode=outside-job ms=\d+$")) == 1);

            // auto com a sonda que diz sim, no mesmo Hub: o status mostra o que a sonda disse.
            var auto = await RunAsync(
                NewStarter(
                    root,
                    OutsideJobPolicy.Auto,
                    StartLauncherNormally,
                    hubInJob: () => true,
                    probe: _ => Task.FromResult<bool?>(true)),
                "auto");
            checks.Add(
                "auto: sonda disse que o filho ficaria em job",
                Text(auto.Backend?["gateway"]?["startMode"]) == "outside-job" &&
                Text(auto.Status?["outsideJobPolicy"]) == "auto" &&
                Flag(auto.Status?["childWouldBeInJob"]) == true);

            // Um Hub que só conecta a um Gateway que não iniciou não diz nada do modo.
            var port2 = ReserveFreePort();
            var endpoint2 = new Uri($"http://127.0.0.1:{port2}/mcp");
            var profile2 = Path.Combine(root, "dono.json");
            WriteProfile(profile2, TestProfile(root, port2, "native", withStartup: true));
            var script2 = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint2).State == ListenerLookupState.Found
            };
            await using var owner = NewHub(profile2, script2, outsideJob: new OutsideJobStarter(OutsideJobPolicy.Never));
            await owner.StartAsync(CancellationToken.None);
            children.AddRange(ProcessesOn(endpoint2));
            await using var visitor = NewHub(profile2, script2, outsideJob: new OutsideJobStarter(OutsideJobPolicy.Never));
            await visitor.StartAsync(CancellationToken.None);
            var visited = visitor.StatusJson()["backends"]?[0]?["gateway"];
            checks.Add(
                "Gateway que o Hub não iniciou: startMode nulo",
                visited is not null && visited.AsObject().ContainsKey("startMode") && visited["startMode"] is null &&
                Flag(visited["startedByThisHub"]) == false);

            // A política que o Hub lê do ambiente quando ninguém a injeta.
            var previous = Environment.GetEnvironmentVariable(OutsideJobPolicySetting.Variable);
            try
            {
                Environment.SetEnvironmentVariable(OutsideJobPolicySetting.Variable, "always");
                var port3 = ReserveFreePort();
                var profile3 = Path.Combine(root, "ambiente.json");
                WriteProfile(profile3, TestProfile(root, port3, "native", withStartup: false));
                await using var fromEnvironment = new HubService(HubConfigLoader.Load(profile3), keepSharedGateway: true);
                checks.Add("política vinda do ambiente", Text(fromEnvironment.StatusJson()["outsideJobPolicy"]) == "always");
            }
            finally
            {
                Environment.SetEnvironmentVariable(OutsideJobPolicySetting.Variable, previous);
            }
        }
        finally
        {
            foreach (var id in children.Where(id => id > 0).Distinct())
            {
                KillOurs(id, testStart);
            }

            DeleteQuietly(root);
        }

        return checks.Done();
    }

    private static async Task<bool> StopAndDetachOnALaunchedProcessTestAsync()
    {
        var checks = new Checks("Stop e Detach");
        var root = NewTempRoot("GxMcpHub-launched-stop-test-");
        var children = new List<int>();
        var testStart = DateTime.UtcNow;
        var startsBefore = ProcessSupervisor.TotalStarts;

        ProcessSupervisor NewLaunched(int port, bool wrapper, int seconds)
        {
            var (file, prefix) = SelfTestChildCommand();
            var backend = new BackendConfig
            {
                Id = "adotado",
                Enabled = true,
                Startup = new StartupConfig
                {
                    Command = file,
                    Arguments = $"{prefix}{(wrapper ? SelfTestListener.WrapperSwitch : SelfTestListener.Switch)} {port} {seconds}",
                    WorkingDirectory = root
                }
            };
            return new ProcessSupervisor(backend)
            {
                OutsideJob = NewStarter(root, OutsideJobPolicy.Always, StartLauncherNormally),
                ProfileRevision = "ABCDEF123456"
            };
        }

        try
        {
            // Stop: o processo e a árvore dele, que aqui é um wrapper com o listener como filho.
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            await using (var wrapped = NewLaunched(port, wrapper: true, seconds: 90))
            {
                string startedText;
                using (var startCapture = new StderrCapture())
                {
                    await wrapped.StartAsync(CancellationToken.None);
                    startedText = startCapture.Text();
                }

                var wrapperId = wrapped.ProcessId ?? 0;
                children.Add(wrapperId);
                checks.Add(
                    "a linha de início do processo adotado: o formato único, com o modo e o tempo como últimos campos",
                    StartLines(startedText).Count(line => System.Text.RegularExpressions.Regex.IsMatch(
                        line,
                        $@"^iniciado pelo hub, PID {wrapperId} mode=outside-job ms=\d+$")) == 1);
                checks.Add("adotado: iniciado pelo lançador", wrapped.StartMode == StartModes.OutsideJob && wrapped.StartedByHub);
                checks.Add(
                    "adotado: a revisão do perfil de que partiu e o instante do início estão anotados",
                    wrapped.StartedFromProfileRevision == "ABCDEF123456" &&
                    wrapped.StartedUtc is DateTime startedAt && DateTime.UtcNow - startedAt < TimeSpan.FromMinutes(1));
                var listening = await WaitUntilAsync(
                    () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found,
                    TimeSpan.FromSeconds(20));
                var listenerId = ListenerInspector.Find(endpoint).ProcessId ?? 0;
                children.Add(listenerId);
                checks.Add("wrapper: quem escuta é o filho do processo adotado", listening && listenerId != wrapperId &&
                                                                                 ProcessTree.IsSameOrDescendant(wrapperId, listenerId) == true);
                using var stderr = new StderrCapture();
                wrapped.Stop("teste");
                checks.Add(
                    "Stop encerra o processo adotado e a árvore dele",
                    !wrapped.StartedByHub && wrapped.StartMode is null &&
                    await WaitUntilAsync(() => !IsAlive(wrapperId) && !IsAlive(listenerId), TimeSpan.FromSeconds(10)));
                checks.Add("Stop diz o motivo", stderr.Count("encerrando o processo iniciado pelo hub") == 1);
            }

            // Detach: o processo continua, e o Hub o solta.
            var port2 = ReserveFreePort();
            var endpoint2 = new Uri($"http://127.0.0.1:{port2}/mcp");
            await using (var detached = NewLaunched(port2, wrapper: false, seconds: 90))
            {
                await detached.StartAsync(CancellationToken.None);
                var id = detached.ProcessId ?? 0;
                children.Add(id);
                await WaitUntilAsync(
                    () => ListenerInspector.Find(endpoint2).State == ListenerLookupState.Found,
                    TimeSpan.FromSeconds(20));
                using var stderr = new StderrCapture();
                detached.Detach("outro Hub conectado");
                checks.Add(
                    "Detach solta o processo adotado sem encerrá-lo",
                    !detached.StartedByHub && IsAlive(id) &&
                    ListenerInspector.Find(endpoint2).State == ListenerLookupState.Found &&
                    stderr.Count("Gateway mantido em execução ao encerrar o Hub") == 1);
            }

            checks.Add(
                "o processo solto segue de pé depois do Dispose do supervisor",
                children.Count > 2 && IsAlive(children[^1]));

            // O aviso de saída de um processo adotado, uma vez só, com o código.
            var port3 = ReserveFreePort();
            using (var stderr = new StderrCapture())
            {
                await using var brief = NewLaunched(port3, wrapper: false, seconds: 2);
                await brief.StartAsync(CancellationToken.None);
                children.Add(brief.ProcessId ?? 0);
                var logged = await WaitUntilAsync(
                    () => stderr.Count("processo finalizado com código 0") >= 1,
                    TimeSpan.FromSeconds(20));
                await Task.Delay(300);
                checks.Add("o fim do processo adotado é avisado uma vez, com o código", logged && stderr.Count("processo finalizado com código 0") == 1);
                checks.Add("StartedByHub volta a falso quando o processo adotado sai", !brief.StartedByHub && brief.StartMode is null);
            }

            checks.Add("cada processo adotado conta como um início", ProcessSupervisor.TotalStarts - startsBefore == 3);
        }
        finally
        {
            foreach (var id in children.Where(id => id > 0).Distinct())
            {
                KillOurs(id, testStart);
            }

            DeleteQuietly(root);
        }

        return checks.Done();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // O serviço WMI de verdade e os jobs
    // ---------------------------------------------------------------------------------------------------------------

    private static bool RealWmiCreatesTheProcessOutsideTheTreeTest()
    {
        var checks = new Checks("WMI de verdade");
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var root = NewTempRoot("GxMcpHub-wmi-test-");
        var created = new List<int>();
        var testStart = DateTime.UtcNow;
        try
        {
            var (file, prefix) = SelfTestChildCommand();
            var dump = Path.Combine(root, "visto.json");
            WmiCreateOutcome outcome = default;
            var thread = new Thread(() => outcome = WmiProcessCreator.Create(
                $"\"{file}\" {prefix}{OutsideJobSelfTestModes.EnvDumpSwitch} \"{dump}\" 20 TEMP;GXHUB_TEST_SECRET",
                root));
            thread.SetApartmentState(ApartmentState.STA);
            Environment.SetEnvironmentVariable("GXHUB_TEST_SECRET", "segredo");
            try
            {
                thread.Start();
                thread.Join();
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXHUB_TEST_SECRET", null);
            }

            checks.Add(
                "flags de produção: janela oculta e sem janela (CREATE_NO_WINDOW com CREATE_NEW_PROCESS_GROUP, que o serviço exige)",
                WmiProcessCreator.ShowWindowHidden == 0 && WmiProcessCreator.CreateFlagsNoWindow == (0x08000000u | 0x00000200u));
            checks.Add("o WMI criou o processo", outcome.Ok && outcome.ProcessId > 0 && outcome.Code is null);
            if (outcome.Ok)
            {
                created.Add(outcome.ProcessId);
                checks.Add(
                    "o processo não é da árvore do teste",
                    ProcessTree.IsSameOrDescendant(Environment.ProcessId, outcome.ProcessId) != true);
                var wrote = SpinWait.SpinUntil(() => File.Exists(dump), TimeSpan.FromSeconds(20));
                checks.Add("o processo rodou e escreveu o que viu", wrote);
                if (wrote)
                {
                    var seen = ReadWhenWhole(dump, TimeSpan.FromSeconds(10));
                    using var self = Process.GetCurrentProcess();
                    checks.Add(
                        "no usuário e na sessão do Hub",
                        string.Equals(seen?["user"]?.GetValue<string>(), Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
                        seen?["session"]?.GetValue<int>() == self.SessionId);
                    checks.Add("no diretório pedido", string.Equals(seen?["cwd"]?.GetValue<string>(), root, StringComparison.OrdinalIgnoreCase));
                    checks.Add("em job nenhum", seen?["inJob"]?.GetValue<bool>() == false);
                    checks.Add("sem o ambiente de quem pediu", seen?["vars"]?["GXHUB_TEST_SECRET"] is null);
                }
            }

            // O serviço recusa com o código dele: diretório que não existe.
            WmiCreateOutcome refused = default;
            var second = new Thread(() => refused = WmiProcessCreator.Create(
                $"\"{file}\" {prefix}--help",
                Path.Combine(root, "nao", "existe")));
            second.SetApartmentState(ApartmentState.STA);
            second.Start();
            second.Join();
            checks.Add(
                "o WMI recusa com o código dele",
                !refused.Ok && refused.Code is not null && refused.Code.StartsWith("wmi-refused-", StringComparison.Ordinal));
            if (refused.Ok)
            {
                created.Add(refused.ProcessId);
            }

            // Fora de uma thread STA o que acontece é informação, não exigência: a implementação usa STA.
            WmiCreateOutcome fromPool = default;
            var pooled = new Thread(() => fromPool = WmiProcessCreator.Create($"\"{file}\" {prefix}--help", root));
            pooled.SetApartmentState(ApartmentState.MTA);
            pooled.Start();
            pooled.Join();
            if (fromPool.Ok)
            {
                created.Add(fromPool.ProcessId);
            }

            Console.Error.WriteLine($"[self-test] WMI numa thread MTA: ok={fromPool.Ok} código={fromPool.Code ?? "-"}.");
        }
        finally
        {
            foreach (var id in created)
            {
                KillOurs(id, testStart);
            }

            DeleteQuietly(root);
        }

        return checks.Done();
    }

    private sealed class HelperInJob : IDisposable
    {
        public HelperInJob(Process helper, IntPtr job, Dictionary<string, string> report, int port)
        {
            Helper = helper;
            Job = job;
            Report = report;
            Port = port;
        }

        public Process Helper { get; }

        public IntPtr Job { get; private set; }

        public Dictionary<string, string> Report { get; }

        public int Port { get; }

        public int ChildId => int.TryParse(Report.GetValueOrDefault("pid"), out var id) ? id : 0;

        // Fechar o job é o que o cliente faz: o que está nele morre, o que saiu dele não.
        public void CloseJob()
        {
            if (Job != IntPtr.Zero)
            {
                NativeHandles.CloseHandle(Job);
                Job = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            CloseJob();
            try
            {
                if (!Helper.HasExited)
                {
                    Helper.Kill(entireProcessTree: false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            Helper.Dispose();
        }
    }

    // Um "Hub" de teste dentro de um job criado aqui. Null quando o sistema não deixou criar ou usar o job.
    private static async Task<HelperInJob?> StartHelperInJobAsync(
        uint limitFlags,
        string policy,
        string root,
        string? extra = null)
    {
        var port = ReserveFreePort();
        var job = NativeHandles.CreateJob(limitFlags);
        if (job == IntPtr.Zero)
        {
            return null;
        }

        Process? helper = null;
        try
        {
            var (file, prefix) = SelfTestChildCommand();
            var startInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = $"{prefix}{OutsideJobSelfTestModes.ReportSwitch} {port} 120 {policy}" +
                            (extra is null ? string.Empty : " " + extra),
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["GXHUB_TEST_SECRET"] = "segredo-do-cliente";
            helper = Process.Start(startInfo);
            if (helper is null || !NativeHandles.AssignProcessToJobObject(job, helper.Handle))
            {
                NativeHandles.CloseHandle(job);
                helper?.Dispose();
                return null;
            }

            helper.ErrorDataReceived += (_, _) => { };
            helper.BeginErrorReadLine();
            var line = helper.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(60))) != line ||
                line.Result is not string text ||
                !text.StartsWith(OutsideJobSelfTestModes.ReportReady, StringComparison.Ordinal))
            {
                NativeHandles.CloseHandle(job);
                helper.Dispose();
                return null;
            }

            var report = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in text[OutsideJobSelfTestModes.ReportReady.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = part.IndexOf('=');
                if (at > 0)
                {
                    report[part[..at]] = part[(at + 1)..];
                }
            }

            return new HelperInJob(helper, job, report, port);
        }
        catch
        {
            NativeHandles.CloseHandle(job);
            helper?.Dispose();
            return null;
        }
    }

    private static bool InJob(int processId, IntPtr job)
    {
        using var process = Process.GetProcessById(processId);
        return NativeHandles.IsProcessInJob(process.Handle, job, out var inside) && inside;
    }

    private static async Task<bool> HubInAKillOnCloseJobStartsOutsideItTestAsync()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            return true;
        }

        var checks = new Checks("job KILL_ON_JOB_CLOSE");
        var root = NewTempRoot("GxMcpHub-killjob-test-");
        var testStart = DateTime.UtcNow;
        var children = new List<int>();
        try
        {
            // auto: o filho normal ficaria no job, então o Hub sai dele pelo lançador (WMI de verdade).
            using (var auto = await StartHelperInJobAsync(NativeHandles.JobLimitKillOnJobClose, "auto", root))
            {
                if (auto is null)
                {
                    Console.Error.WriteLine("[self-test] job KILL_ON_JOB_CLOSE: o ambiente não deixou criar o job de teste; não provado aqui.");
                    return true;
                }

                children.Add(auto.ChildId);
                checks.Add("auto: o Hub de teste está no job", InJob(auto.Helper.Id, auto.Job));
                checks.Add(
                    "auto: iniciado fora do job, a sonda disse que o filho ficaria nele",
                    auto.Report.GetValueOrDefault("mode") == "outside-job" &&
                    auto.Report.GetValueOrDefault("inJob") == "false" &&
                    auto.Report.GetValueOrDefault("child") == "true" &&
                    auto.Report.GetValueOrDefault("policy") == "auto");
                checks.Add("auto: o filho não está no job de teste", !InJob(auto.ChildId, auto.Job));
                using (var child = Process.GetProcessById(auto.ChildId))
                {
                    var any = NativeHandles.IsProcessInJob(child.Handle, IntPtr.Zero, out var inAny) && inAny;
                    checks.Add("auto: o filho não está em job algum", !any);
                    checks.Add("auto: o filho não é da árvore do Hub de teste", ProcessTree.IsSameOrDescendant(auto.Helper.Id, auto.ChildId) != true);
                }

                var endpoint = new Uri($"http://127.0.0.1:{auto.Port}/mcp");
                checks.Add(
                    "auto: o filho escuta",
                    await WaitUntilAsync(() => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found, TimeSpan.FromSeconds(20)));

                auto.CloseJob();
                checks.Add("auto: fechar o job encerra o Hub de teste", auto.Helper.WaitForExit(15000));
                await Task.Delay(2500);
                checks.Add(
                    "auto: o filho sobrevive ao fechamento do job e segue escutando",
                    IsAlive(auto.ChildId) && ListenerInspector.Find(endpoint).State == ListenerLookupState.Found);
            }

            // never: o controle. O filho entra no job e morre com ele.
            using (var never = await StartHelperInJobAsync(NativeHandles.JobLimitKillOnJobClose, "never", root))
            {
                if (never is null)
                {
                    return checks.Done();
                }

                children.Add(never.ChildId);
                checks.Add(
                    "never: início de hoje, o filho está no job",
                    never.Report.GetValueOrDefault("mode") == "hub-tree" &&
                    never.Report.GetValueOrDefault("inJob") == "true" &&
                    InJob(never.ChildId, never.Job));
                never.CloseJob();
                checks.Add("never: o filho morre com o job", WaitForExit(never.ChildId, TimeSpan.FromSeconds(15)));
            }
        }
        finally
        {
            foreach (var id in children.Where(id => id > 0))
            {
                KillOurs(id, testStart);
            }

            DeleteQuietly(root);
        }

        return checks.Done();
    }

    private static async Task<bool> HubInASilentBreakawayJobTestAsync()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            return true;
        }

        var checks = new Checks("job com saída silenciosa");
        var root = NewTempRoot("GxMcpHub-silentjob-test-");
        var testStart = DateTime.UtcNow;
        var children = new List<int>();
        try
        {
            // Job com saída silenciosa: o filho normal já nasce fora dele, então o auto não precisa do WMI.
            using (var silent = await StartHelperInJobAsync(
                       NativeHandles.JobLimitKillOnJobClose | NativeHandles.JobLimitSilentBreakawayOk,
                       "auto",
                       root))
            {
                if (silent is null)
                {
                    Console.Error.WriteLine("[self-test] job com saída silenciosa: o ambiente não deixou criar o job de teste; não provado aqui.");
                    return true;
                }

                children.Add(silent.ChildId);
                // A saída silenciosa é só deste job: se quem roda o autoteste já está num job que segura os filhos, o
                // filho do Hub de teste fica nesse e a sonda, com razão, manda sair. Então o que se confere é a
                // coerência (sonda e modo concordam) e, quando não há job do ambiente, o início normal.
                using var self = Process.GetCurrentProcess();
                var ambient = ProcessSupervisor.IsInJob(self) == true;
                var mode = silent.Report.GetValueOrDefault("mode");
                var probe = silent.Report.GetValueOrDefault("child");
                checks.Add("auto: o filho não está no job de saída silenciosa", !InJob(silent.ChildId, silent.Job));
                checks.Add(
                    "auto: sonda e modo concordam (job que segura o filho: fora do job; senão início normal)",
                    probe is "true" or "false" && (probe == "true") == (mode == "outside-job"));
                checks.Add(
                    "auto: iniciado dentro da árvore do Hub de teste exatamente quando o modo é o normal",
                    ProcessTree.IsSameOrDescendant(silent.Helper.Id, silent.ChildId) == (mode == "hub-tree"));
                if (ambient)
                {
                    Console.Error.WriteLine("[self-test] job com saída silenciosa: este processo já está num job do ambiente; o início normal sem job algum não foi provado aqui.");
                }
                else
                {
                    checks.Add("auto: sem job do ambiente, a saída silenciosa dá início normal", mode == "hub-tree" && probe == "false");
                }
            }

            // always: sai do job mesmo sem job nenhum (diagnóstico), com o Hub de teste fora de qualquer job do teste.
            var plainRoot = Path.Combine(root, "sem-job");
            Directory.CreateDirectory(plainRoot);
            using (var always = await StartHelperInJobAsync(NativeHandles.JobLimitSilentBreakawayOk, "always", plainRoot))
            {
                if (always is not null)
                {
                    children.Add(always.ChildId);
                    checks.Add(
                        "always: sai do job sem sondar",
                        always.Report.GetValueOrDefault("mode") == "outside-job" &&
                        always.Report.GetValueOrDefault("inJob") == "false" &&
                        always.Report.GetValueOrDefault("child") == "null" &&
                        !InJob(always.ChildId, always.Job));
                }
            }

            // wrapper: o filho direto do lançador é o wrapper e o Gateway é neto; os dois ficam fora do job e vivos.
            using (var wrapper = await StartHelperInJobAsync(NativeHandles.JobLimitKillOnJobClose, "auto", root, "wrapper"))
            {
                if (wrapper is not null)
                {
                    var wrapperId = wrapper.ChildId;
                    children.Add(wrapperId);
                    var endpoint = new Uri($"http://127.0.0.1:{wrapper.Port}/mcp");
                    var listening = await WaitUntilAsync(
                        () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found,
                        TimeSpan.FromSeconds(20));
                    var listenerId = ListenerInspector.Find(endpoint).ProcessId ?? 0;
                    children.Add(listenerId);
                    checks.Add(
                        "wrapper: iniciado fora do job, o Gateway é neto do processo adotado",
                        wrapper.Report.GetValueOrDefault("mode") == "outside-job" && listening && listenerId != wrapperId &&
                        ProcessTree.IsSameOrDescendant(wrapperId, listenerId) == true &&
                        !InJob(wrapperId, wrapper.Job) && !InJob(listenerId, wrapper.Job));
                    wrapper.CloseJob();
                    wrapper.Helper.WaitForExit(15000);
                    await Task.Delay(2500);
                    checks.Add("wrapper: o wrapper e o Gateway sobrevivem ao fechamento do job", IsAlive(wrapperId) && IsAlive(listenerId));
                }
            }

            // O que o Gateway vê: o padrão do usuário e o do perfil, não o do Hub.
            var dump = Path.Combine(root, "gateway-visto.json");
            using (var seen = await StartHelperInJobAsync(NativeHandles.JobLimitKillOnJobClose, "auto", root, "envdump:" + dump))
            {
                if (seen is not null)
                {
                    children.Add(seen.ChildId);
                    var wrote = await WaitUntilAsync(() => File.Exists(dump), TimeSpan.FromSeconds(30));
                    checks.Add("ambiente: o processo iniciado fora do job escreveu o que viu", wrote);
                    if (wrote)
                    {
                        await Task.Delay(200);
                        var visible = ReadWhenWhole(dump, TimeSpan.FromSeconds(10));
                        using var self = Process.GetCurrentProcess();
                        checks.Add(
                            "ambiente: o do perfil e a revisão do perfil chegam",
                            visible?["vars"]?["GXHUB_TEST_FROM_PROFILE"]?.GetValue<string>() == "perfil" &&
                            visible["vars"]?["GXMCP_HUB_PROFILE_REVISION"]?.GetValue<string>() == "0123456789AB");
                        checks.Add("ambiente: o do Hub não chega", visible!["vars"]?["GXHUB_TEST_SECRET"] is null);
                        checks.Add(
                            "ambiente: o diretório é o do perfil, o usuário e a sessão são os do Hub, há PATH, em job nenhum",
                            string.Equals(visible["cwd"]?.GetValue<string>(), root, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(visible["user"]?.GetValue<string>(), Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
                            visible["session"]?.GetValue<int>() == self.SessionId &&
                            visible["pathLength"]?.GetValue<int>() > 0 &&
                            visible["inJob"]?.GetValue<bool>() == false);
                    }
                }
            }
        }
        finally
        {
            foreach (var id in children.Where(id => id > 0).Distinct())
            {
                KillOurs(id, testStart);
            }

            DeleteQuietly(root);
        }

        return checks.Done();
    }
}
