using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GxMcpHub;

// 0.4.0 v3: o lançador que o Hub desiste de esperar e encerra leva consigo o que ele iniciou. Um lançador que passou na
// conferência do prazo pouco antes do instante pode demorar, entre iniciar o processo e anotar a resposta (antivírus, EDR,
// carga), mais do que a tolerância: o Hub então o encerra pelo handle conferido, espera a saída pelo mesmo handle e encerra
// o que nasceu dele (o Gateway que ele já iniciou e não anotou), varredura a varredura, e só então cai no início comum.
// Os testes seguram o lançador de verdade num atraso que só os autotestes sabem pedir (o segundo parâmetro da linha de
// comando, que o Hub nunca escreve e uma especificação não carrega).
internal static partial class SelfTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int which, IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    // O lançador de verdade, com a especificação que o Hub montou, e o atraso de autoteste entre iniciar o processo e anotar
    // a resposta (o segundo parâmetro da linha de comando, que o Hub nunca escreve).
    private static Process StartDelayedLauncher(string commandLine, string? directory, int delayMilliseconds, List<int> created)
    {
        var spec = commandLine[(commandLine.LastIndexOf(' ') + 1)..];
        var (hubFile, hubPrefix) = OutsideJobStarter.DefaultHubCommand();
        var launcher = Process.Start(new ProcessStartInfo
        {
            FileName = hubFile,
            Arguments = $"{hubPrefix}{DetachedLauncher.Switch} {spec} {DetachedLauncher.DelayAfterStartSwitch} {delayMilliseconds}",
            WorkingDirectory = directory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        created.Add(launcher.Id);
        return launcher;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // O lançador lento: encerrado com o que iniciou
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<bool> SlowLauncherLeavesNoGatewayBehindTestAsync()
    {
        var checks = new Checks("lançador lento");
        var root = NewTempRoot("GxMcpHub-slowlauncher-test-");
        var standIns = new List<int>();
        try
        {
            // O lançador de verdade, com a especificação de verdade, que passa na conferência do prazo, inicia o Gateway e só
            // anota a resposta 9 s depois (o atraso é um parâmetro que só o autoteste dá). O Hub chega ao fim do orçamento
            // sem resposta: encerra o lançador e o Gateway que ele iniciou, e há um só Gateway depois da queda.
            Process? launcher = null;
            var slow = await RunFallbackAsync(
                root,
                (commandLine, directory) =>
                {
                    var spec = commandLine[(commandLine.LastIndexOf(' ') + 1)..];
                    var (hubFile, hubPrefix) = OutsideJobStarter.DefaultHubCommand();
                    launcher = Process.Start(new ProcessStartInfo
                    {
                        FileName = hubFile,
                        Arguments = $"{hubPrefix}{DetachedLauncher.Switch} {spec} {DetachedLauncher.DelayAfterStartSwitch} 9000",
                        WorkingDirectory = directory ?? string.Empty,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })!;
                    standIns.Add(launcher.Id);
                    return new WmiCreateOutcome(true, launcher.Id, null);
                },
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(3000);
                    starter.Grace = TimeSpan.FromMilliseconds(300);
                    starter.LauncherExitWait = TimeSpan.FromSeconds(3);
                    starter.SweepPause = TimeSpan.FromMilliseconds(50);
                },
                StartLogEnvironment(Path.Combine(root, "slow.log")));
            var gateways = slow.LoggedStarts.Where(id => id != slow.ProcessId).ToList();
            standIns.AddRange(gateways);
            checks.Add(
                "o Hub cai no início comum com no-result e o Gateway dele segue",
                IsFallback(slow, "no-result") && slow.ElapsedMilliseconds >= 3250);
            checks.Add(
                "o lançador que demorava a anotar foi encerrado",
                launcher is not null && !IsAlive(launcher.Id));
            checks.Add(
                "o Gateway que o lançador iniciou e não anotou foi encerrado, e há um só vivo",
                gateways.Count == 1 && !IsAlive(gateways[0]) && slow.LoggedAlive == 1);

            // O lançador que nada encerra (algo o protege): o Hub avisa (launcher-unkillable), espera só o prazo curto e
            // segue para o início comum.
            OwnedProcess.TerminateSuppressedForTests = () => true;
            FallbackOutcome unkillable;
            try
            {
                unkillable = await RunFallbackAsync(
                    root,
                    (_, _) => new WmiCreateOutcome(true, StartStandIn(ReserveFreePort(), 60, standIns).Id, null),
                    starter =>
                    {
                        starter.Budget = TimeSpan.FromMilliseconds(400);
                        starter.Grace = TimeSpan.FromMilliseconds(200);
                        starter.LauncherExitWait = TimeSpan.FromMilliseconds(700);
                        starter.SweepPause = TimeSpan.FromMilliseconds(20);
                    });
            }
            finally
            {
                OwnedProcess.TerminateSuppressedForTests = null;
            }

            checks.Add(
                "lançador que não morre: avisa launcher-unkillable, espera só o prazo curto e cai no início comum",
                IsFallback(unkillable, "no-result") &&
                unkillable.Stderr.Contains("launcher-unkillable", StringComparison.Ordinal) &&
                unkillable.ElapsedMilliseconds >= 1250 &&
                IsAlive(standIns[^1]));

            // A chamada ao WMI só volta depois do prazo, mas a resposta já estava lá e o Hub adotou o Gateway: o lançador que
            // a chamada devolve não é tocado (o que ele iniciou é do Hub agora).
            Process? lateLauncher = null;
            var lateReturned = false;
            var adoptedRun = await RunFallbackAsync(
                root,
                (commandLine, _) =>
                {
                    var gateway = StartGatewayLikeTheLauncher(commandLine, standIns);
                    LaunchResultFile.Write(ResultPathOf(commandLine), gateway.Id, gateway.StartTime.ToUniversalTime(), null);
                    lateLauncher = StartStandIn(ReserveFreePort(), 60, standIns);
                    Thread.Sleep(1500);
                    Volatile.Write(ref lateReturned, true);
                    return new WmiCreateOutcome(true, lateLauncher.Id, null);
                },
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(400);
                    starter.Grace = TimeSpan.FromMilliseconds(100);
                    starter.LateAnswerCleanup = TimeSpan.FromMilliseconds(100);
                });
            await WaitUntilAsync(() => Volatile.Read(ref lateReturned), TimeSpan.FromSeconds(10));
            await Task.Delay(900);
            checks.Add(
                "chamada que volta depois do prazo numa tentativa adotada: o lançador devolvido não é tocado",
                adoptedRun.Mode == StartModes.OutsideJob && lateLauncher is not null && IsAlive(lateLauncher.Id));

            // Cancelado enquanto o lançador lento segura o Gateway que já iniciou: o lançador e o Gateway são encerrados (a
            // árvore, não só o lançador), e o Hub, que está saindo, não deixa um Gateway sem dono.
            var cancelLog = Path.Combine(root, "cancel.log");
            Process? cancelledLauncher = null;
            var cancelStarter = NewStarter(
                root,
                OutsideJobPolicy.Always,
                (commandLine, directory) =>
                {
                    cancelledLauncher = StartDelayedLauncher(commandLine, directory, 12000, standIns);
                    return new WmiCreateOutcome(true, cancelledLauncher.Id, null);
                });
            cancelStarter.Budget = TimeSpan.FromSeconds(30);
            cancelStarter.LauncherExitWait = TimeSpan.FromSeconds(3);
            cancelStarter.SweepPause = TimeSpan.FromMilliseconds(50);
            var (cancelFile, cancelPrefix) = SelfTestChildCommand();
            using (var cancellation = new CancellationTokenSource())
            {
                var attempt = cancelStarter.TryStartAsync(
                    "fb",
                    new StartupConfig
                    {
                        Command = cancelFile,
                        Arguments = $"{cancelPrefix}{SelfTestListener.Switch} {ReserveFreePort()} 60",
                        WorkingDirectory = root,
                        Environment = StartLogEnvironment(cancelLog)
                    },
                    null,
                    cancellation.Token);
                var started = await WaitUntilAsync(() => ReadStartLog(cancelLog).Count == 1, TimeSpan.FromSeconds(20));
                cancellation.Cancel();
                try
                {
                    await attempt;
                }
                catch (OperationCanceledException)
                {
                    // O esperado.
                }

                var cancelledGateways = ReadStartLog(cancelLog);
                standIns.AddRange(cancelledGateways);
                checks.Add(
                    "cancelado com o lançador lento: o lançador e o Gateway que ele iniciou são encerrados",
                    started && cancelledLauncher is not null && !IsAlive(cancelledLauncher.Id) &&
                    cancelledGateways.Count == 1 && !IsAlive(cancelledGateways[0]));
            }

            // A chamada ao WMI só volta DEPOIS de o Hub desistir, e o lançador que ela devolve é lento e segura o Gateway que
            // já iniciou: a continuação o encerra com a árvore. Até lá o Gateway do lançador segura a porta e o do Hub
            // (que subiu na queda) a perde e sai: a janela estreita que a documentação diz; depois da continuação o
            // Gateway do lançador não existe mais.
            Process? stuckLauncher = null;
            var stuckReturned = false;
            var stuckLog = Path.Combine(root, "stuck.log");
            var stuck = await RunFallbackAsync(
                root,
                (commandLine, directory) =>
                {
                    stuckLauncher = StartDelayedLauncher(commandLine, directory, 12000, standIns);
                    Thread.Sleep(2500);
                    Volatile.Write(ref stuckReturned, true);
                    return new WmiCreateOutcome(true, stuckLauncher.Id, null);
                },
                starter =>
                {
                    starter.Budget = TimeSpan.FromMilliseconds(1000);
                    starter.Grace = TimeSpan.FromMilliseconds(200);
                    starter.LauncherExitWait = TimeSpan.FromSeconds(3);
                    starter.SweepPause = TimeSpan.FromMilliseconds(50);
                    starter.LateAnswerCleanup = TimeSpan.FromMilliseconds(100);
                },
                StartLogEnvironment(stuckLog));
            await WaitUntilAsync(() => Volatile.Read(ref stuckReturned), TimeSpan.FromSeconds(15));
            await Task.Delay(1500);
            var stuckGateways = ReadStartLog(stuckLog).Where(id => id != stuck.ProcessId).ToList();
            standIns.AddRange(stuckGateways);
            checks.Add(
                "chamada tardia que devolve um lançador lento: a continuação o encerra com o Gateway que ele iniciou",
                stuck.Stderr.Contains("início fora do job não concluído (wmi-timeout)", StringComparison.Ordinal) &&
                stuckLauncher is not null && !IsAlive(stuckLauncher.Id) &&
                stuckGateways.Count == 1 && !IsAlive(stuckGateways[0]));
        }
        finally
        {
            OwnedProcess.TerminateSuppressedForTests = null;
            KillLeftovers(standIns);
            DeleteQuietly(root);
        }

        return checks.Done();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A varredura dos descendentes
    // ---------------------------------------------------------------------------------------------------------------

    private static Task<bool> LauncherReaperSweepsDescendantsTestAsync()
    {
        var checks = new Checks("varredura dos descendentes");
        var created = new List<int>();
        try
        {
            // Um processo que nasceu ANTES do lançador e cujo pai registrado é o número do lançador (o número foi reaproveitado):
            // não é filho dele e não pode ser tocado.
            var older = StartStandIn(ReserveFreePort(), 60, created);
            Thread.Sleep(300);
            var launcherProcess = StartStandIn(ReserveFreePort(), 60, created);
            Thread.Sleep(100);
            var child = StartStandIn(ReserveFreePort(), 60, created);
            var grandchild = StartStandIn(ReserveFreePort(), 60, created);
            var late = StartStandIn(ReserveFreePort(), 60, created);

            using var launcher = OwnedProcess.OpenIfOurs(
                launcherProcess.Id,
                DateTime.UtcNow - TimeSpan.FromMinutes(5),
                SelfTestChildCommand().Command,
                out var state);
            checks.Add("o lançador de teste foi aberto e conferido", launcher is not null && state == LauncherState.Ours);
            if (launcher is null)
            {
                return Task.FromResult(checks.Done());
            }

            // Os retratos dos processos: o filho e o neto desde o primeiro; o que só aparece depois (outra varredura), no
            // segundo; o mais velho aparece sempre como filho do lançador.
            var calls = 0;
            IReadOnlyDictionary<int, int>? Picture()
            {
                calls++;
                var parents = new Dictionary<int, int>
                {
                    [child.Id] = launcherProcess.Id,
                    [grandchild.Id] = child.Id,
                    [older.Id] = launcherProcess.Id
                };
                if (calls >= 2)
                {
                    parents[late.Id] = launcherProcess.Id;
                }

                return parents;
            }

            var exited = LauncherReaper.StopWithDescendants(
                launcher,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(20),
                Picture);
            checks.Add("o lançador foi encerrado e a saída foi vista pelo handle", exited && !IsAlive(launcherProcess.Id));
            checks.Add("o filho foi encerrado", WaitForExit(child.Id, TimeSpan.FromSeconds(10)));
            checks.Add("o neto (filho do filho) foi encerrado", WaitForExit(grandchild.Id, TimeSpan.FromSeconds(10)));
            checks.Add("o que só apareceu na varredura seguinte foi encerrado", WaitForExit(late.Id, TimeSpan.FromSeconds(10)));
            checks.Add("o que nasceu antes do lançador, com o pai registrado igual, não foi tocado", IsAlive(older.Id));
            checks.Add(
                "varreu até uma varredura sem ninguém novo: três retratos, e nem mais que o teto",
                calls == 3 && LauncherReaper.MaxSweeps == 5);
        }
        finally
        {
            KillLeftovers(created);
        }

        return Task.FromResult(checks.Done());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Os sobreviventes das mutações: o que fecha cada um
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<bool> SurvivingMutantsAreClosedTestAsync()
    {
        var checks = new Checks("sobreviventes das mutações");
        var root = NewTempRoot("GxMcpHub-survivors-test-");
        try
        {
            // A chamada ao WMI roda numa thread STA (a prática dos hospedeiros de script; o serviço também funciona em MTA,
            // medido, mas o desenho a fixa).
            ApartmentState? apartment = null;
            var thread = await RunFallbackAsync(
                root,
                (_, _) =>
                {
                    apartment = Thread.CurrentThread.GetApartmentState();
                    return new WmiCreateOutcome(false, 0, "wmi-refused-2");
                });
            checks.Add("o WMI é chamado numa thread STA", IsFallback(thread, "wmi-refused-2") && apartment == ApartmentState.STA);

            // Os objetos COM do WMI são soltos ao fim da chamada: depois dela nenhum serve mais (o WMI de verdade, com um
            // comando que sai na hora).
            if (OperatingSystem.IsWindows())
            {
                var (file, prefix) = SelfTestChildCommand();
                var separated = new List<bool>();
                var outcome = WmiProcessCreator.Create(
                    $"\"{file}\" {prefix}--help",
                    root,
                    afterRelease: objects =>
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            return;
                        }

                        foreach (var item in objects)
                        {
                            try
                            {
                                var pointer = Marshal.GetIUnknownForObject(item);
                                Marshal.Release(pointer);
                                separated.Add(false);
                            }
                            catch (InvalidComObjectException)
                            {
                                separated.Add(true);
                            }
                        }
                    });
                checks.Add(
                    "o WMI de verdade criou o processo e todos os objetos COM foram soltos",
                    outcome.Ok && separated.Count >= 8 && separated.All(value => value));
            }

            // Os três canais padrão perdem a herança mesmo que a varredura não faça nada: cada um é o que o sistema dá para o
            // canal certo (entrada, saída, erro).
            var previousSweep = StandardHandles.LastSweep;
            foreach (var which in new[] { -10, -11, -12 })
            {
                var original = GetStdHandle(which);
                using var ours = File.OpenHandle(
                    Path.Combine(root, $"canal{which}.txt"),
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                var handle = ours.DangerousGetHandle();
                try
                {
                    SetHandleInformation(handle, 1, 1);
                    SetStdHandle(which, handle);
                    StandardHandles.StopInheriting(() => new HandleSweep(StandardHandles.PathNone, 0, 0, 0));
                    var cleared = GetHandleInformation(handle, out var flags) && (flags & 1) == 0;
                    checks.Add($"o canal padrão {which} perde a herança sem a varredura", cleared);
                }
                finally
                {
                    SetStdHandle(which, original);
                }
            }

            StandardHandles.LastSweep = previousSweep;
            checks.Add(
                "sem canal ou com INVALID_HANDLE_VALUE nada é feito",
                !StandardHandles.StopInheriting(IntPtr.Zero) && !StandardHandles.StopInheriting(new IntPtr(-1)));

            // O identificador do TEMP: o que o desenho diz (SHA-256 do caminho completo, sem a barra final e em maiúsculas,
            // doze dígitos), e a premissa de que o GetTempPath já vem normalizado (por isso o GetFullPath que o segue não
            // muda nada, e a mutação que o tira é equivalente).
            var temp = Environment.GetEnvironmentVariable("TEMP");
            var tmp = Environment.GetEnvironmentVariable("TMP");
            try
            {
                var odd = Path.Combine(root, "a", "..", "b").Replace('\\', '/');
                Directory.CreateDirectory(Path.Combine(root, "b"));
                Environment.SetEnvironmentVariable("TEMP", odd);
                Environment.SetEnvironmentVariable("TMP", odd);
                var reported = Path.GetTempPath();
                var expected = Convert.ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                Path.GetFullPath(reported)
                                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    .ToUpperInvariant())))[..12]
                    .ToLowerInvariant();
                checks.Add(
                    "o identificador do TEMP é o que o desenho diz",
                    HubPresence.TempRootId() == expected && reported == Path.GetFullPath(reported) &&
                    !reported.Contains("..", StringComparison.Ordinal));
            }
            finally
            {
                Environment.SetEnvironmentVariable("TEMP", temp);
                Environment.SetEnvironmentVariable("TMP", tmp);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }

        return checks.Done();
    }
}
