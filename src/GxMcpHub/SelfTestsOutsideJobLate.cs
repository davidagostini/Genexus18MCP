using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GxMcpHub;

// 0.4.0 v2: nenhuma queda para o início comum deixa para trás um Gateway que o lançador já iniciou ou possa iniciar.
// O orçamento do caminho fora do job (a chamada ao WMI, a resposta do lançador e a adoção) tem um instante final,
// dito ao lançador na especificação (notAfterUtc): o lançador criado depois dele não inicia nada, e o Hub só cai no início
// comum depois dele (mais uma tolerância), tenha ou não voltado a chamada ao WMI. O que o lançador criou e o Hub não vai
// adotar é encerrado pelo handle conferido, e o que não confere nunca é tocado. Os processos que cada cenário inicia
// dizem que iniciaram num arquivo (GXHUB_TEST_START_LOG), para contar quantos Gateways uma tentativa fez.
internal static partial class SelfTests
{
    private const string StartLogVariable = "GXHUB_TEST_START_LOG";

    private static Dictionary<string, string> StartLogEnvironment(string path) =>
        new(StringComparer.OrdinalIgnoreCase) { [StartLogVariable] = path };

    private static List<int> ReadStartLog(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path).Select(line => int.TryParse(line, out var id) ? id : 0).Where(id => id > 0).ToList()
            : new List<int>();

    // O que o lançador faz com a especificação que o Hub pôs na linha de comando (o mesmo comando, argumentos, diretório e
    // ambiente), sem lançador nenhum no meio: o "Gateway" que um lançador teria deixado para trás.
    private static Process StartGatewayLikeTheLauncher(string commandLine, List<int> created)
    {
        var spec = LaunchSpec.Decode(commandLine[(commandLine.LastIndexOf(' ') + 1)..]);
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.File,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var pair in spec.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(startInfo)!;
        created.Add(process.Id);
        return process;
    }

    // O processo de teste escreve o arquivo com File.WriteAllText: ele existe antes de estar inteiro, e fica preso enquanto é
    // escrito. Lê-se de novo até vir legível e inteiro (ou até o prazo).
    private static JsonNode? ReadWhenWhole(string path, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is { } node)
                {
                    return node;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                // Ainda sendo escrito.
            }

            if (DateTime.UtcNow >= until)
            {
                return null;
            }

            Thread.Sleep(20);
        }
    }

    private static string ResultPathOf(string commandLine) =>
        LaunchSpec.Decode(commandLine[(commandLine.LastIndexOf(' ') + 1)..]).ResultPath;

    // O número de um processo que já saiu: o "lançador" que o serviço criou e que foi embora nesse meio-tempo.
    private static int StartedAndFinishedProcessId()
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
        return quick.Id;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // O prazo, o orçamento e os padrões
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<bool> LateLauncherStartsNothingTestAsync()
    {
        var checks = new Checks("lançador tardio");
        var root = NewTempRoot("GxMcpHub-late-test-");
        var standIns = new List<int>();
        try
        {
            var (file, prefix) = SelfTestChildCommand();
            var launch = Path.Combine(root, "GxMcpHub", LaunchSpec.LaunchFolderName);
            Directory.CreateDirectory(launch);

            // Os padrões: orçamento de 15 s, tolerância de 1 s, sonda de 5 s (o pior caso do início fica abaixo do que o
            // kit espera de um início).
            var defaults = new OutsideJobStarter(OutsideJobPolicy.Auto);
            checks.Add(
                "padrões: orçamento 15 s, tolerância 1 s, depois da saída do lançador 1 s, sonda 5 s",
                defaults.Budget == TimeSpan.FromSeconds(15) && defaults.Grace == TimeSpan.FromSeconds(1) &&
                defaults.LauncherExitGrace == TimeSpan.FromSeconds(1) && defaults.ProbeTimeout == TimeSpan.FromSeconds(5));

            // A especificação leva o prazo, e ele volta igual; sem prazo (um Hub anterior) não há prazo; um texto que não
            // é um instante é recusado.
            var notAfter = new DateTime(2026, 11, 16, 20, 30, 45, 123, DateTimeKind.Utc);
            var resultPath = Path.Combine(launch, "x.result");
            var withDeadline = LaunchSpec.Decode(new LaunchSpec("a.exe", string.Empty, null, new Dictionary<string, string>(), resultPath, notAfter).Encode());
            var withoutDeadline = LaunchSpec.Decode(new LaunchSpec("a.exe", string.Empty, null, new Dictionary<string, string>(), resultPath).Encode());
            var invalidDeadline = false;
            try
            {
                LaunchSpec.Decode(EncodeSpec(new JsonObject
                {
                    ["v"] = 1, ["file"] = "a.exe", ["args"] = string.Empty, ["env"] = new JsonObject(),
                    ["result"] = resultPath, ["notAfterUtc"] = "ontem à tarde"
                }));
            }
            catch (LaunchSpecException ex)
            {
                invalidDeadline = ex.Code == "invalid-spec";
            }

            checks.Add(
                "especificação: o prazo ida e volta, ausente é nulo, texto que não é instante é recusado",
                withDeadline.NotAfterUtc == notAfter && withoutDeadline.NotAfterUtc is null && invalidDeadline);

            // O Hub põe na especificação o instante da chamada mais o orçamento.
            var seen = new List<LaunchSpec>();
            var before = DateTime.UtcNow;
            var asked = await RunFallbackAsync(
                root,
                (commandLine, _) =>
                {
                    seen.Add(LaunchSpec.Decode(commandLine[(commandLine.LastIndexOf(' ') + 1)..]));
                    return new WmiCreateOutcome(false, 0, "wmi-refused-2");
                });
            var after = DateTime.UtcNow;
            checks.Add(
                "o Hub diz ao lançador o instante da chamada mais o orçamento",
                IsFallback(asked, "wmi-refused-2") && seen.Count == 1 &&
                seen[0].NotAfterUtc is DateTime told &&
                told >= before + TimeSpan.FromSeconds(15) && told <= after + TimeSpan.FromSeconds(15));

            // O lançador de verdade, com o prazo vencido: recusa, diz launcher-late, sai com o código próprio e não inicia nada.
            var startLog = Path.Combine(root, "starts.log");
            var lateResult = Path.Combine(launch, Guid.NewGuid().ToString("N") + LaunchSpec.ResultExtension);
            var lateSpec = new LaunchSpec(
                file,
                $"{prefix}{SelfTestListener.Switch} {ReserveFreePort()} 30",
                root,
                StartLogEnvironment(startLog),
                lateResult,
                DateTime.UtcNow - TimeSpan.FromSeconds(1));
            var (lateExit, lateAnswer) = RunLauncherProcess(lateSpec.Encode(), lateResult);
            await Task.Delay(700);
            checks.Add(
                "prazo vencido: o lançador sai com o código próprio e responde launcher-late, sem processo",
                lateExit == 3 && DetachedLauncher.ExitLate == 3 && lateAnswer?.Error == "launcher-late" &&
                DetachedLauncher.LateCode == "launcher-late" && lateAnswer.ProcessId is null);
            checks.Add("prazo vencido: nada foi iniciado", ReadStartLog(startLog).Count == 0);

            // No prazo, e sem prazo, o mesmo lançador inicia (o controle: a recusa não é o lançador recusando sempre).
            foreach (var deadline in new DateTime?[] { DateTime.UtcNow + TimeSpan.FromSeconds(60), null })
            {
                var okResult = Path.Combine(launch, Guid.NewGuid().ToString("N") + LaunchSpec.ResultExtension);
                var okSpec = new LaunchSpec(
                    file,
                    $"{prefix}{SelfTestListener.Switch} {ReserveFreePort()} 30",
                    root,
                    StartLogEnvironment(startLog),
                    okResult,
                    deadline);
                var (okExit, okAnswer) = RunLauncherProcess(okSpec.Encode(), okResult);
                if (okAnswer?.ProcessId is int started)
                {
                    standIns.Add(started);
                }

                checks.Add(
                    deadline is null ? "sem prazo: inicia" : "dentro do prazo: inicia",
                    okExit == 0 && okAnswer?.Error is null && okAnswer?.ProcessId is not null);
            }

            await Task.Delay(500);
            checks.Add("os dois que iniciaram disseram que iniciaram", ReadStartLog(startLog).Count == 2);

            // As respostas que estes lançadores de mão deixaram na pasta são deles, não do Hub: saem daqui, para que o que
            // sobrar depois do cenário do WMI lento seja só o que o Hub deixou.
            foreach (var handMade in Directory.GetFiles(launch, "*" + LaunchSpec.ResultExtension))
            {
                File.Delete(handMade);
            }

            // O motivo da queda do lançador tardio é launcher-late (nunca launcher-launcher-late), e os outros códigos
            // seguem como eram.
            checks.Add(
                "motivos: launcher-late não é repetido, os outros códigos levam o prefixo",
                OutsideJobStarter.LauncherReason("launcher-late") == "launcher-late" &&
                OutsideJobStarter.LauncherReason("file-not-found") == "launcher-file-not-found" &&
                OutsideJobStarter.LauncherReason(null) == "launcher-unknown");

            // Só um executável dado pelo caminho completo é a imagem do processo; um nome ou um script, não.
            checks.Add(
                "comando que é a imagem: só .exe e .com por caminho completo",
                OutsideJobStarter.CommandNamesItsImage(@"C:\g\GxMcp.Gateway.exe") &&
                OutsideJobStarter.CommandNamesItsImage(@"C:\g\GxMcp.Gateway.EXE") &&
                OutsideJobStarter.CommandNamesItsImage(@"C:\g\gateway.com") &&
                !OutsideJobStarter.CommandNamesItsImage("powershell.exe") &&
                !OutsideJobStarter.CommandNamesItsImage(@"C:\g\iniciar.cmd") &&
                !OutsideJobStarter.CommandNamesItsImage(@"C:\g\iniciar.bat") &&
                !OutsideJobStarter.CommandNamesItsImage(@"g\GxMcp.Gateway.exe"));
            checks.Add(
                "o mesmo arquivo com caixa e barras diferentes é o mesmo; outro arquivo, ou vazio, não",
                ProcessNative.SamePath(@"C:\Windows\System32\cmd.exe", "c:/windows/system32/CMD.EXE") &&
                !ProcessNative.SamePath(@"C:\Windows\System32\cmd.exe", @"C:\Windows\System32\conhost.exe") &&
                !ProcessNative.SamePath(string.Empty, @"C:\Windows\System32\cmd.exe") &&
                !ProcessNative.SamePath(null, null));

            // Um WMI mais lento que o orçamento: a queda vem no fim dele (não antes), com o motivo wmi-timeout, e o
            // lançador que o serviço cria depois recusa e não deixa segundo Gateway na porta. O número que a chamada
            // devolve não é o do lançador (não há o que o Hub possa encerrar): só o prazo do próprio lançador o contém.
            var lateLauncherId = 0;
            var slow = await RunFallbackAsync(
                root,
                (commandLine, directory) =>
                {
                    Thread.Sleep(1300);
                    lateLauncherId = StartLauncherNormally(commandLine, directory).ProcessId;
                    return new WmiCreateOutcome(true, 0x7FFFFF00, null);
                },
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(400);
                    starter.Grace = TimeSpan.FromMilliseconds(200);
                    starter.LateAnswerCleanup = TimeSpan.FromMilliseconds(2500);
                },
                StartLogEnvironment(Path.Combine(root, "slow.log")));
            checks.Add(
                "WMI mais lento que o orçamento: cai só no fim dele, com wmi-timeout",
                IsFallback(slow, "wmi-timeout") && slow.ElapsedMilliseconds is >= 550 and < 1250);
            // O lançador só nasce quando a chamada lenta volta, depois da queda: espera-se por ele, e por ele sair.
            var lateStarted = await WaitUntilAsync(() => Volatile.Read(ref lateLauncherId) != 0, TimeSpan.FromSeconds(10));
            var lateExited = lateStarted && WaitForExit(Volatile.Read(ref lateLauncherId), TimeSpan.FromSeconds(20));
            await Task.Delay(500);
            var slowStarts = ReadStartLog(Path.Combine(root, "slow.log"));
            standIns.AddRange(slowStarts);
            checks.Add(
                "o lançador criado tarde recusa: só o Gateway do próprio Hub foi iniciado",
                lateExited && slowStarts.Count == 1 && slowStarts[0] == slow.ProcessId);
            checks.Add(
                "a resposta launcher-late que o lançador tardio escreveu é apagada pelo Hub",
                await WaitUntilAsync(
                    () => Directory.GetFiles(launch, "*" + LaunchSpec.ResultExtension).Length == 0,
                    TimeSpan.FromSeconds(10)));
        }
        finally
        {
            KillLeftovers(standIns);
            DeleteQuietly(root);
        }

        return checks.Done();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Esperar a resposta até o fim do orçamento
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<bool> HubWaitsForTheAnswerUntilTheEndOfTheBudgetTestAsync()
    {
        var checks = new Checks("espera da resposta");
        var root = NewTempRoot("GxMcpHub-budget-test-");
        var standIns = new List<int>();
        try
        {
            // A chamada ao WMI ainda não voltou, mas o lançador já respondeu: o Hub adota o Gateway sem esperar a chamada
            // nem cair no início comum.
            var pending = await RunFallbackAsync(
                root,
                (commandLine, directory) =>
                {
                    var outcome = StartLauncherNormally(commandLine, directory);
                    Thread.Sleep(3500);
                    return outcome;
                });
            checks.Add(
                "chamada ao WMI pendente, lançador que respondeu: adotado, sem esperar a chamada",
                pending.Mode == StartModes.OutsideJob && pending.Alive && pending.ElapsedMilliseconds < 3000);

            // A chamada falhou depois de o serviço poder ter criado o processo (falha incerta): o Hub espera a resposta e
            // adota, em vez de cair no início comum com um segundo Gateway.
            var uncertain = await RunFallbackAsync(
                root,
                (commandLine, directory) =>
                {
                    StartLauncherNormally(commandLine, directory);
                    return new WmiCreateOutcome(false, 0, "wmi-error", Uncertain: true);
                });
            checks.Add("falha incerta da chamada: espera a resposta e adota", uncertain.Mode == StartModes.OutsideJob && uncertain.Alive);

            // O lançador que o serviço devolveu não pôde ser aberto (acesso negado: o processo System, número 4): não se
            // sabe se saiu, e a espera não acaba antes do fim do orçamento.
            var unreachable = await RunFallbackAsync(
                root,
                (_, _) => new WmiCreateOutcome(true, 4, null),
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(400);
                    starter.Grace = TimeSpan.FromMilliseconds(200);
                    starter.LauncherExitGrace = TimeSpan.FromMilliseconds(100);
                });
            checks.Add(
                "lançador que não pode ser aberto: não se conclui que saiu, a espera vai até o fim do orçamento",
                IsFallback(unreachable, "no-result") && unreachable.ElapsedMilliseconds >= 550);

            // A falha incerta sem lançador nenhum: cai no fim do orçamento, com o código da falha.
            var neverCame = await RunFallbackAsync(
                root,
                (_, _) => new WmiCreateOutcome(false, 0, "wmi-error", Uncertain: true),
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(500);
                    starter.Grace = TimeSpan.FromMilliseconds(200);
                });
            checks.Add(
                "falha incerta sem lançador: cai no fim do orçamento, com wmi-error",
                IsFallback(neverCame, "wmi-error") && neverCame.ElapsedMilliseconds >= 650);

            // A resposta existe mas está presa por um instante logo depois de colocada (um antivírus): depois de o lançador
            // ter saído, o Hub continua lendo e adota o Gateway.
            var locked = await RunFallbackAsync(
                root,
                (commandLine, directory) =>
                {
                    var gateway = StartGatewayLikeTheLauncher(commandLine, standIns);
                    var path = ResultPathOf(commandLine);
                    LaunchResultFile.Write(path, gateway.Id, gateway.StartTime.ToUniversalTime(), null);
                    var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        hold.Dispose();
                    });
                    return new WmiCreateOutcome(true, StartedAndFinishedProcessId(), null);
                });
            checks.Add(
                "resposta presa por 300 ms depois de o lançador sair: lida e adotada",
                locked.Mode == StartModes.OutsideJob && locked.Alive && standIns.Count > 0 && locked.ProcessId == standIns[^1]);

            // Um arquivo aberto para escrita por outro processo (compartilhando tudo) não esconde a resposta.
            var shared = Path.Combine(root, "partilhado.result");
            LaunchResultFile.Write(shared, 4242, DateTime.UtcNow, null);
            using (new FileStream(shared, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                checks.Add(
                    "arquivo aberto por outro para escrita: a resposta é lida na hora",
                    LaunchResultFile.TryRead(shared) is { ProcessId: 4242 });
            }
        }
        finally
        {
            KillLeftovers(standIns);
            DeleteQuietly(root);
        }

        return checks.Done();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A queda não deixa Gateway para trás
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<bool> FallbackLeavesNoGatewayBehindTestAsync()
    {
        var checks = new Checks("sem Gateway para trás");
        var root = NewTempRoot("GxMcpHub-nogateway-test-");
        var standIns = new List<int>();
        try
        {
            // A resposta diz o número de um Gateway que o lançador iniciou mas não diz quando nasceu (resposta que não dá
            // para adotar): o Gateway, conferido (nasceu neste lançamento, a imagem é o comando), é encerrado antes da
            // queda, e depois dela há um só Gateway, o do Hub.
            Process? first = null;
            var invalid = await RunFallbackAsync(
                root,
                (commandLine, _) =>
                {
                    first = StartGatewayLikeTheLauncher(commandLine, standIns);
                    LaunchResultFile.Write(ResultPathOf(commandLine), first.Id, null, null);
                    return new WmiCreateOutcome(true, StartedAndFinishedProcessId(), null);
                },
                environment: StartLogEnvironment(Path.Combine(root, "invalid.log")));
            checks.Add(
                "resposta sem o instante: o Gateway que o lançador iniciou é encerrado e há um só depois da queda",
                IsFallback(invalid, "invalid-result") && first is not null && !IsAlive(first.Id) && invalid.LoggedAlive == 1);

            // O Gateway passou em tudo e a adoção falha: é encerrado, não deixado na porta sem dono.
            Process? second = null;
            ProcessSupervisor.AdoptionFailureForTests = _ => true;
            FallbackOutcome adoption;
            try
            {
                adoption = await RunFallbackAsync(
                    root,
                    (commandLine, _) =>
                    {
                        second = StartGatewayLikeTheLauncher(commandLine, standIns);
                        LaunchResultFile.Write(ResultPathOf(commandLine), second.Id, second.StartTime.ToUniversalTime(), null);
                        return new WmiCreateOutcome(true, StartedAndFinishedProcessId(), null);
                    },
                    environment: StartLogEnvironment(Path.Combine(root, "adopt.log")));
            }
            finally
            {
                ProcessSupervisor.AdoptionFailureForTests = null;
            }

            checks.Add(
                "adoção que falha: o Gateway é encerrado e há um só depois da queda",
                IsFallback(adoption, "adopt-failed") && second is not null && !IsAlive(second.Id) && adoption.LoggedAlive == 1);

            // A imagem não é a do comando (um processo qualquer nascido na janela, com o instante certo): não é adotado e
            // não é tocado.
            Process? stranger = null;
            var image = await RunFallbackAsync(
                root,
                (commandLine, _) =>
                {
                    stranger = Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c ping -n 20 127.0.0.1 > nul",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })!;
                    standIns.Add(stranger.Id);
                    LaunchResultFile.Write(ResultPathOf(commandLine), stranger.Id, stranger.StartTime.ToUniversalTime(), null);
                    return new WmiCreateOutcome(true, StartedAndFinishedProcessId(), null);
                },
                environment: StartLogEnvironment(Path.Combine(root, "image.log")));
            checks.Add(
                "imagem que não é a do comando: não adotado e não encerrado",
                IsFallback(image, "image-mismatch") && stranger is not null && IsAlive(stranger.Id) &&
                image.LoggedStarts.Count == 1);

            // Um comando que não é o caminho de um executável (um nome que se procura) só confere o instante: o mesmo
            // processo estranho, agora sob um comando por nome, passa pela imagem e é adotado se o instante bate (o limite
            // que o desenho aceita, dito no SECURITY).
            checks.Add(
                "só o instante confere para um comando por nome",
                !OutsideJobStarter.CommandNamesItsImage("powershell.exe"));

            // O lançador que ninguém viu responder e ainda está de pé no fim do orçamento é encerrado pelo handle; o
            // Gateway que ele poderia ter iniciado depois não existe, porque o prazo dele passou.
            var silent = await RunFallbackAsync(
                root,
                (_, _) =>
                {
                    var standIn = StartStandIn(ReserveFreePort(), 60, standIns);
                    return new WmiCreateOutcome(true, standIn.Id, null);
                },
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(400);
                    starter.Grace = TimeSpan.FromMilliseconds(200);
                },
                StartLogEnvironment(Path.Combine(root, "silent.log")));
            checks.Add(
                "lançador mudo no fim do orçamento: encerrado pelo handle, e a queda vem depois",
                IsFallback(silent, "no-result") && !IsAlive(standIns[^1]) && silent.ElapsedMilliseconds >= 550);
        }
        finally
        {
            ProcessSupervisor.AdoptionFailureForTests = null;
            KillLeftovers(standIns);
            DeleteQuietly(root);
        }

        return checks.Done();
    }
}
