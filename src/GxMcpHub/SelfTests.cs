using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace GxMcpHub;

internal static partial class SelfTests
{
    private const int TestTimeoutSeconds = 90;

    // only: fragmentos de nome; com ele, rodam só os testes cujo nome contém algum fragmento. É para a rodada de
    // mutações, que confere um teste de cada vez; a validação de um pacote roda tudo, sem filtro.
    public static int Run(IReadOnlyList<string>? only = null)
    {
        // The suite works in a folder of its own inside the TEMP, which becomes the TEMP of this process and of the
        // processes it starts, and is removed at the end: the startup leases, the presences and the folders that the
        // tests make never reach the real TEMP, where the kit enumerates the leases of the Hubs.
        var original = Path.GetTempPath();
        var oldTmp = Environment.GetEnvironmentVariable("TMP");
        var oldTemp = Environment.GetEnvironmentVariable("TEMP");
        string? privateTemp = null;
        try
        {
            privateTemp = Path.Combine(original, "GxMcpHub-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(privateTemp);
            Environment.SetEnvironmentVariable("TMP", privateTemp);
            Environment.SetEnvironmentVariable("TEMP", privateTemp);
            _originalTemp = original;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            privateTemp = null;
            Console.Error.WriteLine($"[self-test] pasta temporária própria não criada ({ex.GetType().Name}); usando o TEMP como está.");
        }

        try
        {
            return RunTests(only);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            if (privateTemp is not null)
            {
                RemovePrivateTemp(privateTemp);
            }
        }
    }

    // Whatever a test left behind goes with the folder; a process that is still closing a file is waited for.
    private static void RemovePrivateTemp(string path)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }

        Console.Error.WriteLine("[self-test] a pasta temporária própria não pôde ser removida.");
    }

    private static int RunTests(IReadOnlyList<string>? only)
    {
        var tests = new (string Name, Func<Task<bool>> Run)[]
        {
            ("Envelope MCP de texto", () => Task.FromResult(ToolTextEnvelopeTest())),
            ("Clone de envelopes sem compartilhamento de parent", () => Task.FromResult(ToolTextCloneTest())),
            ("Trava impede duas instâncias escritoras para a mesma KB", () => Task.FromResult(WriterLeaseTest())),
            ("Modo open-writer permite múltiplas instâncias para a mesma KB",
                () => Task.FromResult(OpenWriterLeaseTest())),
            ("Inicialização concorrente serializa a criação do backend compartilhado", BackendStartupLeaseTestAsync),
            ("Reconexão automática após expiração da sessão MCP HTTP", SessionRecoveryTestAsync),
            ("Status online exige resposta MCP atual do backend", BackendHealthProbeTestAsync),
            ("gxhub_refresh recarrega o perfil sem substituir o Hub STDIO", ProfileHotReloadTestAsync),
            ("O Gateway iniciado pelo Hub recebe a revisão do perfil sem aceitar valor vindo do próprio perfil",
                () => Task.FromResult(GatewayEnvironmentCarriesTheProfileRevisionTest())),
            ("gxhub_status mostra se o perfil carregado declara os pins de KB e versão, sem repetir valores",
                PinsStatusFollowsTheLoadedProfileTestAsync),
            ("Nomes de variável repetidos em outra caixa não derrubam a inicialização e o status de pins os enxerga",
                () => Task.FromResult(StartupEnvironmentIgnoresNameCaseTest())),
            ("O processo de backend recebe entrada padrão própria e não herda o canal MCP do Hub",
                () => Task.FromResult(BackendProcessGetsItsOwnStandardInputTest())),
            ("A tabela TCP do sistema diz qual processo escuta a porta do backend (PID e criação), sem tráfego MCP",
                () => Task.FromResult(ListenerInspectorFindsTheProcessBehindAPortTest())),
            ("profileApplied: revisão para o Gateway deste Hub, instante de criação para o de outro lançador",
                () => Task.FromResult(GatewayProfileEvidenceTest())),
            ("Perfil editado no disco é recarregado sozinho quando não há Gateway iniciado por este Hub",
                ProfileIsReloadedAutomaticallyWithoutALiveChildTestAsync),
            ("Com Gateway iniciado por este Hub em execução a recarga fica adiada e só o gxhub_refresh o reinicia",
                ReloadIsDeferredWhileOwnGatewayRunsTestAsync),
            ("Perfil que troca nome ou política de escrita não é recarregado sozinho e o Hub segue de pé",
                IncompatibleProfileIsNotLoadedTestAsync),
            ("JSON de perfil inválido ou incompleto não é recarregado e não derruba o Hub",
                InvalidProfileIsNotLoadedTestAsync),
            ("Gateway substituído ou encerrado: o Hub reconecta sem iniciar processo e avisa a mudança de tools",
                BackendIsReconnectedWhenTheListenerChangesTestAsync),
            ("Reconexão automática tem prazo: quem aceita a conexão e não responde MCP não prende o Hub",
                AutomaticHandshakeIsBoundedTestAsync),
            ("Chamada a backend offline inicia o Gateway pelo perfil atual uma vez e despacha uma única vez",
                OfflineBackendIsStartedOnDemandByACallTestAsync),
            ("Início sob demanda que não se completa vira erro da chamada e o Hub segue respondendo",
                FailedOnDemandStartIsAnErrorForTheCallTestAsync),
            ("A rodada periódica pula enquanto há chamada em curso, recarrega depois e para no encerramento",
                PeriodicReconcileSkipsWhileACallIsInFlightTestAsync),
            ("GXMCP_HUB_AUTO_RECONCILE desligado mantém o comportamento anterior: nada recarrega nem inicia sozinho",
                AutoReconcileCanBeSwitchedOffTestAsync),
            ("No STDIO o aviso de mudança de tools vindo da reconciliação só sai depois do initialize do cliente",
                ToolsChangedNotificationWaitsForTheClientInitializeTestAsync),
            ("A trava de promoção só vale com o arquivo aberto sem compartilhamento; ausente, livre ou ilegível não",
                () => Task.FromResult(PromotionLockProbeTest())),
            ("Promoção em andamento no início do Hub: sobe sem esperar, backend offline e nenhum processo iniciado",
                PromotionAtHubStartLeavesTheBackendOfflineTestAsync),
            ("Promoção em andamento numa chamada ou no gxhub_refresh: espera, não inicia e devolve o código",
                PromotionMakesCallsAndRefreshWaitAndThenRefuseTestAsync),
            ("A chave GXMCP_HUB_AUTO_RECONCILE desligada não desliga a trava de promoção",
                PromotionLockIsNotSwitchedOffTestAsync),
            ("Trava solta durante a espera com o perfil reescrito: o backend é iniciado uma vez, pelo perfil novo",
                BackendStartsOnceFromTheNewProfileAfterThePromotionTestAsync),
            ("Arquivo da trava ausente, ou presente sem ninguém segurando, não muda o início do backend",
                UnheldPromotionLockFileHasNoEffectTestAsync),
            ("Gateway já escutando com promoção em andamento: o Hub conecta sem consultar a trava",
                ListeningGatewayIsConnectedWithoutAskingForTheLockTestAsync),
            ("Perfil alterado no disco antes do início do backend é recarregado primeiro, mesmo sem promoção",
                ProfileChangedBeforeTheStartIsReloadedFirstTestAsync),
            ("Perfil em disco que não carrega, que muda fora de startup, ou alterado com processo deste Hub vivo, " +
             "não inicia pelo perfil antigo",
                StaleProfileNeverStartsABackendTestAsync),
            ("Gateway iniciado por um wrapper é deste Hub: quem escuta descende do processo que o Hub iniciou",
                GatewayStartedThroughAWrapperIsOwnTestAsync),
            ("Processo deste Hub que perdeu a porta para outro é encerrado e o backend passa a ser de outro lançador",
                ProcessThatLostThePortIsNotTheGatewayTestAsync),
            ("Processo deste Hub vivo sem ninguém escutando depois do prazo é encerrado e a chamada inicia outro",
                OwnProcessThatStoppedListeningIsReplacedTestAsync),
            ("Quem descende de quem: a cadeia de processos para em pai inexistente, PID reaproveitado ou laço",
                () => Task.FromResult(ProcessTreeTest())),
            ("Reconexão passiva conta só falhas seguidas: uma conexão que funciona zera a contagem",
                PassiveReconnectCountsOnlyConsecutiveFailuresTestAsync),
            ("Perfil que não carrega por qualquer exceção é invalid-profile dito uma vez; arquivo em uso é " +
             "profile-unreadable",
                ProfileThatBreaksTheLoaderIsInvalidOnceTestAsync),
            ("Falha inesperada numa rodada de reconciliação é dita uma vez e não gera aviso de mudança de tools",
                UnexpectedReconcileFailureIsSaidOnceTestAsync),
            ("Perfil restaurado por cópia com data antiga: vale o instante em que o Hub viu a troca, não a data",
                RestoredProfileWithAnOldDateIsNotAppliedTestAsync),
            ("Listener trocado durante o handshake: a conexão é refeita, e recusada se ele não para de mudar",
                HandshakeIsRepeatedWhenTheListenerChangesMeanwhileTestAsync),
            ("Recarga automática só aplica mudança confinada a backends[].startup; o resto espera o gxhub_refresh",
                AutomaticReloadOnlyAppliesStartupChangesTestAsync),
            ("Hub sozinho que iniciou o Gateway o encerra ao sair e não deixa presença",
                OwnerAloneStopsItsGatewayOnTheWayOutTestAsync),
            ("Com outro Hub conectado, o Hub que iniciou o Gateway sai sem encerrá-lo e o outro segue atendido",
                SharedGatewaySurvivesItsOwnerTestAsync),
            ("Presença de Hub que morreu (arquivo que abre) não conta e é apagada",
                DeadHubPresenceDoesNotCountTestAsync),
            ("Hub morto à força não deixa presença presa, nem no processo que ele iniciou",
                KilledHubLeavesNoPresenceTestAsync),
            ("Sem resposta sobre os outros Hubs (trava de início ocupada, presença ilegível) o Gateway fica em execução",
                NoAnswerLeavesTheGatewayRunningTestAsync),
            ("otherHubsConnected conta os outros Hubs conectados ao endpoint, inclusive com este Hub offline",
                OtherHubsConnectedCountTestAsync),
            ("GXMCP_HUB_KEEP_SHARED_GATEWAY desligado: o Hub encerra o Gateway que iniciou mesmo com outro conectado",
                EscapeSwitchRestoresThePreviousExitTestAsync),
            ("GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE ligado: o Hub sozinho deixa o Gateway em execução (kept, 0 outros), o seguinte o encontra e é atendido",
                KeepGatewayWhenAloneLeavesTheGatewayRunningTestAsync),
            ("GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE: desligado por padrão, perde para KEEP_SHARED_GATEWAY=0, não segura quem não é Gateway e com outro Hub é o de sempre",
                KeepGatewayWhenAloneYieldsToTheOtherCasesTestAsync),
            ("GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE só deixa o Gateway que o Hub viu funcionar e cujo listener é o dele; sem handshake ou sem identificação é encerrado, e o wrapper vivo é mantido",
                KeepGatewayWhenAloneNeedsAGatewayThatServedTestAsync),
            ("gxhub_refresh continua reiniciando o Gateway do próprio Hub com outro Hub conectado, sem soltar a presença",
                RefreshStillRestartsOwnGatewayTestAsync),
            ("A presença acompanha o endpoint: offline solta, reconexão mantém, troca de endpoint solta a antiga e pega a nova",
                PresenceFollowsTheEndpointTestAsync),
            ("Hub que chega enquanto o dono sai: o Gateway que saiu entre o handshake e a presença é iniciado de novo",
                ArrivingHubRestartsAGatewayThatLeftTestAsync),
            ("Ao sair, um processo deste Hub que não é o Gateway de ninguém é encerrado mesmo com outro Hub registrado",
                OwnProcessThatServesNobodyIsStoppedOnTheWayOutTestAsync),
            ("Com a trava de início ocupada o Hub conecta sem registrar presença e registra na rodada seguinte",
                PresenceIsOnlyRegisteredUnderTheStartupLeaseTestAsync),
            ("Consulta aos outros Hubs que falha na saída deixa o Gateway em execução e o Hub diz por quê",
                FailedQuestionOnTheWayOutLeavesTheGatewayRunningTestAsync),
            ("Presença que não pode ser registrada não derruba o Hub: segue sem ser vista e diz isso uma vez",
                () => Task.FromResult(PresenceThatCannotBeRegisteredIsNotAnErrorTest())),
            ("Identificadores padrão do Hub deixam de ser herdáveis; um herdável perde o flag e um inválido é ignorado",
                () => Task.FromResult(StandardHandlesAreNotInheritedTest())),
            ("Hub que sai deixando um filho vivo não segura os canais de quem o lançou nem um identificador solto herdado",
                HubThatLeavesAChildDoesNotHoldTheLauncherChannelsTestAsync),
            ("O --self-test usa uma pasta temporária própria e deixa o TEMP real em paz",
                () => Task.FromResult(SelfTestUsesATemporaryFolderOfItsOwnTest())),
            ("A chave da trava de início e da presença é a AbsoluteUri do endpoint: localhost e 127.0.0.1, ou /mcp e /mcp/, " +
             "são endpoints distintos (limite documentado)",
                () => Task.FromResult(EndpointKeyIsTheAbsoluteUriTest())),
            ("Varredura de identificadores herdáveis: todos perdem a herança, também os de valor alto, pelo caminho do " +
             "sistema e pela reserva, sem tocar nos outros",
                () => Task.FromResult(SweepClearsEveryInheritableHandleTest())),
            ("Varredura: o teto da reserva, a sonda que falha, a tabela ilegível e a troca que falha não a interrompem",
                () => Task.FromResult(HandleSweepLogicTest())),
            ("Depois da varredura Process.Start com os três canais redirecionados segue funcionando, também em paralelo",
                ProcessStartWorksAfterTheSweepTestAsync),
            ("Hub de verdade morto à força não passa ao Gateway que iniciou um identificador solto herdado",
                RealHubDoesNotHandLooseHandlesToItsGatewayTestAsync),
            ("gxhub_status mostra a varredura (caminho e quantos) e o identificador do TEMP, que não muda com a grafia",
                StatusShowsSweepAndTempRootTestAsync),
            ("A presença é tentada de novo antes da chamada mesmo com a reconciliação desligada",
                PresenceIsRetriedBeforeACallWithReconcileOffTestAsync),
            ("Trava de início ocupada na chegada: o Hub confere o listener e não fica conectado a um Gateway que saiu",
                ArrivingHubWithBusyLeaseChecksTheListenerTestAsync),
            ("O dono só solta a trava de início quando nada mais escuta, também com wrapper",
                OwnerReleasesTheLeaseOnlyWhenNobodyListensTestAsync),
            ("startedInJob é o que o sistema diz do processo que o Hub iniciou e nulo para o que não é dele",
                StartedInJobFollowsTheSystemTestAsync),
            ("startedInJob dentro de jobs de verdade (KILL_ON_JOB_CLOSE e SILENT_BREAKAWAY_OK)",
                StartedInJobInsideRealJobsTestAsync),
            ("Registro de saída: uma linha por backend com a decisão, sem caminho",
                ExitLogRecordsEachDecisionTestAsync),
            ("Registro de saída: formato, rotação em 256 KB e escrita que falha sem atrapalhar a saída",
                ExitLogRotatesAndNeverBreaksTheExitTestAsync),
            // 0.4.0: o Gateway que o Hub inicia fica fora do job do cliente.
            ("Política GXMCP_HUB_START_OUTSIDE_JOB: auto é o padrão, também vazio ou desconhecido; always e never com sinônimos",
                () => Task.FromResult(OutsideJobPolicyTest())),
            ("Especificação do lançador: codificação e decodificação, linha curta e caminho de resposta válido",
                () => Task.FromResult(LaunchSpecRoundTripTest())),
            ("O ambiente do Hub nunca entra na especificação: só o do perfil e a revisão do perfil",
                LaunchSpecNeverCarriesTheHubEnvironmentTestAsync),
            ("Especificação inválida é recusada pelo lançador com código curto e nada é iniciado",
                () => Task.FromResult(InvalidLaunchSpecTest())),
            ("Lançador: resposta atômica com PID e instante, ambiente da especificação, diretório, e o filho morre se a resposta não puder ser gravada",
                LauncherProtocolTestAsync),
            ("Sonda de job: mapeamento do resultado e execução de verdade",
                () => Task.FromResult(JobProbeTest())),
            ("Decisão de sair do job: auto só com o Hub em job e o filho de sonda também; uma sonda por processo; always e never",
                LeaveJobDecisionTestAsync),
            ("A sonda de job é preguiçosa: só roda onde o Hub vai iniciar um processo, depois da trava de promoção; nem a partida, nem o status, nem a rodada a rodam",
                ProbeRunsOnlyWhereTheHubStartsAProcessTestAsync),
            ("Início fora do job que falha: WMI recusa, não existe, levanta exceção ou não responde no prazo",
                OutsideJobFallsBackWhenWmiFailsTestAsync),
            ("Início fora do job que falha: resultado ausente, lançador que sumiu, PID e instante que não conferem, erro do lançador, especificação grande",
                OutsideJobFallsBackWhenTheResultFailsTestAsync),
            ("Lançador tardio: o prazo vencido não inicia nada e responde launcher-late; um WMI mais lento que o orçamento não deixa segundo Gateway",
                LateLauncherStartsNothingTestAsync),
            ("Fim do orçamento: o Hub espera a resposta até o prazo (chamada pendente, falha incerta, arquivo preso) e só então cai no início comum",
                HubWaitsForTheAnswerUntilTheEndOfTheBudgetTestAsync),
            ("Queda sem Gateway para trás: o que o lançador criou e o Hub não adota é encerrado pelo handle conferido, e o que não confere não é tocado",
                FallbackLeavesNoGatewayBehindTestAsync),
            ("Lançador lento para anotar a resposta: o Hub o encerra com o que ele iniciou e a queda fica com um só Gateway; o que não morre é avisado",
                SlowLauncherLeavesNoGatewayBehindTestAsync),
            ("Varredura dos descendentes do lançador: filhos, netos e o que nasce depois, em varreduras repetidas; o que nasceu antes não é tocado",
                LauncherReaperSweepsDescendantsTestAsync),
            ("Os sobreviventes das mutações fechados: thread STA do WMI, objetos COM soltos, canais padrão sem herança sem a varredura, identificador do TEMP",
                SurvivingMutantsAreClosedTestAsync),
            ("gxhub_status: startMode nos três estados, outsideJobPolicy, childWouldBeInJob e versão 0.5.0",
                StatusShowsTheStartModeTestAsync),
            ("Stop, Detach e aviso de saída sobre um processo iniciado pelo lançador, com a árvore de um wrapper",
                StopAndDetachOnALaunchedProcessTestAsync),
            ("WMI de verdade: cria o processo fora da árvore e de job, na sessão e no usuário do Hub, e recusa com o código do WMI",
                () => Task.FromResult(RealWmiCreatesTheProcessOutsideTheTreeTest())),
            ("Hub em job KILL_ON_JOB_CLOSE (WMI de verdade): auto inicia fora do job e o filho sobrevive ao fechamento; never morre com o job",
                HubInAKillOnCloseJobStartsOutsideItTestAsync),
            ("Job com saída silenciosa: auto inicia normal; always sai do job; wrapper e ambiente do perfil (WMI de verdade)",
                HubInASilentBreakawayJobTestAsync)
        };

        var total = tests.Length;
        if (only is { Count: > 0 })
        {
            tests = tests
                .Where(test => only.Any(fragment => test.Name.Contains(fragment, StringComparison.Ordinal)))
                .ToArray();
            if (tests.Length == 0)
            {
                Console.Error.WriteLine("Self-test: nenhum teste corresponde ao filtro.");
                return 2;
            }

            Console.WriteLine($"Self-test com filtro: {tests.Length} de {total} testes.");
        }

        var failures = new List<string>();
        foreach (var (name, run) in tests)
        {
            var watch = Stopwatch.StartNew();
            var passed = RunWithTimeout(run);
            Console.WriteLine($"{(passed ? "ok    " : "FALHOU")} {name} ({watch.ElapsedMilliseconds} ms)");
            if (!passed)
            {
                failures.Add(name);
            }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"Self-test: {tests.Length}/{tests.Length} aprovados.");
            return 0;
        }

        Console.Error.WriteLine($"Self-test: {tests.Length - failures.Count}/{tests.Length} aprovados.");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("- " + failure);
        }
        return 1;
    }

    // Um teste que trava reprova em vez de prender a execução inteira.
    private static bool RunWithTimeout(Func<Task<bool>> test)
    {
        try
        {
            var task = Task.Run(test);
            return task.Wait(TimeSpan.FromSeconds(TestTimeoutSeconds)) && task.Result;
        }
        catch (Exception ex)
        {
            // Um teste que falha por exceção diz qual, no stderr: sem isso o único sinal seria o FALHOU.
            var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
            Console.Error.WriteLine($"[self-test] exceção em um teste: {cause.GetType().Name}: {cause.Message}");
            return false;
        }
    }

    private static bool ToolTextEnvelopeTest()
    {
        var result = HubService.ToolText("ok");
        return result["content"]?[0]?["text"]?.GetValue<string>() == "ok" &&
               result["isError"]?.GetValue<bool>() == false;
    }

    private static bool ToolTextCloneTest()
    {
        var result = HubService.ToolText("ok");
        var clone = (JsonObject)result.DeepClone();
        clone["isError"] = true;
        return result["isError"]?.GetValue<bool>() == false;
    }

    private static async Task<bool> BackendStartupLeaseTestAsync()
    {
        var endpoints = new[] { new Uri("http://127.0.0.1:65431/mcp") };
        try
        {
            using var first = await BackendStartupLease.AcquireAsync(
                endpoints,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            var secondTask = BackendStartupLease.AcquireAsync(
                endpoints,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

            await Task.Delay(200);
            if (secondTask.IsCompleted)
            {
                return false;
            }

            first.Dispose();
            using var second = await secondTask;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool WriterLeaseTest()
    {
        var lockRoot = Path.Combine(Path.GetTempPath(), "GxMcpHub-tests-" + Guid.NewGuid().ToString("N"));
        var config = new HubConfig
        {
            ProfileName = "self-test",
            KnowledgeBasePath = Path.GetTempPath(),
            WritePolicy = new WritePolicyConfig { Mode = "single-writer", WriterBackend = "test" }
        };

        try
        {
            using var first = WriterLease.AcquireIfNeeded(config, lockRoot);
            try
            {
                using var second = WriterLease.AcquireIfNeeded(config, lockRoot);
                return false;
            }
            catch (ConfigurationException)
            {
                return true;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(lockRoot, recursive: true);
            }
            catch
            {
                // O teste não deve falhar apenas porque um antivírus atrasou a remoção temporária.
            }
        }
    }

    private static bool OpenWriterLeaseTest()
    {
        var lockRoot = Path.Combine(Path.GetTempPath(), "GxMcpHub-tests-" + Guid.NewGuid().ToString("N"));
        var config = new HubConfig
        {
            ProfileName = "self-test-open",
            KnowledgeBasePath = Path.GetTempPath(),
            WritePolicy = new WritePolicyConfig { Mode = "open-writer", WriterBackend = "test" }
        };

        try
        {
            using var first = WriterLease.AcquireIfNeeded(config, lockRoot);
            using var second = WriterLease.AcquireIfNeeded(config, lockRoot);
            return first is null && second is null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(lockRoot))
                {
                    Directory.Delete(lockRoot, recursive: true);
                }
            }
            catch
            {
                // O teste não deve falhar apenas porque um antivírus atrasou a remoção temporária.
            }
        }
    }

    private static async Task<bool> SessionRecoveryTestAsync()
    {
        var handler = new ExpiringSessionHandler();
        var config = new BackendConfig
        {
            Id = "session-test",
            ProtocolVersion = "2025-11-25"
        };

        try
        {
            using var client = new BackendClient(
                config,
                new Uri("http://127.0.0.1:65535/mcp"),
                connectTimeoutSeconds: 1,
                callTimeoutSeconds: 5,
                handler);
            await client.InitializeAsync(CancellationToken.None);
            var tools = await client.ListToolsAsync(CancellationToken.None);
            var callResult = await client.CallToolAsync(
                "test_tool",
                new JsonObject(),
                CancellationToken.None);

            return handler.InitializeCalls == 2 &&
                   handler.ListCalls == 1 &&
                   handler.ToolCalls == 2 &&
                   handler.ListUsedOldSession &&
                   handler.FirstCallUsedOldSession &&
                   handler.SecondCallUsedNewSession &&
                   tools.Count == 1 &&
                   tools[0]["name"]?.GetValue<string>() == "test_tool" &&
                   callResult?["content"]?[0]?["text"]?.GetValue<string>() == "ok";
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> BackendHealthProbeTestAsync()
    {
        var handler = new FailingHealthHandler();
        var hubConfig = new HubConfig
        {
            ConnectTimeoutSeconds = 1,
            CallTimeoutSeconds = 5
        };
        var backendConfig = new BackendConfig
        {
            Id = "health-test",
            DisplayName = "health-test",
            Kind = "native",
            Enabled = true,
            Endpoint = "http://127.0.0.1:65534/mcp",
            ProtocolVersion = "2025-11-25"
        };

        try
        {
            await using var runtime = new BackendRuntime(hubConfig, backendConfig, _ => handler);
            await runtime.InitializeAsync(allowStartup: false, CancellationToken.None);
            if (!runtime.Online)
            {
                return false;
            }

            var healthy = await runtime.ProbeAsync(CancellationToken.None);
            return !healthy && !runtime.Online && runtime.Error?.Contains("verificação ativa MCP") == true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProfileHotReloadTestAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "GxMcpHub-profile-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profilePath = Path.Combine(root, "profile.json");

        static JsonObject Profile(string rootPath, string displayName) => new()
        {
            ["profileName"] = "hot-reload-test",
            ["workspaceRoot"] = rootPath,
            ["knowledgeBasePath"] = rootPath,
            ["connectTimeoutSeconds"] = 1,
            ["callTimeoutSeconds"] = 5,
            ["writePolicy"] = new JsonObject
            {
                ["mode"] = "read-only",
                ["hideBlockedTools"] = true
            },
            ["backends"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "native",
                    ["displayName"] = displayName,
                    ["kind"] = "fixed",
                    ["enabled"] = true,
                    ["toolPrefix"] = "native_",
                    ["protocolVersion"] = "2025-11-25",
                    ["endpoint"] = "http://127.0.0.1:65533/mcp",
                    ["useRemoteReadOnlyAnnotations"] = true
                }
            }
        };

        try
        {
            File.WriteAllText(profilePath, Profile(root, "before").ToJsonString());
            var loaded = HubConfigLoader.Load(profilePath);
            await using var hub = new HubService(
                loaded,
                (hubConfig, backendConfig) => new BackendRuntime(
                    hubConfig,
                    backendConfig,
                    _ => new StableBackendHandler()));

            await hub.StartAsync(CancellationToken.None);
            var sameHub = hub;
            File.WriteAllText(profilePath, Profile(root, "after").ToJsonString());
            await hub.RefreshFromProfileAsync(
                allowStartup: false,
                notify: false,
                CancellationToken.None);

            var status = hub.StatusJson();
            return ReferenceEquals(sameHub, hub) &&
                   hub.Config.Backends[0].DisplayName == "after" &&
                   status["lastProfileReloadAtUtc"] is not null &&
                   status["lastProfileReloadTrigger"]?.GetValue<string>() == "manual" &&
                   status["hubVersion"]?.GetValue<string>() == HubInfo.Version &&
                   status["backends"]?[0]?["online"]?.GetValue<bool>() == true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Antivírus pode atrasar a liberação da pasta temporária.
            }
        }
    }

    private static bool GatewayEnvironmentCarriesTheProfileRevisionTest()
    {
        var startup = new StartupConfig();
        startup.Environment["GX_MCP_PORT"] = "5015";
        // Um perfil que tente declarar a própria revisão não pode enganar o diagnóstico de "aplicado versus pendente".
        startup.Environment[ProcessSupervisor.ProfileRevisionVariable] = "FORGED000000";

        var withRevision = ProcessSupervisor.BuildStartupEnvironment(startup, "198AFA700987");
        var withoutRevision = ProcessSupervisor.BuildStartupEnvironment(new StartupConfig(), " ");
        var untouched = ProcessSupervisor.BuildStartupEnvironment(startup, null);

        return withRevision[ProcessSupervisor.ProfileRevisionVariable] == "198AFA700987" &&
               withRevision["GX_MCP_PORT"] == "5015" &&
               withRevision.Count == 2 &&
               withoutRevision.Count == 0 &&
               untouched[ProcessSupervisor.ProfileRevisionVariable] == "FORGED000000" &&
               startup.Environment[ProcessSupervisor.ProfileRevisionVariable] == "FORGED000000";
    }

    private static bool StartupEnvironmentIgnoresNameCaseTest()
    {
        // O JSON do perfil chega como dicionário que diferencia caixa, mas o Windows não: um nome repetido em outra
        // caixa não pode derrubar a inicialização, o último vence, a revisão do Hub não se forja trocando a caixa
        // e o status de pins enxerga o nome como o Gateway o receberá.
        var startup = new StartupConfig
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GX_MCP_PORT"] = "5015",
                ["gx_mcp_port"] = "5016",
                ["gxmcp_hub_profile_revision"] = "FORGED000000",
                ["gxmcp_expected_kb_path"] = "synthetic-kb-marker",
                ["GXMCP_EXPECTED_KB_VERSION"] = "SyntheticVersion",
                ["gxmcp_expected_kb_version"] = "   "
            }
        };

        try
        {
            var environment = ProcessSupervisor.BuildStartupEnvironment(startup, "198AFA700987");
            var pins = BackendRuntime.PinsStatus(new BackendConfig { Startup = startup });

            return environment["GX_MCP_PORT"] == "5016" &&
                   environment[ProcessSupervisor.ProfileRevisionVariable] == "198AFA700987" &&
                   environment.Count(pair => string.Equals(
                       pair.Key, ProcessSupervisor.ProfileRevisionVariable, StringComparison.OrdinalIgnoreCase)) == 1 &&
                   environment.Count == 4 &&
                   pins["kbPathConfigured"]?.GetValue<bool>() == true &&
                   // O último valor do nome repetido está em branco: não conta como pin configurado.
                   pins["versionConfigured"]?.GetValue<bool>() == false &&
                   !pins.ToJsonString().Contains("synthetic-kb-marker", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> PinsStatusFollowsTheLoadedProfileTestAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "GxMcpHub-pins-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profilePath = Path.Combine(root, "profile.json");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executável do Hub indisponível.");

        JsonObject Profile(JsonObject environment) => new()
        {
            ["profileName"] = "pins-status-test",
            ["workspaceRoot"] = root,
            ["knowledgeBasePath"] = root,
            ["connectTimeoutSeconds"] = 1,
            ["callTimeoutSeconds"] = 5,
            ["writePolicy"] = new JsonObject
            {
                ["mode"] = "read-only",
                ["hideBlockedTools"] = true
            },
            ["backends"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "native",
                    ["displayName"] = "native",
                    ["kind"] = "fixed",
                    ["enabled"] = true,
                    ["toolPrefix"] = "native_",
                    ["protocolVersion"] = "2025-11-25",
                    ["endpoint"] = "http://127.0.0.1:65532/mcp",
                    ["useRemoteReadOnlyAnnotations"] = true,
                    ["startup"] = new JsonObject
                    {
                        ["command"] = exe,
                        ["arguments"] = "",
                        ["workingDirectory"] = root,
                        ["startupTimeoutSeconds"] = 1,
                        ["environment"] = environment
                    }
                }
            }
        };

        try
        {
            File.WriteAllText(profilePath, Profile(new JsonObject { ["GX_MCP_PORT"] = "5015" }).ToJsonString());
            var loaded = HubConfigLoader.Load(profilePath);
            await using var hub = new HubService(
                loaded,
                (hubConfig, backendConfig) => new BackendRuntime(
                    hubConfig,
                    backendConfig,
                    _ => new StableBackendHandler()));
            // Sem allowStartup: o teste nunca inicia o processo de backend declarado no perfil.
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

            var before = hub.StatusJson();
            var beforePins = before["backends"]?[0]?["pinsConfigured"];
            var beforeRevision = before["profileRevision"]?.GetValue<string>();
            var cleanBeforeEdit = before["profileReloadPending"]?.GetValue<bool>() == false &&
                                  before["profileOnDiskRevision"]?.GetValue<string>() == beforeRevision;

            File.WriteAllText(
                profilePath,
                Profile(new JsonObject
                {
                    ["GX_MCP_PORT"] = "5015",
                    ["GXMCP_EXPECTED_KB_PATH"] = @"C:\Synthetic\PinnedKbMarker",
                    ["GXMCP_EXPECTED_KB_VERSION"] = "   "
                }).ToJsonString());
            // Editado no disco e ainda não recarregado: o status mostra a edição como pendente.
            var pending = hub.StatusJson();
            var pendingShown = pending["profileReloadPending"]?.GetValue<bool>() == true &&
                               pending["profileRevision"]?.GetValue<string>() == beforeRevision &&
                               pending["profileOnDiskRevision"]?.GetValue<string>() != beforeRevision &&
                               pending["backends"]?[0]?["pinsConfigured"]?["kbPathConfigured"]?.GetValue<bool>() == false;

            await hub.RefreshFromProfileAsync(allowStartup: false, notify: false, CancellationToken.None);

            var after = hub.StatusJson();
            var afterPins = after["backends"]?[0]?["pinsConfigured"];
            var serialized = after.ToJsonString();

            return cleanBeforeEdit && pendingShown &&
                   after["profileReloadPending"]?.GetValue<bool>() == false &&
                   beforePins?["kbPathConfigured"]?.GetValue<bool>() == false &&
                   beforePins["versionConfigured"]?.GetValue<bool>() == false &&
                   afterPins?["kbPathConfigured"]?.GetValue<bool>() == true &&
                   // Um valor em branco não conta como pin configurado.
                   afterPins["versionConfigured"]?.GetValue<bool>() == false &&
                   after["profileRevision"]?.GetValue<string>() != beforeRevision &&
                   // O status só diz que há pin; nunca repete o valor.
                   !serialized.Contains("PinnedKbMarker", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Antivírus pode atrasar a liberação da pasta temporária.
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hub 0.2.0: reconciliação passiva, início sob demanda e diagnóstico "aplicado ou pendente" no próprio Hub.
    // ------------------------------------------------------------------------------------------------------------

    private static bool BackendProcessGetsItsOwnStandardInputTest()
    {
        // Herdando a entrada padrão do Hub, um backend iniciado enquanto o Hub espera a próxima requisição (refresh
        // ou início sob demanda) pode travar na própria inicialização, e a entrada é o canal MCP do cliente.
        var startup = new StartupConfig
        {
            Command = "backend.exe",
            Arguments = "--flag",
            WorkingDirectory = Path.GetTempPath()
        };
        startup.Environment["GX_MCP_PORT"] = "5015";

        var startInfo = ProcessSupervisor.BuildStartInfo(startup, "ABCDEF012345");
        return startInfo.RedirectStandardInput &&
               startInfo.RedirectStandardOutput &&
               startInfo.RedirectStandardError &&
               !startInfo.UseShellExecute &&
               startInfo.CreateNoWindow &&
               startInfo.FileName == "backend.exe" &&
               startInfo.Arguments == "--flag" &&
               startInfo.Environment["GX_MCP_PORT"] == "5015" &&
               startInfo.Environment[ProcessSupervisor.ProfileRevisionVariable] == "ABCDEF012345";
    }

    private static bool ListenerInspectorFindsTheProcessBehindAPortTest()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var expected = ListenerLookup.Found(Environment.ProcessId, self.StartTime.ToUniversalTime());

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port;
            try
            {
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var byAddress = ListenerInspector.Find(new Uri($"http://127.0.0.1:{port}/mcp"));
                var byName = ListenerInspector.Find(new Uri($"http://localhost:{port}/mcp"));
                if (byAddress != expected || byName != expected)
                {
                    return false;
                }

                // Um endereço IPv6 explícito não é atendido por um socket que só existe em IPv4.
                if (ListenerInspector.Find(new Uri($"http://[::1]:{port}/mcp")) != ListenerLookup.NotFound)
                {
                    return false;
                }
            }
            finally
            {
                listener.Stop();
            }

            // Porta fechada: ninguém escuta.
            if (ListenerInspector.Find(new Uri($"http://127.0.0.1:{port}/mcp")) != ListenerLookup.NotFound)
            {
                return false;
            }

            if (!Socket.OSSupportsIPv6)
            {
                return true;
            }

            TcpListener listenerV6;
            try
            {
                listenerV6 = new TcpListener(IPAddress.IPv6Loopback, 0);
                listenerV6.Start();
            }
            catch (SocketException)
            {
                // IPv6 presente mas sem loopback utilizável nesta máquina: a tabela IPv4 já foi provada.
                return true;
            }

            try
            {
                var portV6 = ((IPEndPoint)listenerV6.LocalEndpoint).Port;
                // Um socket que só existe em ::1 é o dono do endpoint [::1] e do nome localhost, e não de um
                // endpoint 127.0.0.1.
                return ListenerInspector.Find(new Uri($"http://[::1]:{portV6}/mcp")) == expected &&
                       ListenerInspector.Find(new Uri($"http://localhost:{portV6}/mcp")) == expected &&
                       ListenerInspector.Find(new Uri($"http://127.0.0.1:{portV6}/mcp")) == ListenerLookup.NotFound;
            }
            finally
            {
                listenerV6.Stop();
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool GatewayProfileEvidenceTest()
    {
        var edited = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var after = ListenerLookup.Found(4100, edited.AddSeconds(1));
        var before = ListenerLookup.Found(4100, edited.AddSeconds(-1));
        var sameInstant = ListenerLookup.Found(4100, edited);

        static bool Is((bool? Applied, string Evidence) actual, bool? applied, string evidence) =>
            actual.Applied == applied && actual.Evidence == evidence;

        return
            // Gateway deste Hub: vale a revisão com que foi iniciado, contra a carregada e a do disco.
            Is(GatewayProfileEvidence.Evaluate(after, true, "AAAAAAAAAAAA", "AAAAAAAAAAAA", "AAAAAAAAAAAA", edited),
                true, GatewayProfileEvidence.FromLoadedRevision) &&
            Is(GatewayProfileEvidence.Evaluate(after, true, "AAAAAAAAAAAA", "AAAAAAAAAAAA", null, edited),
                true, GatewayProfileEvidence.FromLoadedRevision) &&
            // Edição no disco ainda não carregada: o Gateway é de uma revisão mais antiga que a do arquivo.
            Is(GatewayProfileEvidence.Evaluate(after, true, "AAAAAAAAAAAA", "AAAAAAAAAAAA", "BBBBBBBBBBBB", edited),
                false, GatewayProfileEvidence.FromOlderRevision) &&
            Is(GatewayProfileEvidence.Evaluate(after, true, "AAAAAAAAAAAA", "BBBBBBBBBBBB", "BBBBBBBBBBBB", edited),
                false, GatewayProfileEvidence.FromOlderRevision) &&
            // Arquivo ilegível no instante do status: a comparação com a revisão carregada basta para negar.
            Is(GatewayProfileEvidence.Evaluate(after, true, "AAAAAAAAAAAA", "BBBBBBBBBBBB", null, edited),
                false, GatewayProfileEvidence.FromOlderRevision) &&
            // A revisão decide mesmo quando a tabela TCP não mostra o listener.
            Is(GatewayProfileEvidence.Evaluate(
                    ListenerLookup.NotFound, true, "AAAAAAAAAAAA", "AAAAAAAAAAAA", "AAAAAAAAAAAA", edited),
                true, GatewayProfileEvidence.FromLoadedRevision) &&
            // Gateway de outro lançador: vale o instante de criação contra a última escrita do perfil.
            Is(GatewayProfileEvidence.Evaluate(after, false, null, "AAAAAAAAAAAA", "AAAAAAAAAAAA", edited),
                true, GatewayProfileEvidence.StartedAfterEdit) &&
            Is(GatewayProfileEvidence.Evaluate(before, false, null, "AAAAAAAAAAAA", "AAAAAAAAAAAA", edited),
                false, GatewayProfileEvidence.StartedBeforeEdit) &&
            Is(GatewayProfileEvidence.Evaluate(sameInstant, false, null, "AAAAAAAAAAAA", "AAAAAAAAAAAA", edited),
                false, GatewayProfileEvidence.StartedBeforeEdit) &&
            // Sem revisão registrada, um processo vivo deste Hub cai na regra do instante.
            Is(GatewayProfileEvidence.Evaluate(before, true, null, "AAAAAAAAAAAA", "AAAAAAAAAAAA", edited),
                false, GatewayProfileEvidence.StartedBeforeEdit) &&
            // Sem listener, ou sem poder inspecioná-lo, não há resposta: nulo, nunca "aplicado".
            Is(GatewayProfileEvidence.Evaluate(
                    ListenerLookup.NotFound, false, null, "AAAAAAAAAAAA", "AAAAAAAAAAAA", edited),
                null, GatewayProfileEvidence.ListenerNotFound) &&
            Is(GatewayProfileEvidence.Evaluate(
                    ListenerLookup.NotInspectable(4100), false, null, "AAAAAAAAAAAA", "AAAAAAAAAAAA", edited),
                null, GatewayProfileEvidence.ListenerNotInspectable) &&
            Is(GatewayProfileEvidence.Evaluate(after, false, null, "AAAAAAAAAAAA", "AAAAAAAAAAAA", null),
                null, GatewayProfileEvidence.ListenerNotInspectable);
    }

    private static async Task<bool> ProfileIsReloadedAutomaticallyWithoutALiveChildTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-autoreload-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var script = new BackendScript();

        try
        {
            // O perfil declara um processo de backend, e ninguém escuta a porta: recarregar não pode iniciá-lo.
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            await using var hub = NewHub(profilePath, script);
            var notifications = 0;
            hub.ToolsChanged += () => Interlocked.Increment(ref notifications);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var before = hub.StatusJson();

            WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
            var changed = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var after = hub.StatusJson();
            var notifiedOnce = Volatile.Read(ref notifications) == 1;
            var loadedTheEdit = MarkOf(hub) == "after";

            var unchangedRound = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var later = hub.StatusJson();
            var noSecondNotification = Volatile.Read(ref notifications) == 1;

            // Sem esperar a rodada periódica: gxhub_status e gxhub_list_tools reconciliam antes de responder.
            WriteProfile(profilePath, TestProfile(root, port, "third", withStartup: true));
            var statusCall = await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
            var statusAnswer = JsonNode.Parse(ResultText(statusCall) ?? "{}");
            var reloadedByStatus = Flag(statusAnswer?["profileReloadPending"]) == false &&
                                   Text(statusAnswer?["profileRevision"]) != Text(later["profileRevision"]) &&
                                   MarkOf(hub) == "third" &&
                                   Text(statusAnswer?["lastProfileReloadTrigger"]) == "auto";
            WriteProfile(profilePath, TestProfile(root, port, "fourth", withStartup: true));
            await hub.CallAsync("gxhub_list_tools", new JsonObject(), CancellationToken.None);
            var reloadedByListTools = MarkOf(hub) == "fourth";
            var afterCalls = hub.StatusJson()["backends"]?[0];
            var callsStartedNothing = Flag(afterCalls?["startedByHub"]) == false && afterCalls?["processId"] is null;

            var auto = after["profileAutoReload"];
            var backend = after["backends"]?[0];
            var gateway = backend?["gateway"];
            return before["lastProfileReloadTrigger"] is null &&
                   Text(before["profileAutoReload"]?["lastOutcome"]) == "none" &&
                   Flag(before["profileAutoReload"]?["enabled"]) == true &&
                   changed && notifiedOnce && !unchangedRound && noSecondNotification &&
                   loadedTheEdit &&
                   Text(after["profileRevision"]) != Text(before["profileRevision"]) &&
                   Flag(after["profileReloadPending"]) == false &&
                   Text(after["lastProfileReloadTrigger"]) == "auto" &&
                   after["lastProfileReloadAtUtc"] is not null &&
                   Text(auto?["lastOutcome"]) == "reloaded" &&
                   auto?["lastCheckUtc"] is not null &&
                   auto["lastOutcomeAtUtc"] is not null &&
                   auto["deferredReason"] is null &&
                   auto["error"] is null &&
                   Flag(backend?["online"]) == true &&
                   Flag(backend?["startedByHub"]) == false &&
                   backend?["processId"] is null &&
                   gateway is not null &&
                   Flag(gateway["startedByThisHub"]) == false &&
                   gateway["listenerProcessId"] is null &&
                   gateway["startedFromProfileRevision"] is null &&
                   gateway["profileApplied"] is null &&
                   Text(gateway["profileAppliedEvidence"]) == GatewayProfileEvidence.ListenerNotFound &&
                   gateway["profileModifiedUtc"] is not null &&
                   // O que foi recarregado continua visível depois de uma rodada sem nada a fazer.
                   Text(later["profileAutoReload"]?["lastOutcome"]) == "reloaded" &&
                   reloadedByStatus && reloadedByListTools && callsStartedNothing &&
                   script.Clients == 4 &&
                   ListenerInspector.Find(new Uri($"http://127.0.0.1:{port}/mcp")) == ListenerLookup.NotFound;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> ReloadIsDeferredWhileOwnGatewayRunsTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-deferred-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var script = new BackendScript();
        var children = new List<int>();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            bool ok;
            await using (var hub = NewHub(profilePath, script))
            {
                // Aqui o Hub inicia de verdade o processo declarado (um filho que só escuta a porta).
                await hub.StartAsync(CancellationToken.None);
                var first = hub.StatusJson();
                var firstBackend = first["backends"]?[0];
                var firstGateway = firstBackend?["gateway"];
                var firstPid = firstBackend?["processId"]?.GetValue<int>() ?? 0;
                if (firstPid == 0)
                {
                    return false;
                }
                children.Add(firstPid);
                var firstRevision = Text(first["profileRevision"]);
                var startedByThisHub =
                    Flag(firstBackend?["startedByHub"]) == true &&
                    firstGateway?["listenerProcessId"]?.GetValue<int>() == firstPid &&
                    firstGateway["startedUtc"] is not null &&
                    Flag(firstGateway["startedByThisHub"]) == true &&
                    Text(firstGateway["startedFromProfileRevision"]) == firstRevision &&
                    Flag(firstGateway["profileApplied"]) == true &&
                    Text(firstGateway["profileAppliedEvidence"]) == GatewayProfileEvidence.FromLoadedRevision;

                WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
                var changed = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                // O caminho de antes da chamada adia do mesmo jeito.
                await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
                var deferred = hub.StatusJson();
                var deferredBackend = deferred["backends"]?[0];
                var deferredShown =
                    !changed &&
                    Text(deferred["profileRevision"]) == firstRevision &&
                    Flag(deferred["profileReloadPending"]) == true &&
                    deferred["lastProfileReloadTrigger"] is null &&
                    MarkOf(hub) == "before" &&
                    Text(deferred["profileAutoReload"]?["lastOutcome"]) == "deferred" &&
                    Text(deferred["profileAutoReload"]?["deferredReason"]) == HubService.DeferredByOwnGateway &&
                    deferred["profileAutoReload"]?["error"] is null &&
                    deferredBackend?["processId"]?.GetValue<int>() == firstPid &&
                    IsAlive(firstPid) &&
                    Flag(deferredBackend["gateway"]?["profileApplied"]) == false &&
                    Text(deferredBackend["gateway"]?["profileAppliedEvidence"]) ==
                    GatewayProfileEvidence.FromOlderRevision;

                // O ato deliberado: recarrega, encerra o Gateway antigo e inicia outro pelo perfil novo.
                await hub.RefreshFromProfileAsync(allowStartup: true, notify: false, CancellationToken.None);
                var refreshed = hub.StatusJson();
                var refreshedBackend = refreshed["backends"]?[0];
                var secondPid = refreshedBackend?["processId"]?.GetValue<int>() ?? 0;
                if (secondPid != 0)
                {
                    children.Add(secondPid);
                }
                var refreshedShown =
                    secondPid != 0 && secondPid != firstPid &&
                    WaitForExit(firstPid, TimeSpan.FromSeconds(10)) &&
                    Text(refreshed["profileRevision"]) != firstRevision &&
                    Flag(refreshed["profileReloadPending"]) == false &&
                    Text(refreshed["lastProfileReloadTrigger"]) == "manual" &&
                    MarkOf(hub) == "after" &&
                    Text(refreshed["profileAutoReload"]?["lastOutcome"]) == "none" &&
                    refreshed["profileAutoReload"]?["deferredReason"] is null &&
                    refreshedBackend?["gateway"]?["listenerProcessId"]?.GetValue<int>() == secondPid &&
                    Text(refreshedBackend["gateway"]?["startedFromProfileRevision"]) ==
                    Text(refreshed["profileRevision"]) &&
                    Flag(refreshedBackend["gateway"]?["profileApplied"]) == true &&
                    Text(refreshedBackend["gateway"]?["profileAppliedEvidence"]) ==
                    GatewayProfileEvidence.FromLoadedRevision;

                ok = startedByThisHub && deferredShown && refreshedShown;
            }

            // Encerrado o Hub, nenhum filho iniciado por ele fica para trás.
            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> IncompatibleProfileIsNotLoadedTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-incompatible-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var script = new BackendScript();

        try
        {
            var original = TestProfile(root, port, "before", withStartup: false).ToJsonString();
            File.WriteAllText(profilePath, original);
            await using var hub = NewHub(profilePath, script);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var revision = Text(hub.StatusJson()["profileRevision"]);

            bool Refused(JsonObject edited)
            {
                WriteProfile(profilePath, edited);
                var changed = hub.ReconcileAsync(fromTimer: true, CancellationToken.None).GetAwaiter().GetResult();
                var status = hub.StatusJson();
                var error = Text(status["profileAutoReload"]?["error"]);
                return !changed &&
                       Text(status["profileAutoReload"]?["lastOutcome"]) == "failed" &&
                       error is not null &&
                       error.StartsWith("incompatible-profile-change:", StringComparison.Ordinal) &&
                       // O status não repete caminho nem valor do perfil recusado.
                       !error.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                       status["profileAutoReload"]?["deferredReason"] is null &&
                       Text(status["profileRevision"]) == revision &&
                       Flag(status["profileReloadPending"]) == true &&
                       status["lastProfileReloadTrigger"] is null &&
                       hub.Config.ProfileName == "reconcile-test" &&
                       hub.Config.WritePolicy.Mode == "read-only" &&
                       Flag(status["backends"]?[0]?["online"]) == true;
            }

            var renamed = Refused(TestProfile(root, port, "after", withStartup: false, profileName: "another-profile"));

            // A política de escrita nunca se alarga por recarga automática.
            var widened = TestProfile(root, port, "after", withStartup: false);
            widened["writePolicy"] = new JsonObject
            {
                ["mode"] = "open-writer",
                ["writerBackend"] = "native",
                ["hideBlockedTools"] = true
            };
            var policyRefused = Refused(widened);

            // O Hub segue respondendo, com o backend que já tinha.
            var statusCall = await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
            var stillServing = !IsError(statusCall) && script.Clients == 1;

            // Desfeita a edição, nada fica pendente nem em erro.
            File.WriteAllText(profilePath, original);
            var restoredRound = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var restored = hub.StatusJson();
            return renamed && policyRefused && stillServing && !restoredRound &&
                   Text(restored["profileAutoReload"]?["lastOutcome"]) == "none" &&
                   restored["profileAutoReload"]?["error"] is null &&
                   Flag(restored["profileReloadPending"]) == false;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> InvalidProfileIsNotLoadedTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-invalid-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var script = new BackendScript();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            await using var hub = NewHub(profilePath, script);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var revision = Text(hub.StatusJson()["profileRevision"]);

            bool Refused(string content)
            {
                File.WriteAllText(profilePath, content);
                var changed = hub.ReconcileAsync(fromTimer: true, CancellationToken.None).GetAwaiter().GetResult();
                var status = hub.StatusJson();
                var error = Text(status["profileAutoReload"]?["error"]);
                return !changed &&
                       Text(status["profileAutoReload"]?["lastOutcome"]) == "failed" &&
                       error is not null &&
                       error.StartsWith("invalid-profile:", StringComparison.Ordinal) &&
                       !error.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                       Text(status["profileRevision"]) == revision &&
                       Flag(status["profileReloadPending"]) == true &&
                       MarkOf(hub) == "before" &&
                       Flag(status["backends"]?[0]?["online"]) == true;
            }

            // Arquivo pela metade (uma gravação em andamento) e JSON que carrega mas não passa na validação.
            var halfWritten = Refused("{ \"profileName\": \"reconcile-test\", \"workspaceRoot\": ");
            var outOfRange = TestProfile(root, port, "after", withStartup: true);
            outOfRange["connectTimeoutSeconds"] = 0;
            var invalidValue = Refused(outOfRange.ToJsonString());

            var statusCall = await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
            var stillServing = !IsError(statusCall) && script.Clients == 1;

            // Quando o arquivo volta a ser válido, a próxima rodada o carrega.
            WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
            var loadedRound = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var loaded = hub.StatusJson();
            return halfWritten && invalidValue && stillServing && loadedRound &&
                   MarkOf(hub) == "after" &&
                   Text(loaded["profileAutoReload"]?["lastOutcome"]) == "reloaded" &&
                   loaded["profileAutoReload"]?["error"] is null &&
                   Text(loaded["lastProfileReloadTrigger"]) == "auto";
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> BackendIsReconnectedWhenTheListenerChangesTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-reconnect-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var created = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        var listener = ListenerLookup.NotInspectable(1001);
        var reachable = true;
        var script = new BackendScript { Tools = new[] { "tool_a" }, Reachable = () => reachable };

        try
        {
            // O perfil declara um processo de backend; a reconexão passiva não pode iniciá-lo em nenhum passo.
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            var startsBefore = ProcessSupervisor.TotalStarts;
            await using var hub = NewHub(profilePath, script, _ => listener);
            var notifications = 0;
            hub.ToolsChanged += () => Interlocked.Increment(ref notifications);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

            // Conectado sem conseguir identificar o listener: quando ele passa a ser visível não há com o que
            // comparar, então a conexão não é refeita.
            listener = ListenerLookup.Found(1001, created);
            var unknownAtConnect = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var keptWhenUnknown = !unknownAtConnect && script.Clients == 1;

            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var initialTools = BackendToolNames(hub);
            var sameListener = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);

            // Promoção: outro processo passa a escutar a porta, com outro catálogo.
            listener = ListenerLookup.Found(2002, created.AddMinutes(1));
            script.Tools = new[] { "tool_a", "tool_b" };
            var replaced = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var replacedTools = BackendToolNames(hub);
            var replacedOk = replaced && script.Clients == 3 && Volatile.Read(ref notifications) == 1 &&
                             replacedTools.SequenceEqual(new[] { "native_tool_a", "native_tool_b" });

            // O Gateway some: o backend fica offline, as tools saem e nada é iniciado.
            listener = ListenerLookup.NotFound;
            reachable = false;
            var gone = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var goneStatus = hub.StatusJson()["backends"]?[0];
            var goneOk = gone && Flag(goneStatus?["online"]) == false && BackendToolNames(hub).Length == 0 &&
                         Volatile.Read(ref notifications) == 2 &&
                         !await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);

            // Volta a haver listener: conecta de novo.
            listener = ListenerLookup.Found(3003, created.AddMinutes(2));
            reachable = true;
            var back = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var backOk = back && Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true &&
                         Volatile.Read(ref notifications) == 3;

            // Sem poder inspecionar o listener não há o que concluir: a conexão fica como está.
            listener = ListenerLookup.NotInspectable(3003);
            var unknown = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);

            // Mesmo PID com outro instante de criação é outro processo (o Windows reaproveita PIDs).
            listener = ListenerLookup.Found(3003, created.AddMinutes(3));
            var reusedPid = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);

            // Um processo que escuta e não responde MCP é tentado um número limitado de vezes.
            listener = ListenerLookup.Found(4004, created.AddMinutes(4));
            reachable = false;
            var attempts = 0;
            for (var round = 0; round < 6; round++)
            {
                if (await hub.ReconcileAsync(fromTimer: true, CancellationToken.None))
                {
                    attempts++;
                }
            }

            var final = hub.StatusJson()["backends"]?[0];
            return keptWhenUnknown && initialTools.SequenceEqual(new[] { "native_tool_a" }) && !sameListener &&
                   replacedOk && goneOk && backOk && !unknown && reusedPid && attempts == 3 &&
                   Flag(final?["startedByHub"]) == false &&
                   final?["processId"] is null &&
                   // Nenhum processo foi iniciado para a porta em passo algum.
                   ProcessSupervisor.TotalStarts == startsBefore &&
                   ListenerInspector.Find(new Uri($"http://127.0.0.1:{port}/mcp")) == ListenerLookup.NotFound;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> AutomaticHandshakeIsBoundedTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-budget-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var created = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        var listener = ListenerLookup.Found(1001, created);
        var script = new BackendScript();
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // Prazo de conexão de 1 s no perfil: o handshake automático tem 5 s.
            WriteProfile(
                profilePath,
                TestProfile(root, port, "native", withStartup: false, connectTimeoutSeconds: 1));
            await using var hub = NewHub(profilePath, script, _ => listener);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

            // Outro processo passa a escutar a porta, aceita a conexão e não responde ao initialize.
            listener = ListenerLookup.Found(2002, created.AddMinutes(1));
            script.InitializeGate = never.Task;
            var watch = Stopwatch.StartNew();
            var changed = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            watch.Stop();
            var stuck = hub.StatusJson()["backends"]?[0];
            var bounded = changed &&
                          watch.Elapsed >= TimeSpan.FromSeconds(4) &&
                          watch.Elapsed < TimeSpan.FromSeconds(30) &&
                          Flag(stuck?["online"]) == false &&
                          Text(stuck?["error"])?.Contains("sem resposta MCP em 5 s", StringComparison.Ordinal) == true;

            // O mesmo prazo vale para o início sob demanda: a chamada recebe o erro, não fica presa.
            listener = ListenerLookup.NotFound;
            watch.Restart();
            var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            watch.Stop();
            var callBounded = IsError(call) &&
                              ResultText(call)?.Contains("sem resposta MCP em 5 s", StringComparison.Ordinal) == true &&
                              watch.Elapsed < TimeSpan.FromSeconds(30) &&
                              script.ToolCalls == 0;

            // Quando o processo passa a responder, a rodada seguinte conecta.
            listener = ListenerLookup.Found(2002, created.AddMinutes(1));
            script.InitializeGate = null;
            var recovered = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            return bounded && callBounded && recovered &&
                   Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true;
        }
        catch
        {
            return false;
        }
        finally
        {
            never.TrySetResult(true);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> OfflineBackendIsStartedOnDemandByACallTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-ondemand-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        // O backend simulado só responde enquanto um processo de verdade escuta a porta.
        var script = new BackendScript
        {
            Tools = new[] { "test_tool", "failing_tool" },
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            bool ok;
            await using (var hub = NewHub(profilePath, script))
            {
                var notifications = 0;
                hub.ToolsChanged += () => Interlocked.Increment(ref notifications);
                await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

                // A rodada periódica e o status nunca iniciam processo.
                var timerRound = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
                var idle = hub.StatusJson()["backends"]?[0];
                var nothingStarted = !timerRound &&
                                     Flag(idle?["online"]) == false &&
                                     Flag(idle?["startedByHub"]) == false &&
                                     idle?["processId"] is null &&
                                     ListenerInspector.Find(endpoint) == ListenerLookup.NotFound;
                // Uma tool que não é de backend algum não inicia nada.
                var foreign = await hub.CallAsync("other_test_tool", new JsonObject(), CancellationToken.None);
                var foreignBackend = hub.StatusJson()["backends"]?[0];
                nothingStarted = nothingStarted && IsError(foreign) &&
                                 Flag(foreignBackend?["startedByHub"]) == false &&
                                 ListenerInspector.Find(endpoint) == ListenerLookup.NotFound;
                var notificationsBefore = Volatile.Read(ref notifications);

                // A chamada precisa do backend: ele é iniciado pelo perfil carregado e a chamada é despachada.
                var first = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var started = hub.StatusJson()["backends"]?[0];
                var firstPid = started?["processId"]?.GetValue<int>() ?? 0;
                if (firstPid == 0)
                {
                    return false;
                }
                children.Add(firstPid);
                var startedOnDemand =
                    ResultText(first) == "ok:test_tool" && !IsError(first) && script.ToolCalls == 1 &&
                    Flag(started?["online"]) == true &&
                    Flag(started?["startedByHub"]) == true &&
                    started?["gateway"]?["listenerProcessId"]?.GetValue<int>() == firstPid &&
                    Flag(started["gateway"]?["profileApplied"]) == true &&
                    Text(started["gateway"]?["profileAppliedEvidence"]) == GatewayProfileEvidence.FromLoadedRevision &&
                    Volatile.Read(ref notifications) > notificationsBefore;

                // Com o backend no ar nada é reiniciado, e uma chamada que falha no backend não é reenviada.
                var second = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var failing = await hub.CallAsync("native_failing_tool", new JsonObject(), CancellationToken.None);
                var missing = await hub.CallAsync("native_missing_tool", new JsonObject(), CancellationToken.None);
                var steady =
                    ResultText(second) == "ok:test_tool" && IsError(failing) && IsError(missing) &&
                    script.ToolCalls == 3 &&
                    hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() == firstPid;

                // O Gateway cai: a chamada seguinte o inicia de novo e é despachada uma vez.
                KillProcess(firstPid);
                var third = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var secondPid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (secondPid != 0)
                {
                    children.Add(secondPid);
                }
                var restarted = ResultText(third) == "ok:test_tool" && script.ToolCalls == 4 &&
                                secondPid != 0 && secondPid != firstPid;

                // O mesmo vale para gxhub_call.
                KillProcess(secondPid);
                var generic = await hub.CallAsync(
                    "gxhub_call",
                    new JsonObject
                    {
                        ["backend"] = "native",
                        ["tool"] = "test_tool",
                        ["arguments"] = new JsonObject()
                    },
                    CancellationToken.None);
                var thirdPid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (thirdPid != 0)
                {
                    children.Add(thirdPid);
                }
                var genericRestarted = ResultText(generic) == "ok:test_tool" && script.ToolCalls == 5 &&
                                       thirdPid != 0 && thirdPid != secondPid;

                ok = nothingStarted && startedOnDemand && steady && restarted && genericRestarted;
            }

            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> FailedOnDemandStartIsAnErrorForTheCallTestAsync()
    {
        // O processo declarado sobe, mas escuta outra porta: o backend nunca abre o endpoint do perfil.
        async Task<bool> NeverOpensThePortAsync()
        {
            var root = NewTempRoot("GxMcpHub-startfail-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var otherPort = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var otherEndpoint = new Uri($"http://127.0.0.1:{otherPort}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };

            try
            {
                WriteProfile(
                    profilePath,
                    TestProfile(root, port, "native", withStartup: true, childPort: otherPort, startupTimeoutSeconds: 1));
                var startsBefore = ProcessSupervisor.TotalStarts;
                await using var hub = NewHub(profilePath, script);
                await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

                // O cliente recebe código e frase fixa: nem a mensagem da exceção, nem caminho algum.
                bool TimedOut(JsonNode? result)
                {
                    var text = ResultText(result);
                    return IsError(result) &&
                           text is not null &&
                           text.StartsWith(BackendRuntime.StartTimeoutCode + ":", StringComparison.Ordinal) &&
                           !text.Contains(root, StringComparison.OrdinalIgnoreCase);
                }

                // O processo que não abriu a porta no prazo é encerrado: a tentativa seguinte começa limpa.
                Task<bool> ChildIsGoneAsync() => WaitUntilAsync(
                    () => ListenerInspector.Find(otherEndpoint) == ListenerLookup.NotFound,
                    TimeSpan.FromSeconds(10));

                var first = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var afterFirst = hub.StatusJson()["backends"]?[0];
                var afterFirstError = Text(afterFirst?["error"]);
                var cleanAfterFirst =
                    TimedOut(first) &&
                    ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                    Flag(afterFirst?["online"]) == false &&
                    Flag(afterFirst?["startedByHub"]) == false &&
                    afterFirst?["processId"] is null &&
                    // O status diz por que o backend está fora, com o mesmo código.
                    afterFirstError is not null &&
                    afterFirstError.StartsWith(BackendRuntime.StartTimeoutCode + ":", StringComparison.Ordinal) &&
                    !afterFirstError.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                    await ChildIsGoneAsync();

                // Cada tentativa inicia um processo novo, em vez de esperar o anterior.
                var generic = await hub.CallAsync(
                    "gxhub_call",
                    new JsonObject
                    {
                        ["backend"] = "native",
                        ["tool"] = "test_tool",
                        ["arguments"] = new JsonObject()
                    },
                    CancellationToken.None);
                var afterGeneric = TimedOut(generic) &&
                                   ProcessSupervisor.TotalStarts == startsBefore + 2 &&
                                   await ChildIsGoneAsync();

                // O gxhub_refresh devolve o mesmo código como erro da tool.
                var refresh = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
                var afterRefresh = TimedOut(refresh) &&
                                   ProcessSupervisor.TotalStarts == startsBefore + 3 &&
                                   hub.StatusJson()["backends"]?[0]?["processId"] is null &&
                                   await ChildIsGoneAsync();

                // Nada chegou ao backend e o Hub continua atendendo.
                var statusCall = await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
                return cleanAfterFirst && afterGeneric && afterRefresh &&
                       script.ToolCalls == 0 &&
                       !IsError(statusCall);
            }
            catch
            {
                return false;
            }
            finally
            {
                DeleteQuietly(root);
            }
        }

        // O comando do perfil existe mas não é um executável: o sistema recusa iniciá-lo, e a mensagem da exceção
        // traz o caminho do comando e o diretório de trabalho.
        async Task<bool> CannotBeStartedAsync()
        {
            var root = NewTempRoot("GxMcpHub-startfail-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };

            try
            {
                var notExecutable = Path.Combine(root, "pacote-sintetico", "nao-e-executavel.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(notExecutable)!);
                File.WriteAllText(notExecutable, "texto");
                var profile = TestProfile(root, port, "native", withStartup: true);
                profile["backends"]![0]!["startup"]!["command"] = notExecutable;
                WriteProfile(profilePath, profile);
                var startsBefore = ProcessSupervisor.TotalStarts;
                await using var hub = NewHub(profilePath, script);
                await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

                bool CouldNotStart(string? text) =>
                    text is not null &&
                    text.StartsWith(BackendRuntime.StartFailedCode + ":", StringComparison.Ordinal) &&
                    !text.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("pacote-sintetico", StringComparison.OrdinalIgnoreCase);

                var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var afterCall = IsError(call) && CouldNotStart(ResultText(call)) &&
                                CouldNotStart(Text(hub.StatusJson()["backends"]?[0]?["error"]));
                var refresh = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
                var afterRefresh = IsError(refresh) && CouldNotStart(ResultText(refresh));
                var statusCall = await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);

                return afterCall && afterRefresh && !IsError(statusCall) &&
                       ProcessSupervisor.TotalStarts == startsBefore &&
                       script.ToolCalls == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                DeleteQuietly(root);
            }
        }

        return await NeverOpensThePortAsync() && await CannotBeStartedAsync();
    }

    private static async Task<bool> PeriodicReconcileSkipsWhileACallIsInFlightTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-periodic-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new BackendScript { CallEntered = entered, CallGate = release.Task };
        HubService? hub = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            hub = NewHub(profilePath, script);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            hub.StartBackgroundReconcile(TimeSpan.FromMilliseconds(50));
            hub.StartBackgroundReconcile(TimeSpan.FromMilliseconds(50));

            // Uma chamada longa segura o Hub; o perfil muda no disco enquanto ela está em curso.
            var call = hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
            await Task.Delay(400);
            var during = hub.StatusJson();
            var heldBack = during["lastProfileReloadTrigger"] is null &&
                           Flag(during["profileReloadPending"]) == true &&
                           !call.IsCompleted &&
                           hub.ReconcileRoundsSkipped > 0;

            // Terminada a chamada, uma rodada seguinte recarrega sozinha.
            release.SetResult(true);
            var result = await call.WaitAsync(TimeSpan.FromSeconds(10));
            var reloaded = await WaitUntilAsync(
                () => Text(hub.StatusJson()["lastProfileReloadTrigger"]) == "auto",
                TimeSpan.FromSeconds(10));
            var running = !hub.BackgroundReconcileStopped;

            await hub.DisposeAsync();
            var stopped = hub.BackgroundReconcileStopped;
            hub = null;
            return heldBack && ResultText(result) == "ok:test_tool" && reloaded && running && stopped;
        }
        catch
        {
            return false;
        }
        finally
        {
            release.TrySetResult(true);
            if (hub is not null)
            {
                await hub.DisposeAsync();
            }
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> AutoReconcileCanBeSwitchedOffTestAsync()
    {
        var parsed = HubService.ParseAutoReconcile(null) &&
                     HubService.ParseAutoReconcile(string.Empty) &&
                     HubService.ParseAutoReconcile("1") &&
                     HubService.ParseAutoReconcile("on") &&
                     !HubService.ParseAutoReconcile("0") &&
                     !HubService.ParseAutoReconcile(" FALSE ") &&
                     !HubService.ParseAutoReconcile("Off") &&
                     !HubService.ParseAutoReconcile("no");

        var root = NewTempRoot("GxMcpHub-switch-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            await using var hub = NewHub(profilePath, script, autoReconcile: false);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            hub.StartBackgroundReconcile(TimeSpan.FromMilliseconds(20));

            WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
            var round = await hub.ReconcileAsync(fromTimer: false, CancellationToken.None);
            await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
            var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            await Task.Delay(150);

            var status = hub.StatusJson();
            var backend = status["backends"]?[0];
            return parsed && !round && hub.BackgroundReconcileStopped &&
                   Flag(status["profileAutoReload"]?["enabled"]) == false &&
                   Text(status["profileAutoReload"]?["lastOutcome"]) == "none" &&
                   status["profileAutoReload"]?["lastCheckUtc"] is null &&
                   Flag(status["profileReloadPending"]) == true &&
                   status["lastProfileReloadTrigger"] is null &&
                   MarkOf(hub) == "before" &&
                   IsError(call) &&
                   ResultText(call)?.Contains("Tool não encontrada", StringComparison.Ordinal) == true &&
                   script.ToolCalls == 0 &&
                   Flag(backend?["startedByHub"]) == false &&
                   backend?["processId"] is null &&
                   ListenerInspector.Find(endpoint) == ListenerLookup.NotFound;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> ToolsChangedNotificationWaitsForTheClientInitializeTestAsync()
    {
        const string ListChanged = "notifications/tools/list_changed";
        var root = NewTempRoot("GxMcpHub-stdio-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var script = new BackendScript();
        var input = new ChannelLineReader();
        var output = new CapturingWriter();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            await using var hub = NewHub(profilePath, script);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var server = new StdioMcpServer(hub, input, output);
            var transport = server.RunAsync(CancellationToken.None);

            // O cliente ainda não fez initialize: a lista muda (perfil recarregado) e nada pode ser escrito.
            WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
            var changedBeforeInitialize = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            await Task.Delay(150);
            var silentBeforeInitialize = changedBeforeInitialize && output.Lines.Count == 0;

            input.Send(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"," +
                "\"params\":{\"protocolVersion\":\"2025-11-25\"}}");
            var initialized = await WaitUntilAsync(
                () => output.Lines.Any(line =>
                    line.Contains("\"id\":1", StringComparison.Ordinal) &&
                    line.Contains($"\"version\":\"{HubInfo.Version}\"", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10));
            // A resposta do initialize precisa estar escrita antes de qualquer aviso.
            var nothingBeforeTheAnswer = output.Lines.All(line => !line.Contains(ListChanged, StringComparison.Ordinal));

            // Depois do initialize, uma mudança que não vem de chamada alguma é avisada uma vez.
            WriteProfile(profilePath, TestProfile(root, port, "again", withStartup: true));
            var changedAfterInitialize = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var notified = await WaitUntilAsync(
                () => output.Lines.Count(line => line.Contains(ListChanged, StringComparison.Ordinal)) == 1,
                TimeSpan.FromSeconds(10));

            input.Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            var listed = await WaitUntilAsync(
                () => output.Lines.Any(line =>
                    line.Contains("\"id\":2", StringComparison.Ordinal) &&
                    line.Contains("native_test_tool", StringComparison.Ordinal) &&
                    line.Contains("gxhub_status", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10));

            // Fechada a entrada, o transporte termina; uma mudança tardia não escreve nem falha.
            input.Complete();
            await transport.WaitAsync(TimeSpan.FromSeconds(10));
            var linesAtTheEnd = output.Lines.Count;
            WriteProfile(profilePath, TestProfile(root, port, "late", withStartup: true));
            var changedAfterTheEnd = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            await Task.Delay(100);

            return silentBeforeInitialize && initialized && nothingBeforeTheAnswer &&
                   changedAfterInitialize && notified && listed &&
                   changedAfterTheEnd && output.Lines.Count == linesAtTheEnd;
        }
        catch
        {
            return false;
        }
        finally
        {
            input.Complete();
            DeleteQuietly(root);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Trava de promoção: o Hub não inicia backend enquanto quem promove segura o arquivo, e nunca pelo perfil antigo.
    // ------------------------------------------------------------------------------------------------------------

    private static bool PromotionLockProbeTest()
    {
        var root = NewTempRoot("GxMcpHub-lockprobe-test-");
        try
        {
            var path = PromotionLock.PathFor(new HubConfig { WorkspaceRoot = root });
            var expectedPath = Path.Combine(root, "GxMcpHub", "runtime", "promotion.lock");

            var absentFolder = !PromotionLock.IsHeld(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var absentFile = !PromotionLock.IsHeld(path);
            File.WriteAllText(path, string.Empty);
            var presentNotHeld = !PromotionLock.IsHeld(path);

            bool heldWithoutSharing;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                heldWithoutSharing = PromotionLock.IsHeld(path);
            }

            bool openButShared;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                openButShared = !PromotionLock.IsHeld(path);
            }

            var released = !PromotionLock.IsHeld(path);

            // Qualquer outra falha ao consultar (aqui, uma pasta no lugar do arquivo) é tratada como livre.
            File.Delete(path);
            Directory.CreateDirectory(path);
            var unreadable = !PromotionLock.IsHeld(path);

            return path == expectedPath && absentFolder && absentFile && presentNotHeld &&
                   heldWithoutSharing && openButShared && released && unreadable;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> PromotionAtHubStartLeavesTheBackendOfflineTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-lockstart-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        FileStream? promotion = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            promotion = HoldPromotionLock(root);
            var startsBefore = ProcessSupervisor.TotalStarts;
            bool ok;
            await using (var hub = NewHub(profilePath, script))
            {
                // O início do Hub não espera a promoção, por maior que seja a espera configurada para as chamadas.
                hub.PromotionLockWait = TimeSpan.FromSeconds(60);
                var watch = Stopwatch.StartNew();
                await hub.StartAsync(CancellationToken.None);
                watch.Stop();
                var held = hub.StatusJson();
                var heldBackend = held["backends"]?[0];
                var heldError = Text(heldBackend?["error"]);
                var cameUpOffline =
                    watch.Elapsed < TimeSpan.FromSeconds(10) &&
                    Flag(held["promotionLock"]?["held"]) == true &&
                    Flag(heldBackend?["online"]) == false &&
                    heldError is not null &&
                    heldError.StartsWith(PromotionLock.Code + ":", StringComparison.Ordinal) &&
                    !heldError.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                    Flag(heldBackend?["startedByHub"]) == false &&
                    heldBackend?["processId"] is null &&
                    hub.PromotionLockChecks == 1 &&
                    ProcessSupervisor.TotalStarts == startsBefore &&
                    ListenerInspector.Find(endpoint) == ListenerLookup.NotFound;

                // Com a promoção ainda em andamento, a rodada periódica não muda nada.
                var roundWhileHeld = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                var stillRefused =
                    !roundWhileHeld &&
                    Text(hub.StatusJson()["backends"]?[0]?["error"])
                        ?.StartsWith(PromotionLock.Code + ":", StringComparison.Ordinal) == true;

                // A promoção termina: a rodada reavalia o backend, ainda sem iniciar processo.
                promotion.Dispose();
                promotion = null;
                var roundAfter = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                var free = hub.StatusJson();
                var freeBackend = free["backends"]?[0];
                var freeError = Text(freeBackend?["error"]);
                var reevaluated =
                    roundAfter &&
                    Flag(free["promotionLock"]?["held"]) == false &&
                    Flag(freeBackend?["online"]) == false &&
                    freeError is not null &&
                    !freeError.StartsWith(PromotionLock.Code, StringComparison.Ordinal) &&
                    Flag(freeBackend?["startedByHub"]) == false &&
                    ProcessSupervisor.TotalStarts == startsBefore;

                // E a chamada seguinte inicia o backend.
                var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (pid != 0)
                {
                    children.Add(pid);
                }
                var startedByTheCall = ResultText(call) == "ok:test_tool" && pid != 0 &&
                                       ProcessSupervisor.TotalStarts == startsBefore + 1;

                ok = cameUpOffline && stillRefused && reevaluated && startedByTheCall;
            }

            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            promotion?.Dispose();
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> PromotionMakesCallsAndRefreshWaitAndThenRefuseTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-lockcall-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        FileStream? promotion = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            var startsBefore = ProcessSupervisor.TotalStarts;
            bool ok;
            await using (var hub = NewHub(profilePath, script))
            {
                // A espera real é de 120 s; aqui ela é encurtada.
                hub.PromotionLockWait = TimeSpan.FromMilliseconds(1200);
                hub.PromotionLockPoll = TimeSpan.FromMilliseconds(100);
                await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
                promotion = HoldPromotionLock(root);

                bool Refused(JsonNode? result, TimeSpan elapsed)
                {
                    var text = ResultText(result);
                    return IsError(result) &&
                           text is not null &&
                           text.StartsWith(PromotionLock.Code + ":", StringComparison.Ordinal) &&
                           !text.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                           elapsed >= TimeSpan.FromMilliseconds(1000) &&
                           elapsed < TimeSpan.FromSeconds(20);
                }

                var watch = Stopwatch.StartNew();
                var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var callRefused = Refused(call, watch.Elapsed);

                watch.Restart();
                var generic = await hub.CallAsync(
                    "gxhub_call",
                    new JsonObject
                    {
                        ["backend"] = "native",
                        ["tool"] = "test_tool",
                        ["arguments"] = new JsonObject()
                    },
                    CancellationToken.None);
                var genericRefused = Refused(generic, watch.Elapsed);

                watch.Restart();
                var refresh = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
                var refreshRefused = Refused(refresh, watch.Elapsed);

                var status = hub.StatusJson();
                var backend = status["backends"]?[0];
                var nothingStarted =
                    script.ToolCalls == 0 &&
                    Flag(status["promotionLock"]?["held"]) == true &&
                    Flag(backend?["online"]) == false &&
                    Flag(backend?["startedByHub"]) == false &&
                    Text(backend?["error"])?.StartsWith(PromotionLock.Code + ":", StringComparison.Ordinal) == true &&
                    ProcessSupervisor.TotalStarts == startsBefore &&
                    ListenerInspector.Find(endpoint) == ListenerLookup.NotFound;

                // Terminada a promoção, o mesmo gxhub_refresh inicia o backend.
                promotion.Dispose();
                promotion = null;
                var afterRelease = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
                var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (pid != 0)
                {
                    children.Add(pid);
                }
                var startedAfterwards = !IsError(afterRelease) && pid != 0 &&
                                        ProcessSupervisor.TotalStarts == startsBefore + 1;

                ok = callRefused && genericRefused && refreshRefused && nothingStarted && startedAfterwards;
            }

            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            promotion?.Dispose();
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> PromotionLockIsNotSwitchedOffTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-lockswitch-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        FileStream? promotion = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            promotion = HoldPromotionLock(root);
            var startsBefore = ProcessSupervisor.TotalStarts;
            bool ok;
            // Com GXMCP_HUB_AUTO_RECONCILE desligado a trava continua valendo: ela é proteção.
            await using (var hub = NewHub(profilePath, script, autoReconcile: false))
            {
                hub.PromotionLockWait = TimeSpan.FromMilliseconds(600);
                hub.PromotionLockPoll = TimeSpan.FromMilliseconds(100);
                await hub.StartAsync(CancellationToken.None);
                var atStart = hub.StatusJson()["backends"]?[0];
                var refresh = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
                var refused =
                    Text(atStart?["error"])?.StartsWith(PromotionLock.Code + ":", StringComparison.Ordinal) == true &&
                    Flag(atStart?["online"]) == false &&
                    IsError(refresh) &&
                    ResultText(refresh)?.StartsWith(PromotionLock.Code + ":", StringComparison.Ordinal) == true &&
                    ProcessSupervisor.TotalStarts == startsBefore &&
                    ListenerInspector.Find(endpoint) == ListenerLookup.NotFound;

                promotion.Dispose();
                promotion = null;
                var afterRelease = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
                var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (pid != 0)
                {
                    children.Add(pid);
                }

                ok = refused && !IsError(afterRelease) && pid != 0 &&
                     ProcessSupervisor.TotalStarts == startsBefore + 1;
            }

            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            promotion?.Dispose();
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> BackendStartsOnceFromTheNewProfileAfterThePromotionTestAsync() =>
        await ReleasedDuringTheWaitScenarioAsync(viaRefresh: false) &&
        await ReleasedDuringTheWaitScenarioAsync(viaRefresh: true);

    private static async Task<bool> ReleasedDuringTheWaitScenarioAsync(bool viaRefresh)
    {
        var root = NewTempRoot("GxMcpHub-lockrelease-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var oldPort = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var oldEndpoint = new Uri($"http://127.0.0.1:{oldPort}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        FileStream? promotion = null;

        try
        {
            // O perfil antigo iniciaria um processo que escuta outra porta: se ele fosse usado, apareceria ali.
            WriteProfile(
                profilePath,
                TestProfile(root, port, "before", withStartup: true, childPort: oldPort, startupTimeoutSeconds: 3));
            var startsBefore = ProcessSupervisor.TotalStarts;
            bool ok;
            await using (var hub = NewHub(profilePath, script))
            {
                hub.PromotionLockWait = TimeSpan.FromSeconds(60);
                hub.PromotionLockPoll = TimeSpan.FromMilliseconds(100);
                await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
                var oldRevision = Text(hub.StatusJson()["profileRevision"]);
                promotion = HoldPromotionLock(root);

                var call = viaRefresh
                    ? hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None)
                    : hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                await Task.Delay(500);
                var waiting = !call.IsCompleted && ProcessSupervisor.TotalStarts == startsBefore;

                // A promoção reescreve o perfil com a trava na mão e só então a solta.
                WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
                await Task.Delay(300);
                var stillWaiting = !call.IsCompleted && ProcessSupervisor.TotalStarts == startsBefore;
                promotion.Dispose();
                promotion = null;

                var result = await call.WaitAsync(TimeSpan.FromSeconds(60));
                var status = hub.StatusJson();
                var backend = status["backends"]?[0];
                var gateway = backend?["gateway"];
                var pid = backend?["processId"]?.GetValue<int>() ?? 0;
                if (pid != 0)
                {
                    children.Add(pid);
                }

                ok = waiting && stillWaiting && !IsError(result) &&
                     (viaRefresh || ResultText(result) == "ok:test_tool") &&
                     script.ToolCalls == (viaRefresh ? 0 : 1) &&
                     // Um único processo iniciado, e pelo perfil novo.
                     ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                     pid != 0 &&
                     MarkOf(hub) == "after" &&
                     Text(status["profileRevision"]) != oldRevision &&
                     Text(status["lastProfileReloadTrigger"]) == (viaRefresh ? "manual" : "auto") &&
                     Flag(status["promotionLock"]?["held"]) == false &&
                     Flag(backend?["online"]) == true &&
                     gateway?["listenerProcessId"]?.GetValue<int>() == pid &&
                     Text(gateway?["startedFromProfileRevision"]) == Text(status["profileRevision"]) &&
                     ListenerInspector.Find(oldEndpoint) == ListenerLookup.NotFound;
            }

            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            promotion?.Dispose();
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> UnheldPromotionLockFileHasNoEffectTestAsync()
    {
        async Task<bool> StartsNormallyAsync(bool createTheFolder, bool createTheFile)
        {
            var root = NewTempRoot("GxMcpHub-lockfree-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var script = new BackendScript();
            var children = new List<int>();
            try
            {
                WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
                var lockPath = PromotionLock.PathFor(new HubConfig { WorkspaceRoot = root });
                if (createTheFolder)
                {
                    // A pasta runtime existe, como na instalação real; o arquivo da trava, não.
                    Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                }

                if (createTheFile)
                {
                    // O arquivo ficou de uma promoção anterior: existe, mas ninguém o segura.
                    HoldPromotionLock(root).Dispose();
                }

                var startsBefore = ProcessSupervisor.TotalStarts;
                bool ok;
                await using (var hub = NewHub(profilePath, script))
                {
                    hub.PromotionLockWait = TimeSpan.FromSeconds(60);
                    var watch = Stopwatch.StartNew();
                    await hub.StartAsync(CancellationToken.None);
                    watch.Stop();
                    var status = hub.StatusJson();
                    var pid = status["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                    if (pid != 0)
                    {
                        children.Add(pid);
                    }

                    ok = watch.Elapsed < TimeSpan.FromSeconds(25) &&
                         File.Exists(lockPath) == createTheFile &&
                         Flag(status["promotionLock"]?["held"]) == false &&
                         Flag(status["backends"]?[0]?["online"]) == true &&
                         pid != 0 &&
                         ProcessSupervisor.TotalStarts == startsBefore + 1;
                }

                return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
            }
            catch
            {
                return false;
            }
            finally
            {
                KillLeftovers(children);
                DeleteQuietly(root);
            }
        }

        return await StartsNormallyAsync(createTheFolder: false, createTheFile: false) &&
               await StartsNormallyAsync(createTheFolder: true, createTheFile: false) &&
               await StartsNormallyAsync(createTheFolder: true, createTheFile: true);
    }

    private static async Task<bool> ListeningGatewayIsConnectedWithoutAskingForTheLockTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-locklisten-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var script = new BackendScript();
        FileStream? promotion = null;
        // O "Gateway" já em execução: um listener de verdade na porta do backend.
        var gateway = new TcpListener(IPAddress.Loopback, 0);
        gateway.Start();

        try
        {
            var port = ((IPEndPoint)gateway.LocalEndpoint).Port;
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            promotion = HoldPromotionLock(root);
            var startsBefore = ProcessSupervisor.TotalStarts;
            await using var hub = NewHub(profilePath, script);
            // Se a trava fosse consultada, qualquer um dos passos abaixo ficaria preso por um minuto.
            hub.PromotionLockWait = TimeSpan.FromSeconds(60);

            var watch = Stopwatch.StartNew();
            await hub.StartAsync(CancellationToken.None);
            var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var refresh = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
            var generic = await hub.CallAsync(
                "gxhub_call",
                new JsonObject
                {
                    ["backend"] = "native",
                    ["tool"] = "test_tool",
                    ["arguments"] = new JsonObject()
                },
                CancellationToken.None);
            watch.Stop();

            var status = hub.StatusJson();
            var backend = status["backends"]?[0];
            return watch.Elapsed < TimeSpan.FromSeconds(20) &&
                   ResultText(call) == "ok:test_tool" &&
                   !IsError(refresh) &&
                   ResultText(generic) == "ok:test_tool" &&
                   script.ToolCalls == 2 &&
                   // O status mostra a promoção, mas o caminho de início nunca perguntou por ela.
                   Flag(status["promotionLock"]?["held"]) == true &&
                   hub.PromotionLockChecks == 0 &&
                   Flag(backend?["online"]) == true &&
                   Flag(backend?["startedByHub"]) == false &&
                   backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() == Environment.ProcessId &&
                   ProcessSupervisor.TotalStarts == startsBefore;
        }
        catch
        {
            return false;
        }
        finally
        {
            promotion?.Dispose();
            gateway.Stop();
            DeleteQuietly(root);
        }
    }

    // Sem promoção alguma, o perfil muda no disco antes do início do backend: depois de o Hub existir, ou no instante
    // entre a leitura do arquivo pelo Hub e o resto da inicialização dele.
    private static async Task<bool> ProfileChangedBeforeTheStartIsReloadedFirstTestAsync() =>
        await ProfileChangedBeforeTheStartScenarioAsync(betweenLoadAndConstruction: false) &&
        await ProfileChangedBeforeTheStartScenarioAsync(betweenLoadAndConstruction: true);

    private static async Task<bool> ProfileChangedBeforeTheStartScenarioAsync(bool betweenLoadAndConstruction)
    {
        var root = NewTempRoot("GxMcpHub-prestart-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var oldPort = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var oldEndpoint = new Uri($"http://127.0.0.1:{oldPort}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();

        try
        {
            // O perfil antigo iniciaria um processo que escuta outra porta: se ele fosse usado, apareceria ali.
            WriteProfile(
                profilePath,
                TestProfile(root, port, "before", withStartup: true, childPort: oldPort, startupTimeoutSeconds: 3));
            var startsBefore = ProcessSupervisor.TotalStarts;
            var loaded = HubConfigLoader.Load(profilePath);
            if (betweenLoadAndConstruction)
            {
                WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
            }

            bool ok;
            await using (var hub = NewHub(profilePath, script, loaded: loaded))
            {
                if (!betweenLoadAndConstruction)
                {
                    WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
                }

                await hub.StartAsync(CancellationToken.None);
                var status = hub.StatusJson();
                var backend = status["backends"]?[0];
                var pid = backend?["processId"]?.GetValue<int>() ?? 0;
                if (pid != 0)
                {
                    children.Add(pid);
                }

                ok = ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                     pid != 0 &&
                     MarkOf(hub) == "after" &&
                     Text(status["lastProfileReloadTrigger"]) == "auto" &&
                     Flag(status["profileReloadPending"]) == false &&
                     Flag(backend?["online"]) == true &&
                     backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() == pid &&
                     ListenerInspector.Find(oldEndpoint) == ListenerLookup.NotFound;
            }

            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> StaleProfileNeverStartsABackendTestAsync()
    {
        // Perfil em disco que não carrega: a chamada recebe o erro e nada é iniciado pelo perfil antigo.
        async Task<bool> InvalidProfileAsync()
        {
            var root = NewTempRoot("GxMcpHub-stale-invalid-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var oldPort = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var oldEndpoint = new Uri($"http://127.0.0.1:{oldPort}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var children = new List<int>();
            try
            {
                WriteProfile(
                    profilePath,
                    TestProfile(root, port, "before", withStartup: true, childPort: oldPort, startupTimeoutSeconds: 3));
                var startsBefore = ProcessSupervisor.TotalStarts;
                bool ok;
                await using (var hub = NewHub(profilePath, script))
                {
                    await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

                    // Arquivo preso por outro processo: não dá para confirmar que o perfil é o carregado.
                    bool unreadable;
                    using (new FileStream(profilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        var held = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                        unreadable = IsError(held) &&
                                     ResultText(held)?.StartsWith("profile-unreadable:", StringComparison.Ordinal) == true &&
                                     ProcessSupervisor.TotalStarts == startsBefore &&
                                     ListenerInspector.Find(oldEndpoint) == ListenerLookup.NotFound;
                    }

                    File.WriteAllText(profilePath, "{ \"profileName\": \"reconcile-test\", \"workspaceRoot\": ");
                    var refused = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                    var refusedText = ResultText(refused);
                    var nothingStarted =
                        IsError(refused) &&
                        refusedText is not null &&
                        refusedText.StartsWith("invalid-profile:", StringComparison.Ordinal) &&
                        !refusedText.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                        ProcessSupervisor.TotalStarts == startsBefore &&
                        ListenerInspector.Find(oldEndpoint) == ListenerLookup.NotFound;

                    // Corrigido o arquivo, a chamada seguinte recarrega e inicia pelo perfil novo.
                    WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
                    var served = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                    var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                    if (pid != 0)
                    {
                        children.Add(pid);
                    }

                    ok = unreadable && nothingStarted && ResultText(served) == "ok:test_tool" && pid != 0 &&
                         ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                         MarkOf(hub) == "after" &&
                         ListenerInspector.Find(oldEndpoint) == ListenerLookup.NotFound;
                }

                return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
            }
            catch
            {
                return false;
            }
            finally
            {
                KillLeftovers(children);
                DeleteQuietly(root);
            }
        }

        // Perfil alterado fora de backends[].startup (aqui, readOnlyTools mais largo): a recarga automática não o
        // aplica, a chamada não inicia nada e manda usar gxhub_refresh, que carrega o perfil e inicia por ele.
        async Task<bool> OutsideStartupAsync()
        {
            var root = NewTempRoot("GxMcpHub-stale-manual-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var oldPort = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var oldEndpoint = new Uri($"http://127.0.0.1:{oldPort}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var children = new List<int>();
            try
            {
                WriteProfile(
                    profilePath,
                    TestProfile(root, port, "before", withStartup: true, childPort: oldPort, startupTimeoutSeconds: 3));
                var startsBefore = ProcessSupervisor.TotalStarts;
                bool ok;
                await using (var hub = NewHub(profilePath, script))
                {
                    await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
                    var widened = TestProfile(root, port, "after", withStartup: true);
                    widened["backends"]![0]!["readOnlyTools"] = new JsonArray("genexus_edit");
                    WriteProfile(profilePath, widened);

                    var refused = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                    var refusedText = ResultText(refused);
                    var deferred =
                        IsError(refused) &&
                        refusedText is not null &&
                        refusedText.StartsWith(HubService.ProfileReloadDeferredCode + ":", StringComparison.Ordinal) &&
                        refusedText.Contains("campos que a recarga automática não aplica", StringComparison.Ordinal) &&
                        !refusedText.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                        ProcessSupervisor.TotalStarts == startsBefore &&
                        ListenerInspector.Find(oldEndpoint) == ListenerLookup.NotFound &&
                        MarkOf(hub) == "before" &&
                        hub.Config.Backends[0].ReadOnlyTools.Count == 0 &&
                        Text(hub.StatusJson()["profileAutoReload"]?["deferredReason"]) ==
                        HubService.DeferredToManualRefresh;

                    var refresh = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
                    var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                    if (pid != 0)
                    {
                        children.Add(pid);
                    }

                    ok = deferred && !IsError(refresh) && pid != 0 &&
                         ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                         MarkOf(hub) == "after" &&
                         hub.Config.Backends[0].ReadOnlyTools.Count == 1 &&
                         Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true &&
                         ListenerInspector.Find(oldEndpoint) == ListenerLookup.NotFound;
                }

                return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
            }
            catch
            {
                return false;
            }
            finally
            {
                KillLeftovers(children);
                DeleteQuietly(root);
            }
        }

        // Perfil alterado com um processo deste Hub vivo, ainda dentro do prazo de início dele, sem ninguém escutando
        // (o wrapper segue vivo e o processo que ele iniciou caiu): a chamada não inicia nada e manda usar
        // gxhub_refresh, que encerra o processo antigo e inicia pelo perfil novo.
        async Task<bool> OwnProcessAliveAsync()
        {
            var root = NewTempRoot("GxMcpHub-stale-deferred-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var children = new List<int>();
            try
            {
                WriteProfile(
                    profilePath,
                    TestProfile(root, port, "before", withStartup: true, wrapper: true, startupTimeoutSeconds: 60));
                bool ok;
                await using (var hub = NewHub(profilePath, script))
                {
                    await hub.StartAsync(CancellationToken.None);
                    var started = hub.StatusJson()["backends"]?[0];
                    var wrapperPid = started?["processId"]?.GetValue<int>() ?? 0;
                    var listenerPid = started?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
                    if (wrapperPid == 0 || listenerPid == 0 || listenerPid == wrapperPid)
                    {
                        return false;
                    }
                    children.Add(wrapperPid);
                    children.Add(listenerPid);
                    KillProcess(listenerPid);
                    var startsBefore = ProcessSupervisor.TotalStarts;

                    WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
                    var refused = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                    var refusedText = ResultText(refused);
                    var deferred =
                        IsError(refused) &&
                        refusedText is not null &&
                        refusedText.StartsWith(HubService.ProfileReloadDeferredCode + ":", StringComparison.Ordinal) &&
                        refusedText.Contains("há Gateway iniciado por este Hub", StringComparison.Ordinal) &&
                        !refusedText.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                        ProcessSupervisor.TotalStarts == startsBefore &&
                        IsAlive(wrapperPid) &&
                        MarkOf(hub) == "before" &&
                        Text(hub.StatusJson()["profileAutoReload"]?["deferredReason"]) ==
                        HubService.DeferredByOwnGateway;

                    var refresh = await hub.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
                    var newPid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                    if (newPid != 0)
                    {
                        children.Add(newPid);
                    }

                    ok = deferred && !IsError(refresh) && newPid != 0 && newPid != wrapperPid &&
                         WaitForExit(wrapperPid, TimeSpan.FromSeconds(10)) &&
                         ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                         MarkOf(hub) == "after" &&
                         Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true;
                }

                return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
            }
            catch
            {
                return false;
            }
            finally
            {
                KillLeftovers(children);
                DeleteQuietly(root);
            }
        }

        return await InvalidProfileAsync() && await OutsideStartupAsync() && await OwnProcessAliveAsync();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Correções da revisão independente da 0.2.0.
    // ------------------------------------------------------------------------------------------------------------

    private static async Task<bool> GatewayStartedThroughAWrapperIsOwnTestAsync() =>
        await GatewayStartedThroughAWrapperScenarioAsync(editPendingWhenThePortIsLost: false) &&
        await GatewayStartedThroughAWrapperScenarioAsync(editPendingWhenThePortIsLost: true);

    private static async Task<bool> GatewayStartedThroughAWrapperScenarioAsync(bool editPendingWhenThePortIsLost)
    {
        var root = NewTempRoot("GxMcpHub-wrapper-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var script = new BackendScript();
        var children = new List<int>();
        TcpListener? external = null;

        try
        {
            // O processo que o Hub inicia é um wrapper; quem escuta a porta é o processo que o wrapper inicia.
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true, wrapper: true));
            bool ok;
            await using (var hub = NewHub(profilePath, script))
            {
                await hub.StartAsync(CancellationToken.None);
                var status = hub.StatusJson();
                var backend = status["backends"]?[0];
                var gateway = backend?["gateway"];
                var wrapperPid = backend?["processId"]?.GetValue<int>() ?? 0;
                var listenerPid = gateway?["listenerProcessId"]?.GetValue<int>() ?? 0;
                if (wrapperPid == 0 || listenerPid == 0 || listenerPid == wrapperPid)
                {
                    return false;
                }
                children.Add(wrapperPid);
                children.Add(listenerPid);

                var ownThroughTheWrapper =
                    Flag(gateway?["startedByThisHub"]) == true &&
                    Text(gateway?["startedFromProfileRevision"]) == Text(status["profileRevision"]) &&
                    Flag(gateway?["profileApplied"]) == true &&
                    Text(gateway?["profileAppliedEvidence"]) == GatewayProfileEvidence.FromLoadedRevision;

                // A mesma pergunta feita direto à árvore de processos do sistema.
                var tree =
                    ProcessTree.IsSameOrDescendant(wrapperPid, listenerPid) == true &&
                    ProcessTree.IsSameOrDescendant(Environment.ProcessId, listenerPid) == true &&
                    ProcessTree.IsSameOrDescendant(wrapperPid, wrapperPid) == true &&
                    ProcessTree.IsSameOrDescendant(listenerPid, wrapperPid) == false &&
                    ProcessTree.IsSameOrDescendant(wrapperPid, Environment.ProcessId) == false;

                // E é por ser deste Hub que a edição do perfil fica adiada.
                WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true, wrapper: true));
                var changed = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                var deferred = hub.StatusJson()["profileAutoReload"];
                var deferredWhileOwn =
                    !changed &&
                    Text(deferred?["lastOutcome"]) == "deferred" &&
                    Text(deferred?["deferredReason"]) == HubService.DeferredByOwnGateway &&
                    MarkOf(hub) == "before" &&
                    IsAlive(wrapperPid) && IsAlive(listenerPid);

                if (!editPendingWhenThePortIsLost)
                {
                    // A edição é desfeita: nada pendente.
                    WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true, wrapper: true));
                    deferredWhileOwn = deferredWhileOwn &&
                                       !await hub.ReconcileAsync(fromTimer: true, CancellationToken.None) &&
                                       Text(hub.StatusJson()["profileAutoReload"]?["lastOutcome"]) == "none";
                }

                // O processo que escutava cai e outro lançador toma a porta; o wrapper deste Hub segue vivo, sem
                // servir ninguém. Uma rodada passiva o encerra e o backend passa a ser de outro lançador; com edição
                // pendente, ela é carregada nessa mesma rodada, em vez de ficar adiada por um processo que não é o
                // Gateway.
                KillProcess(listenerPid);
                external = new TcpListener(IPAddress.Loopback, port);
                external.Start();
                var lostRound = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                var after = hub.StatusJson();
                var afterBackend = after["backends"]?[0];
                var afterGateway = afterBackend?["gateway"];
                var treatedAsForeign =
                    lostRound &&
                    WaitForExit(wrapperPid, TimeSpan.FromSeconds(5)) &&
                    Flag(afterBackend?["online"]) == true &&
                    Flag(afterBackend?["startedByHub"]) == false &&
                    afterBackend?["processId"] is null &&
                    afterGateway?["listenerProcessId"]?.GetValue<int>() == Environment.ProcessId &&
                    Flag(afterGateway?["startedByThisHub"]) == false &&
                    afterGateway?["startedFromProfileRevision"] is null &&
                    MarkOf(hub) == (editPendingWhenThePortIsLost ? "after" : "before") &&
                    Text(after["profileAutoReload"]?["lastOutcome"]) ==
                    (editPendingWhenThePortIsLost ? "reloaded" : "none") &&
                    Flag(after["profileReloadPending"]) == false;

                ok = ownThroughTheWrapper && tree && deferredWhileOwn && treatedAsForeign;
            }

            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            external?.Stop();
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> ProcessThatLostThePortIsNotTheGatewayTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-lostport-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var otherPort = ReserveFreePort();
        var otherEndpoint = new Uri($"http://127.0.0.1:{otherPort}/mcp");
        var externalUp = 0;
        var script = new BackendScript { Reachable = () => Volatile.Read(ref externalUp) == 1 };
        var children = new List<int>();
        TcpListener? external = null;

        try
        {
            // O processo que o Hub inicia fica vivo sem escutar o endpoint do perfil, como um Gateway que perdeu a
            // porta para outro e seguiu em modo proxy.
            WriteProfile(
                profilePath,
                TestProfile(root, port, "before", withStartup: true, childPort: otherPort, startupTimeoutSeconds: 30));
            var startsBefore = ProcessSupervisor.TotalStarts;
            await using var hub = NewHub(profilePath, script);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

            var call = hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var started = await WaitUntilAsync(
                () => hub.StatusJson()["backends"]?[0]?["processId"] is not null,
                TimeSpan.FromSeconds(15));
            var lostPid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (lostPid != 0)
            {
                children.Add(lostPid);
            }

            // Outro lançador sobe o Gateway de verdade na porta enquanto o Hub espera o endpoint abrir.
            Volatile.Write(ref externalUp, 1);
            external = new TcpListener(IPAddress.Loopback, port);
            external.Start();
            var result = await call.WaitAsync(TimeSpan.FromSeconds(40));
            var status = hub.StatusJson();
            var backend = status["backends"]?[0];
            var gateway = backend?["gateway"];
            // O processo que perdeu a porta foi encerrado, e o backend é tratado como de outro lançador.
            var treatedAsForeign =
                started && lostPid != 0 &&
                ResultText(result) == "ok:test_tool" &&
                WaitForExit(lostPid, TimeSpan.FromSeconds(10)) &&
                Flag(backend?["online"]) == true &&
                Flag(backend?["startedByHub"]) == false &&
                backend?["processId"] is null &&
                gateway?["listenerProcessId"]?.GetValue<int>() == Environment.ProcessId &&
                Flag(gateway?["startedByThisHub"]) == false &&
                gateway?["startedFromProfileRevision"] is null &&
                Text(gateway?["profileAppliedEvidence"]) == GatewayProfileEvidence.StartedBeforeEdit &&
                ProcessSupervisor.TotalStarts == startsBefore + 1;

            // A edição do perfil não fica adiada para sempre por um processo que não é o Gateway.
            WriteProfile(
                profilePath,
                TestProfile(root, port, "after", withStartup: true, childPort: otherPort, startupTimeoutSeconds: 1));
            var reloaded = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var notDeferred = reloaded && MarkOf(hub) == "after" &&
                              Text(hub.StatusJson()["profileAutoReload"]?["lastOutcome"]) == "reloaded";

            // O Gateway de verdade cai: cada chamada inicia um processo novo (e o encerra se ele não abrir a porta),
            // em vez de esperar um processo antigo que nunca vai abrir.
            external.Stop();
            external = null;
            Volatile.Write(ref externalUp, 0);
            await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var retryOne = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var afterOne = ProcessSupervisor.TotalStarts;
            var retryTwo = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var freshStarts =
                IsError(retryOne) && IsError(retryTwo) &&
                ResultText(retryOne)?.StartsWith(BackendRuntime.StartTimeoutCode + ":", StringComparison.Ordinal) == true &&
                ResultText(retryTwo)?.StartsWith(BackendRuntime.StartTimeoutCode + ":", StringComparison.Ordinal) == true &&
                afterOne == startsBefore + 2 &&
                ProcessSupervisor.TotalStarts == startsBefore + 3 &&
                hub.StatusJson()["backends"]?[0]?["processId"] is null &&
                await WaitUntilAsync(
                    () => ListenerInspector.Find(otherEndpoint) == ListenerLookup.NotFound,
                    TimeSpan.FromSeconds(10));

            return treatedAsForeign && notDeferred && freshStarts;
        }
        catch
        {
            return false;
        }
        finally
        {
            external?.Stop();
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> OwnProcessThatStoppedListeningIsReplacedTestAsync()
    {
        const int StartupTimeoutSeconds = 5;
        var root = NewTempRoot("GxMcpHub-stuck-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();

        try
        {
            // Wrapper vivo, e o processo que escutava por ele cai: sobra um processo deste Hub que não vai abrir a porta.
            WriteProfile(
                profilePath,
                TestProfile(
                    root,
                    port,
                    "native",
                    withStartup: true,
                    wrapper: true,
                    startupTimeoutSeconds: StartupTimeoutSeconds));
            bool ok;
            await using (var hub = NewHub(profilePath, script))
            {
                await hub.StartAsync(CancellationToken.None);
                var backend = hub.StatusJson()["backends"]?[0];
                var wrapperPid = backend?["processId"]?.GetValue<int>() ?? 0;
                var listenerPid = backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
                if (wrapperPid == 0 || listenerPid == 0 || listenerPid == wrapperPid)
                {
                    return false;
                }
                children.Add(wrapperPid);
                children.Add(listenerPid);

                KillProcess(listenerPid);
                // Passa o prazo de início do processo antigo.
                await Task.Delay(TimeSpan.FromSeconds(StartupTimeoutSeconds) + TimeSpan.FromMilliseconds(400));
                var stuck = IsAlive(wrapperPid) && ListenerInspector.Find(endpoint) == ListenerLookup.NotFound;

                // A rodada periódica nota o backend fora do ar e não encerra nem inicia nada. O processo segue
                // sendo deste Hub, mas não há Gateway atrás do endpoint para o status descrever.
                await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                var idle = hub.StatusJson()["backends"]?[0];
                var timerLeftItAlone =
                    IsAlive(wrapperPid) &&
                    Flag(idle?["online"]) == false &&
                    Flag(idle?["startedByHub"]) == true &&
                    idle?["processId"]?.GetValue<int>() == wrapperPid &&
                    Flag(idle?["gateway"]?["startedByThisHub"]) == false &&
                    idle?["gateway"]?["startedFromProfileRevision"] is null &&
                    Text(idle?["gateway"]?["profileAppliedEvidence"]) == GatewayProfileEvidence.ListenerNotFound;

                var startsBefore = ProcessSupervisor.TotalStarts;
                var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var replacement = hub.StatusJson()["backends"]?[0];
                var newPid = replacement?["processId"]?.GetValue<int>() ?? 0;
                var newListener = replacement?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
                if (newPid != 0)
                {
                    children.Add(newPid);
                }
                if (newListener != 0)
                {
                    children.Add(newListener);
                }

                // O processo antigo é encerrado e a chamada é atendida por um novo, sem esperar pelo que não vai abrir.
                ok = stuck && timerLeftItAlone &&
                     ResultText(call) == "ok:test_tool" &&
                     ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                     newPid != 0 && newPid != wrapperPid &&
                     WaitForExit(wrapperPid, TimeSpan.FromSeconds(10)) &&
                     Flag(replacement?["gateway"]?["startedByThisHub"]) == true;
            }

            return ok && children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10)));
        }
        catch
        {
            return false;
        }
        finally
        {
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static bool ProcessTreeTest()
    {
        var t0 = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        // Quem iniciou quem, como o sistema anota: o PID do pai fica gravado mesmo depois de o pai sair e de o número
        // ser dado a outro processo.
        var parents = new Dictionary<int, int>
        {
            [30] = 20, [20] = 10, [10] = 4,     // 10 -> 20 -> 30
            [40] = 99,                           // o pai anotado não existe mais
            [80] = 81,                           // o PID do pai foi reaproveitado por um processo mais novo
            [70] = 71, [71] = 10,                // idem, no meio da cadeia
            [90] = 91, [91] = 10,                // 91 não pode ser datado (outra conta)
            [50] = 60, [60] = 50                 // anotação em laço
        };
        var created = new Dictionary<int, DateTime>
        {
            [4] = t0.AddHours(-1), [10] = t0, [20] = t0.AddMinutes(1), [30] = t0.AddMinutes(2),
            [40] = t0, [80] = t0, [81] = t0.AddMinutes(5), [70] = t0.AddMinutes(1), [71] = t0.AddMinutes(5),
            [90] = t0.AddMinutes(3), [50] = t0, [60] = t0
        };
        (DateTime? Value, bool Gone) Started(int processId) =>
            processId == 99 ? (null, true)
            : created.TryGetValue(processId, out var value) ? (value, false)
            : (null, false);

        bool? Descends(int ancestor, int process) => ProcessTree.Descends(ancestor, process, parents, Started);

        return Descends(10, 10) == true &&
               Descends(10, 20) == true &&
               Descends(10, 30) == true &&
               Descends(20, 30) == true &&
               // O ancestral não aparece na cadeia de pais: com certeza não descende. Só essa resposta autoriza
               // tratar um processo deste Hub como perdido.
               Descends(30, 10) == false &&
               Descends(20, 10) == false &&
               Descends(99, 30) == false &&
               Descends(10, 12345) == false &&
               // Um laço não prende nem vira resposta positiva.
               Descends(10, 50) == false &&
               // O ancestral aparece, mas os instantes de criação não fecham (o pai anotado saiu, ou é mais novo
               // que o filho: PID reaproveitado ou relógio recuado). Não dá para concluir.
               Descends(99, 40) is null &&
               Descends(81, 80) is null &&
               Descends(10, 70) is null &&
               // Um elo que não pode ser datado é atravessado.
               Descends(10, 90) == true &&
               Descends(91, 90) == true;
    }

    private static async Task<bool> PassiveReconnectCountsOnlyConsecutiveFailuresTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-failures-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var created = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        // O mesmo processo escuta quase até o fim; o que falha é a resposta dele ao MCP.
        var listener = ListenerLookup.Found(1001, created);
        var script = new BackendScript();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script, _ => listener);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

            // O backend deixa de responder à verificação do gxhub_status e fica offline.
            async Task<bool> DropAsync()
            {
                script.FailNextList();
                await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
                return Flag(hub.StatusJson()["backends"]?[0]?["online"]) == false;
            }

            async Task<bool> RoundAsync(bool attempted, bool online)
            {
                var clientsBefore = script.Clients;
                var changed = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                return changed == attempted &&
                       script.Clients == clientsBefore + (attempted ? 1 : 0) &&
                       Flag(hub.StatusJson()["backends"]?[0]?["online"]) == online;
            }

            // Três quedas, cada uma com duas tentativas que falham e uma que conecta: seis falhas ao todo, nunca
            // três seguidas. A reconexão não pode desistir.
            var cycles = true;
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                cycles = cycles && await DropAsync();
                script.FailNextInitializes(2);
                cycles = cycles &&
                         await RoundAsync(attempted: true, online: false) &&
                         await RoundAsync(attempted: true, online: false) &&
                         await RoundAsync(attempted: true, online: true);
            }

            // Três falhas seguidas com o mesmo processo: aí ele é deixado em paz.
            var dropped = await DropAsync();
            script.FailNextInitializes(100);
            var limit = dropped &&
                        await RoundAsync(attempted: true, online: false) &&
                        await RoundAsync(attempted: true, online: false) &&
                        await RoundAsync(attempted: true, online: false) &&
                        await RoundAsync(attempted: false, online: false) &&
                        await RoundAsync(attempted: false, online: false);

            // Outro processo na porta: a contagem não o atinge.
            listener = ListenerLookup.Found(2002, created.AddMinutes(1));
            script.FailNextInitializes(0);
            var otherProcess = await RoundAsync(attempted: true, online: true);

            return cycles && limit && otherProcess;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> ProfileThatBreaksTheLoaderIsInvalidOnceTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-loader-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var script = new BackendScript();
        Func<Exception>? loaderFailure = null;
        Action? beforeLoad = null;
        Action? afterLoad = null;

        // O carregador de verdade, com ganchos: falhar no lugar de carregar, e mexer no arquivo logo antes ou logo
        // depois da carga (o que uma promoção faz quando reescreve o perfil no mesmo instante).
        LoadedHubConfig Loader(string path)
        {
            if (loaderFailure is not null)
            {
                throw loaderFailure();
            }

            beforeLoad?.Invoke();
            var loaded = HubConfigLoader.Load(path);
            afterLoad?.Invoke();
            return loaded;
        }

        string RevisionOnDisk() =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(profilePath)))[..12];

        // Valor nulo onde ele é obrigatório: o carregador recusa, em vez de falhar adiante com uma referência nula.
        bool LoaderRefuses(Action<JsonObject> breakIt)
        {
            var broken = TestProfile(root, port, "broken", withStartup: true);
            breakIt(broken);
            var brokenPath = Path.Combine(root, "broken-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(brokenPath, broken.ToJsonString());
            try
            {
                HubConfigLoader.Load(brokenPath);
                return false;
            }
            catch (ConfigurationException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var loaderRefusesNulls =
                LoaderRefuses(profile => profile["backends"] = null) &&
                LoaderRefuses(profile => profile["writePolicy"] = null) &&
                LoaderRefuses(profile => profile["writePolicy"]!["mode"] = null) &&
                LoaderRefuses(profile => profile["backends"] = new JsonArray { null }) &&
                LoaderRefuses(profile => profile["backends"]![0]!["readOnlyTools"] = null) &&
                LoaderRefuses(profile => profile["backends"]![0]!["startup"]!["environment"] = null);

            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            await using var hub = NewHub(profilePath, script, profileLoader: Loader);
            var notifications = 0;
            hub.ToolsChanged += () => Interlocked.Increment(ref notifications);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

            async Task<bool> FailsAsync(string code)
            {
                var changed = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
                var auto = hub.StatusJson()["profileAutoReload"];
                var error = Text(auto?["error"]);
                return !changed &&
                       Text(auto?["lastOutcome"]) == "failed" &&
                       error is not null &&
                       error.StartsWith(code + ":", StringComparison.Ordinal) &&
                       !error.Contains(root, StringComparison.OrdinalIgnoreCase) &&
                       MarkOf(hub) == "before";
            }

            // O perfil muda no disco e o carregador falha com uma exceção que ninguém previu: é um perfil que não
            // carrega, dito uma vez, sem aviso de mudança de tools a cada rodada.
            WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
            loaderFailure = () => new InvalidOperationException("falha sintética do carregador em " + root);
            bool saidOnce;
            using (var stderr = new StderrCapture())
            {
                var first = await FailsAsync("invalid-profile");
                var firstOutcomeAt = Text(hub.StatusJson()["profileAutoReload"]?["lastOutcomeAtUtc"]);
                var second = await FailsAsync("invalid-profile");
                var third = await FailsAsync("invalid-profile");
                saidOnce = first && second && third &&
                           firstOutcomeAt is not null &&
                           Text(hub.StatusJson()["profileAutoReload"]?["lastOutcomeAtUtc"]) == firstOutcomeAt &&
                           stderr.Count("invalid-profile") == 1 &&
                           stderr.Count("reconciliação passiva falhou") == 0 &&
                           Volatile.Read(ref notifications) == 0;
            }

            var statusCall = await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
            var stillServing = !IsError(statusCall) && script.Clients == 1;

            // Outro processo segura o arquivo sem compartilhar: é "ilegível agora", não "inválido".
            loaderFailure = null;
            bool inUseAtRead;
            using (new FileStream(profilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                inUseAtRead = await FailsAsync("profile-unreadable");
            }

            // O mesmo quando o arquivo só fica preso no instante da carga, depois de a revisão ter sido lida.
            loaderFailure = () => new InvalidOperationException("outra falha sintética");
            var backToInvalid = await FailsAsync("invalid-profile");
            loaderFailure = () => new IOException("O arquivo já está sendo usado por outro processo.");
            var inUseAtLoad = await FailsAsync("profile-unreadable");
            loaderFailure = null;

            // O arquivo é reescrito logo depois de o Hub o carregar: a revisão que o Hub passa a declarar é a do
            // conteúdo que ele carregou, não a do que está no disco, e a rodada seguinte carrega o que ficou.
            var afterRevision = RevisionOnDisk();
            afterLoad = () => WriteProfile(profilePath, TestProfile(root, port, "moved", withStartup: true));
            var loadedAfter = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            afterLoad = null;
            var between = hub.StatusJson();
            var boundToWhatWasLoaded =
                loadedAfter && MarkOf(hub) == "after" &&
                Text(between["profileRevision"]) == afterRevision &&
                Text(between["profileOnDiskRevision"]) == RevisionOnDisk() &&
                Flag(between["profileReloadPending"]) == true;
            var loadedMoved = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None) &&
                              MarkOf(hub) == "moved" &&
                              Flag(hub.StatusJson()["profileReloadPending"]) == false;

            bool LoadedIsWhatIsOnDisk(string mark)
            {
                var status = hub.StatusJson();
                return MarkOf(hub) == mark &&
                       Text(status["profileRevision"]) == RevisionOnDisk() &&
                       Flag(status["profileReloadPending"]) == false;
            }

            // O arquivo é reescrito entre a leitura da revisão e a carga: vale o que foi carregado, de uma vez.
            WriteProfile(profilePath, TestProfile(root, port, "passing", withStartup: true));
            beforeLoad = () => WriteProfile(profilePath, TestProfile(root, port, "landed", withStartup: true));
            var landed = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None) &&
                         LoadedIsWhatIsOnDisk("landed");

            // O arquivo muda e volta ao conteúdo carregado entre a leitura da revisão e a carga: nada é trocado.
            WriteProfile(profilePath, TestProfile(root, port, "passing", withStartup: true));
            var clientsBefore = script.Clients;
            var wentBack = !await hub.ReconcileAsync(fromTimer: true, CancellationToken.None) &&
                           script.Clients == clientsBefore &&
                           LoadedIsWhatIsOnDisk("landed");
            var automatic = Text(hub.StatusJson()["profileAutoReload"]?["lastOutcome"]) == "reloaded" &&
                            Text(hub.StatusJson()["lastProfileReloadTrigger"]) == "auto";

            // O mesmo no gxhub_refresh: a revisão é a do conteúdo carregado, e sem mudança de fato nada é trocado.
            WriteProfile(profilePath, TestProfile(root, port, "passing", withStartup: true));
            beforeLoad = () => WriteProfile(profilePath, TestProfile(root, port, "final", withStartup: true));
            await hub.RefreshFromProfileAsync(allowStartup: false, notify: false, CancellationToken.None);
            var manualBound = LoadedIsWhatIsOnDisk("final") &&
                              Text(hub.StatusJson()["lastProfileReloadTrigger"]) == "manual";
            var reloadedAt = Text(hub.StatusJson()["lastProfileReloadAtUtc"]);
            WriteProfile(profilePath, TestProfile(root, port, "passing", withStartup: true));
            await hub.RefreshFromProfileAsync(allowStartup: false, notify: false, CancellationToken.None);
            beforeLoad = null;
            var manualWentBack = LoadedIsWhatIsOnDisk("final") && reloadedAt is not null &&
                                 Text(hub.StatusJson()["lastProfileReloadAtUtc"]) == reloadedAt;

            return loaderRefusesNulls && saidOnce && stillServing && inUseAtRead && backToInvalid && inUseAtLoad &&
                   boundToWhatWasLoaded && loadedMoved && landed && wentBack && automatic &&
                   manualBound && manualWentBack;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> UnexpectedReconcileFailureIsSaidOnceTestAsync()
    {
        const string Said = "reconciliação passiva falhou";
        var root = NewTempRoot("GxMcpHub-roundfail-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var created = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        var broken = false;
        var script = new BackendScript();
        ListenerLookup Lookup(Uri _) => broken
            ? throw new InvalidOperationException("tabela de listeners indisponível (falha sintética)")
            : ListenerLookup.Found(1001, created);

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script, Lookup);
            var notifications = 0;
            hub.ToolsChanged += () => Interlocked.Increment(ref notifications);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);

            using var stderr = new StderrCapture();
            Task<bool> RoundAsync() => hub.ReconcileAsync(fromTimer: true, CancellationToken.None);

            // Toda rodada falha do mesmo jeito: dito uma vez, e nenhum aviso de mudança de tools.
            broken = true;
            var quiet = !await RoundAsync() && !await RoundAsync() && !await RoundAsync() &&
                        stderr.Count(Said) == 1 &&
                        Volatile.Read(ref notifications) == 0;

            // Passou: nada a dizer. Voltou a falhar: dito de novo, uma vez.
            broken = false;
            var healthy = !await RoundAsync() && stderr.Count(Said) == 1;
            broken = true;
            var again = !await RoundAsync() && !await RoundAsync() &&
                        stderr.Count(Said) == 2 &&
                        Volatile.Read(ref notifications) == 0;

            // O Hub segue atendendo com a conexão que tinha.
            broken = false;
            var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            return quiet && healthy && again && ResultText(call) == "ok:test_tool" && script.Clients == 1;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> RestoredProfileWithAnOldDateIsNotAppliedTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-restore-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var backupPath = Path.Combine(root, "rollback-profile.json");
        var port = ReserveFreePort();
        var now = DateTime.UtcNow;
        // Um Gateway de outro lançador, criado há 1 h a partir do perfil "pacote-novo", escrito há 3 h.
        var listener = ListenerLookup.Found(1001, now.AddHours(-1));
        var script = new BackendScript();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "pacote-novo", withStartup: true));
            File.SetLastWriteTimeUtc(profilePath, now.AddHours(-3));
            // O snapshot de rollback guarda o perfil anterior com a data de escrita dele, de 2 dias atrás.
            WriteProfile(backupPath, TestProfile(root, port, "pacote-antigo", withStartup: true));
            File.SetLastWriteTimeUtc(backupPath, now.AddDays(-2));

            await using var hub = NewHub(profilePath, script, _ => listener);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            // O Hub olha o arquivo de novo um pouco depois de ter sido criado: é desse último olhar que conta o
            // instante a partir do qual o conteúdo pode ter mudado.
            await Task.Delay(1100);
            var lastLook = DateTime.UtcNow;
            var before = hub.StatusJson()["backends"]?[0]?["gateway"];
            var appliedBefore = Flag(before?["profileApplied"]) == true &&
                                Text(before?["profileAppliedEvidence"]) == GatewayProfileEvidence.StartedAfterEdit;

            // Rollback agora: o conteúdo antigo volta por cópia, que preserva a data de escrita de 2 dias atrás.
            File.Copy(backupPath, profilePath, overwrite: true);
            var keptTheOldDate = File.GetLastWriteTimeUtc(profilePath) < now.AddDays(-1);
            var reloaded = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var after = hub.StatusJson()["backends"]?[0]?["gateway"];
            var editedAt = DateTime.Parse(
                Text(after?["profileModifiedUtc"]) ?? string.Empty,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);

            // O Gateway é anterior à chegada do conteúdo atual ao disco, o que a data do arquivo não mostra.
            return appliedBefore && keptTheOldDate && reloaded && MarkOf(hub) == "pacote-antigo" &&
                   Flag(after?["profileApplied"]) == false &&
                   Text(after?["profileAppliedEvidence"]) == GatewayProfileEvidence.StartedBeforeEdit &&
                   editedAt >= lastLook && editedAt <= DateTime.UtcNow;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> HandshakeIsRepeatedWhenTheListenerChangesMeanwhileTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-handshake-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var created = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        var oldProcess = ListenerLookup.Found(1001, created);
        var newProcess = ListenerLookup.Found(2002, created.AddMinutes(1));
        var lookups = 0;
        var script = new BackendScript();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            var config = HubConfigLoader.Load(profilePath).Config;

            // O processo atrás do endpoint é trocado durante o primeiro handshake: antes dele é o antigo, depois o novo.
            ListenerLookup Replaced(Uri _) => Interlocked.Increment(ref lookups) == 1 ? oldProcess : newProcess;
            await using var runtime = new BackendRuntime(config, config.Backends[0], _ => script.NewHandler(), Replaced);
            await runtime.InitializeAsync(allowStartup: false, CancellationToken.None);
            // O handshake foi refeito, e o que ficou anotado é o processo com que ele foi feito por inteiro.
            var repeated = runtime.Online && script.Clients == 2 && runtime.ConnectedListener == newProcess;

            // Se o processo não para de mudar, a conexão não é aceita.
            var changes = 0;
            ListenerLookup Restless(Uri _) => ListenerLookup.Found(3000 + Interlocked.Increment(ref changes), created);
            await using var restless = new BackendRuntime(config, config.Backends[0], _ => script.NewHandler(), Restless);
            await restless.InitializeAsync(allowStartup: false, CancellationToken.None);
            return repeated && !restless.Online && restless.Error is not null && script.Clients == 5 &&
                   restless.ConnectedListener is null;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> AutomaticReloadOnlyAppliesStartupChangesTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-startuponly-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var otherFolder = Path.Combine(root, "outra-pasta");
        var port = ReserveFreePort();
        var script = new BackendScript();

        HubConfig Loaded(Action<JsonObject>? edit = null)
        {
            var profile = TestProfile(root, port, "base", withStartup: true);
            edit?.Invoke(profile);
            var path = Path.Combine(root, "shape-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, profile.ToJsonString());
            return HubConfigLoader.Load(path).Config;
        }

        try
        {
            Directory.CreateDirectory(otherFolder);
            var otherCommand = Path.Combine(otherFolder, "outro-comando.txt");
            File.WriteAllText(otherCommand, "sintético");
            var reference = Loaded();
            bool OnlyStartup(Action<JsonObject> edit) => ProfileShape.DiffersOnlyInStartup(reference, Loaded(edit));

            // O que uma promoção reescreve: tudo dentro de backends[].startup.
            var startupChanges =
                OnlyStartup(_ => { }) &&
                OnlyStartup(profile => profile["backends"]![0]!["startup"]!["arguments"] = "--outro") &&
                OnlyStartup(profile => profile["backends"]![0]!["startup"]!["command"] = otherCommand) &&
                OnlyStartup(profile => profile["backends"]![0]!["startup"]!["workingDirectory"] = otherFolder) &&
                OnlyStartup(profile => profile["backends"]![0]!["startup"]!["startupTimeoutSeconds"] = 45) &&
                OnlyStartup(profile => profile["backends"]![0]!["startup"]!["environment"]!["GX_MCP_PORT"] = "5015") &&
                OnlyStartup(profile => profile["backends"]![0]!.AsObject().Remove("startup"));

            // Qualquer outra diferença espera o gxhub_refresh.
            var otherChanges =
                !OnlyStartup(profile => profile["backends"]![0]!["endpoint"] = "http://127.0.0.1:1/mcp") &&
                !OnlyStartup(profile => profile["backends"]![0]!["toolPrefix"] = "outro_") &&
                !OnlyStartup(profile => profile["backends"]![0]!["readOnlyTools"] = new JsonArray("genexus_edit")) &&
                !OnlyStartup(profile => profile["backends"]![0]!["useRemoteReadOnlyAnnotations"] = false) &&
                !OnlyStartup(profile => profile["backends"]![0]!["displayName"] = "outro nome") &&
                !OnlyStartup(profile => profile["backends"]![0]!["enabled"] = false) &&
                !OnlyStartup(profile => profile["backends"]![0]!["kind"] = "dynamic") &&
                !OnlyStartup(profile => profile["backends"]![0]!["id"] = "outro") &&
                !OnlyStartup(profile => profile["backends"]![0]!["protocolVersion"] = "2024-01-01") &&
                !OnlyStartup(profile => profile["backends"]![0]!["bearerTokenEnvVar"] = "GXHUB_SELFTEST_TOKEN") &&
                !OnlyStartup(profile => profile["writePolicy"]!["hideBlockedTools"] = false) &&
                !OnlyStartup(profile => profile["writePolicy"] = new JsonObject
                {
                    ["mode"] = "open-writer",
                    ["writerBackend"] = "native",
                    ["hideBlockedTools"] = true
                }) &&
                !OnlyStartup(profile => profile["connectTimeoutSeconds"] = 9) &&
                !OnlyStartup(profile => profile["callTimeoutSeconds"] = 99) &&
                !OnlyStartup(profile => profile["profileName"] = "outro-perfil") &&
                !OnlyStartup(profile => profile["workspaceRoot"] = otherFolder) &&
                !OnlyStartup(profile => profile["knowledgeBasePath"] = otherFolder) &&
                !OnlyStartup(profile => profile["backends"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = "aux",
                    ["enabled"] = false,
                    ["toolPrefix"] = "aux_",
                    ["protocolVersion"] = "2025-11-25",
                    ["endpoint"] = "http://127.0.0.1:2/mcp"
                }));

            // A política de escrita, um campo de cada vez (dois backends, para o escritor poder mudar sozinho).
            HubConfig Policy(string mode, string writer) => Loaded(profile =>
            {
                profile["writePolicy"] = new JsonObject
                {
                    ["mode"] = mode,
                    ["writerBackend"] = writer,
                    ["hideBlockedTools"] = true
                };
                profile["backends"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = "aux",
                    ["enabled"] = true,
                    ["toolPrefix"] = "aux_",
                    ["protocolVersion"] = "2025-11-25",
                    ["endpoint"] = "http://127.0.0.1:2/mcp"
                });
            });
            otherChanges = otherChanges &&
                ProfileShape.DiffersOnlyInStartup(Policy("open-writer", "native"), Policy("open-writer", "native")) &&
                !ProfileShape.DiffersOnlyInStartup(Policy("single-writer", "native"), Policy("open-writer", "native")) &&
                !ProfileShape.DiffersOnlyInStartup(Policy("open-writer", "native"), Policy("open-writer", "aux"));

            // Num perfil read-only, alargar readOnlyTools no disco não passa a valer sozinho.
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            await using var hub = NewHub(profilePath, script);
            var notifications = 0;
            hub.ToolsChanged += () => Interlocked.Increment(ref notifications);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var widened = TestProfile(root, port, "after", withStartup: true);
            widened["backends"]![0]!["readOnlyTools"] = new JsonArray("genexus_edit");
            WriteProfile(profilePath, widened);

            var changed = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var again = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            var waiting = hub.StatusJson();
            var deferred =
                !changed && !again && Volatile.Read(ref notifications) == 0 &&
                Text(waiting["profileAutoReload"]?["lastOutcome"]) == "deferred" &&
                Text(waiting["profileAutoReload"]?["deferredReason"]) == HubService.DeferredToManualRefresh &&
                waiting["profileAutoReload"]?["error"] is null &&
                Flag(waiting["profileReloadPending"]) == true &&
                waiting["lastProfileReloadTrigger"] is null &&
                MarkOf(hub) == "before" &&
                hub.Config.Backends[0].ReadOnlyTools.Count == 0 &&
                script.Clients == 1;

            // O gxhub_refresh é o ato deliberado que aplica a mudança.
            await hub.RefreshFromProfileAsync(allowStartup: false, notify: false, CancellationToken.None);
            var applied = hub.StatusJson();
            return startupChanges && otherChanges && deferred &&
                   hub.Config.Backends[0].ReadOnlyTools.Count == 1 &&
                   MarkOf(hub) == "after" &&
                   Text(applied["lastProfileReloadTrigger"]) == "manual" &&
                   Text(applied["profileAutoReload"]?["lastOutcome"]) == "none" &&
                   applied["profileAutoReload"]?["deferredReason"] is null &&
                   Flag(applied["profileReloadPending"]) == false;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // 0.3.0: presença por Hub. O Gateway compartilhado não morre com o Hub que o iniciou.
    // ------------------------------------------------------------------------------------------------------------

    private static int? OtherHubs(HubService hub) =>
        hub.StatusJson()["backends"]?[0]?["gateway"]?["otherHubsConnected"]?.GetValue<int>();

    private static string[] PresenceFiles(HubService hub, Uri endpoint)
    {
        var folder = hub.Presence.FolderFor(endpoint);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*" + HubPresence.Extension) : Array.Empty<string>();
    }

    private static async Task<bool> OwnerAloneStopsItsGatewayOnTheWayOutTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-owneralone-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            using var self = Process.GetCurrentProcess();
            // O nome do arquivo da trava de início deste endpoint, calculado à parte: SHA-256 do endereço.
            var leaseHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));
            // Uma execução anterior pode ter usado esta mesma porta: o arquivo da trava tem de nascer nesta.
            var leaseFile = Path.Combine(Path.GetTempPath(), "GxMcpHub", "backend-startup", leaseHash + ".lock");
            if (File.Exists(leaseFile))
            {
                File.Delete(leaseFile);
            }

            bool connected;
            string presenceFile;
            string presenceFolder;
            using var stderr = new StderrCapture();
            await using (var hub = NewHub(profilePath, script))
            {
                await hub.StartAsync(CancellationToken.None);
                var status = hub.StatusJson();
                var backend = status["backends"]?[0];
                var gateway = backend?["gateway"];
                var pid = backend?["processId"]?.GetValue<int>() ?? 0;
                if (pid == 0)
                {
                    return false;
                }
                children.Add(pid);
                presenceFile = hub.Presence.FileFor(endpoint);
                presenceFolder = hub.Presence.FolderFor(endpoint);
                connected =
                    Text(status["hubVersion"]) == HubInfo.Version &&
                    Flag(gateway?["startedByThisHub"]) == true &&
                    gateway!.AsObject().ContainsKey("otherHubsConnected") &&
                    OtherHubs(hub) == 0 &&
                    // A presença é um arquivo deste Hub na pasta do endpoint, fora da pasta das travas de início.
                    hub.Presence.IsHeld(endpoint) &&
                    File.Exists(presenceFile) &&
                    // <TEMP>\GxMcpHub\backend-presence\<hash do endpoint>\<pid>-<criação>.presence: fora da pasta
                    // backend-startup, cujos *.lock quem promove acompanha, e com o mesmo hash da trava de início.
                    Path.GetFileName(presenceFile).StartsWith(
                        $"{Environment.ProcessId}-{self.StartTime.ToUniversalTime().Ticks}",
                        StringComparison.Ordinal) &&
                    presenceFile.EndsWith(".presence", StringComparison.Ordinal) &&
                    presenceFolder == Path.Combine(Path.GetTempPath(), "GxMcpHub", "backend-presence", leaseHash) &&
                    File.Exists(leaseFile) &&
                    Directory.GetFiles(Path.Combine(Path.GetTempPath(), "GxMcpHub", "backend-startup"), "*.presence").Length == 0;
            }

            // Ninguém mais conectado: o Gateway é encerrado como antes, e não sobra presença.
            return connected &&
                   children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10))) &&
                   !File.Exists(presenceFile) &&
                   stderr.Count("Gateway mantido em execução") == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> SharedGatewaySurvivesItsOwnerTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-shared-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        HubService? owner = null;
        HubService? other = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            var startsBefore = ProcessSupervisor.TotalStarts;
            owner = NewHub(profilePath, script);
            await owner.StartAsync(CancellationToken.None);
            var pid = owner.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (pid == 0)
            {
                return false;
            }
            children.Add(pid);

            // Outro Hub conecta ao mesmo endpoint: não inicia nada, só registra a presença dele.
            other = NewHub(profilePath, script);
            await other.StartAsync(CancellationToken.None);
            var otherBackend = other.StatusJson()["backends"]?[0];
            var both =
                ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                Flag(otherBackend?["online"]) == true &&
                Flag(otherBackend?["startedByHub"]) == false &&
                Flag(otherBackend?["gateway"]?["startedByThisHub"]) == false &&
                OtherHubs(owner) == 1 &&
                OtherHubs(other) == 1 &&
                PresenceFiles(owner, endpoint).Length == 2;

            // O Hub que iniciou o Gateway sai: não o encerra, e diz isso.
            bool said;
            using (var stderr = new StderrCapture())
            {
                await owner.DisposeAsync();
                owner = null;
                said = stderr.Count("Gateway mantido em execução") == 1 &&
                       stderr.Count("1 outro(s) Hub(s) conectado(s)") == 1;
            }

            var kept = IsAlive(pid) && ListenerInspector.Find(endpoint).State == ListenerLookupState.Found;
            var call = await other.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var served = ResultText(call) == "ok:test_tool" &&
                         OtherHubs(other) == 0 &&
                         PresenceFiles(other, endpoint).Length == 1 &&
                         ProcessSupervisor.TotalStarts == startsBefore + 1;

            // O segundo Hub sai: o Gateway não é dele, e fica.
            await other.DisposeAsync();
            other = null;
            var orphan = IsAlive(pid) && ListenerInspector.Find(endpoint).State == ListenerLookupState.Found;

            return both && said && kept && served && orphan;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }
            if (other is not null)
            {
                await other.DisposeAsync();
            }
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static string[] ExitLogLines(string root)
    {
        var path = Path.Combine(root, "GxMcpHub", "runtime", "hub-exit.log");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).ToArray()
            : Array.Empty<string>();
    }

    // GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE ligado: o Hub que sai sozinho deixa o Gateway que iniciou em execução e diz isso
    // (kept, 0 outros Hubs, sem presença). O Hub seguinte o encontra, não inicia nada e é atendido; ao sair não o encerra.
    private static async Task<bool> KeepGatewayWhenAloneLeavesTheGatewayRunningTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-keepalone-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        HubService? owner = null;
        HubService? next = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            var startsBefore = ProcessSupervisor.TotalStarts;
            // A chave vem do ambiente do Hub, lida quando ele é criado.
            var previous = Environment.GetEnvironmentVariable(HubService.KeepGatewayWhenAloneVariable);
            Environment.SetEnvironmentVariable(HubService.KeepGatewayWhenAloneVariable, " Yes ");
            try
            {
                owner = NewHub(profilePath, script, keepGatewayWhenAlone: null);
            }
            finally
            {
                Environment.SetEnvironmentVariable(HubService.KeepGatewayWhenAloneVariable, previous);
            }

            await owner.StartAsync(CancellationToken.None);
            var status = owner.StatusJson();
            var pid = status["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (pid == 0)
            {
                return false;
            }

            children.Add(pid);
            var presenceFile = owner.Presence.FileFor(endpoint);
            var announced = Flag(status["keepGatewayWhenAlone"]) == true &&
                            Flag(status["backends"]?[0]?["gateway"]?["startedByThisHub"]) == true &&
                            OtherHubs(owner) == 0 &&
                            File.Exists(presenceFile);

            // Sozinho e com a chave ligada: o Gateway fica, e o Hub diz por quê.
            bool said;
            using (var stderr = new StderrCapture())
            {
                await owner.DisposeAsync();
                owner = null;
                said = stderr.Count("Gateway mantido em execução") == 1 &&
                       stderr.Count("GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE") == 1;
            }

            var kept = IsAlive(pid) &&
                       ListenerInspector.Find(endpoint).State == ListenerLookupState.Found &&
                       !File.Exists(presenceFile);
            var lines = ExitLogLines(root);
            var recorded = lines.Length == 1 &&
                           lines[0].Contains(" backend=native decision=kept ", StringComparison.Ordinal) &&
                           lines[0].Contains($" gateway={pid} others=0 ", StringComparison.Ordinal) &&
                           lines[0].EndsWith(" mode=hub-tree", StringComparison.Ordinal);

            // O Hub seguinte (chave desligada) encontra o Gateway, não inicia nada e é atendido.
            next = NewHub(profilePath, script, keepGatewayWhenAlone: false);
            await next.StartAsync(CancellationToken.None);
            var nextBackend = next.StatusJson()["backends"]?[0];
            var found = ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                        Flag(nextBackend?["online"]) == true &&
                        Flag(nextBackend?["startedByHub"]) == false &&
                        Flag(nextBackend?["gateway"]?["startedByThisHub"]) == false &&
                        OtherHubs(next) == 0;
            var call = await next.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var served = ResultText(call) == "ok:test_tool" && ProcessSupervisor.TotalStarts == startsBefore + 1;

            // Ele não é o dono: sai sem encerrar o Gateway.
            await next.DisposeAsync();
            next = null;
            var orphan = IsAlive(pid) && ListenerInspector.Find(endpoint).State == ListenerLookupState.Found;
            lines = ExitLogLines(root);
            var notOwner = lines.Length == 2 &&
                           lines[1].Contains(" backend=native decision=not-owner gateway=-", StringComparison.Ordinal);

            return announced && said && kept && recorded && found && served && orphan && notOwner;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            if (next is not null)
            {
                await next.DisposeAsync();
            }

            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    // O modo só vale onde o Hub já deixava o Gateway: desligado por padrão, perde para GXMCP_HUB_KEEP_SHARED_GATEWAY=0,
    // não segura um processo que não é o Gateway de ninguém e, com outro Hub conectado, é o mesmo que sempre foi.
    private static async Task<bool> KeepGatewayWhenAloneYieldsToTheOtherCasesTestAsync()
    {
        async Task<bool> ScenarioAsync(bool wrapper, Func<string, string, Uri, BackendScript, List<int>, Task<bool>> run)
        {
            var root = NewTempRoot("GxMcpHub-keepalone-yield-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var children = new List<int>();
            try
            {
                WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true, wrapper: wrapper));
                return await run(root, profilePath, endpoint, script, children);
            }
            catch
            {
                return false;
            }
            finally
            {
                KillLeftovers(children);
                DeleteQuietly(root);
            }
        }

        // Só 1, true, on e yes ligam; nada, vazio e qualquer outro valor desligam (o inverso da chave irmã).
        var parsing = new string?[] { null, string.Empty, " ", "0", "off", "false", "no", "maybe", "2", "enable" }
                          .All(value => !HubService.ParseOptIn(value)) &&
                      new[] { "1", "true", "TRUE", " On ", "yes", "Yes" }.All(HubService.ParseOptIn) &&
                      HubService.ParseAutoReconcile(null) && !HubService.ParseAutoReconcile("off");

        // Sem a chave no ambiente: o Hub sozinho encerra o Gateway, como na 0.4.0.
        var offByDefault = await ScenarioAsync(false, async (root, profilePath, endpoint, script, children) =>
        {
            var previous = Environment.GetEnvironmentVariable(HubService.KeepGatewayWhenAloneVariable);
            Environment.SetEnvironmentVariable(HubService.KeepGatewayWhenAloneVariable, null);
            HubService hub;
            try
            {
                hub = NewHub(profilePath, script, keepGatewayWhenAlone: null);
            }
            finally
            {
                Environment.SetEnvironmentVariable(HubService.KeepGatewayWhenAloneVariable, previous);
            }

            int pid;
            bool reported;
            await using (hub)
            {
                await hub.StartAsync(CancellationToken.None);
                var status = hub.StatusJson();
                pid = status["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                reported = Flag(status["keepGatewayWhenAlone"]) == false;
                if (pid != 0)
                {
                    children.Add(pid);
                }
            }

            var lines = ExitLogLines(Path.GetDirectoryName(profilePath)!);
            return pid != 0 && reported && WaitForExit(pid, TimeSpan.FromSeconds(10)) &&
                   lines.Length == 1 && lines[0].Contains(" decision=stopped ", StringComparison.Ordinal) &&
                   !lines[0].Contains("others=", StringComparison.Ordinal);
        });

        // GXMCP_HUB_KEEP_SHARED_GATEWAY=0 é a chave de retorno: o Gateway é sempre encerrado, e o status não promete o contrário.
        var escapeWins = await ScenarioAsync(false, async (root, profilePath, endpoint, script, children) =>
        {
            int pid;
            bool reported;
            await using (var hub = NewHub(profilePath, script, keepSharedGateway: false, keepGatewayWhenAlone: true))
            {
                await hub.StartAsync(CancellationToken.None);
                var status = hub.StatusJson();
                pid = status["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                reported = Flag(status["keepGatewayWhenAlone"]) == false;
                if (pid != 0)
                {
                    children.Add(pid);
                }
            }

            var lines = ExitLogLines(Path.GetDirectoryName(profilePath)!);
            return pid != 0 && reported && WaitForExit(pid, TimeSpan.FromSeconds(10)) &&
                   lines.Length == 1 && lines[0].Contains(" decision=stopped ", StringComparison.Ordinal);
        });

        // Um processo do Hub que não é o Gateway de ninguém (ninguém escuta) é encerrado mesmo com o modo ligado.
        var servesNobody = await ScenarioAsync(true, async (root, profilePath, endpoint, script, children) =>
        {
            HubService? hub = null;
            try
            {
                hub = NewHub(profilePath, script, keepGatewayWhenAlone: true);
                await hub.StartAsync(CancellationToken.None);
                var backend = hub.StatusJson()["backends"]?[0];
                var wrapperPid = backend?["processId"]?.GetValue<int>() ?? 0;
                var listenerPid = backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
                if (wrapperPid == 0 || listenerPid == 0 || listenerPid == wrapperPid)
                {
                    return false;
                }

                children.Add(wrapperPid);
                children.Add(listenerPid);
                KillProcess(listenerPid);
                if (!await WaitUntilAsync(
                        () => ListenerInspector.Find(endpoint).State == ListenerLookupState.NotFound,
                        TimeSpan.FromSeconds(10)))
                {
                    return false;
                }

                await hub.DisposeAsync();
                hub = null;
                var lines = ExitLogLines(Path.GetDirectoryName(profilePath)!);
                return lines.Length == 1 &&
                       lines[0].Contains(" decision=stopped ", StringComparison.Ordinal) &&
                       lines[0].Contains($" gateway={wrapperPid} ", StringComparison.Ordinal) &&
                       WaitForExit(wrapperPid, TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (hub is not null)
                {
                    await hub.DisposeAsync();
                }
            }
        });

        // Com outro Hub conectado o resultado é o de sempre: o Gateway fica (kept, 1 outro) e o outro Hub segue atendido.
        var withAnother = await ScenarioAsync(false, async (root, profilePath, endpoint, script, children) =>
        {
            HubService? owner = null;
            HubService? other = null;
            try
            {
                owner = NewHub(profilePath, script, keepGatewayWhenAlone: true);
                await owner.StartAsync(CancellationToken.None);
                var pid = owner.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (pid == 0)
                {
                    return false;
                }

                children.Add(pid);
                other = NewHub(profilePath, script, keepGatewayWhenAlone: false);
                await other.StartAsync(CancellationToken.None);
                var both = OtherHubs(owner) == 1 && OtherHubs(other) == 1;
                await owner.DisposeAsync();
                owner = null;
                var lines = ExitLogLines(Path.GetDirectoryName(profilePath)!);
                var call = await other.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                return both && IsAlive(pid) &&
                       lines.Length == 1 &&
                       lines[0].Contains($" decision=kept gateway={pid} others=1 ", StringComparison.Ordinal) &&
                       ResultText(call) == "ok:test_tool";
            }
            finally
            {
                if (owner is not null)
                {
                    await owner.DisposeAsync();
                }

                if (other is not null)
                {
                    await other.DisposeAsync();
                }
            }
        });

        // O mesmo caso do teste do processo que não serve ninguém (aí com outro Hub conectado), agora com o modo ligado: o
        // guarda que vem antes da contagem dos outros Hubs continua valendo, com ou sem outro processo na porta.
        var servesNobodyWithAnother =
            await OwnProcessThatServesNobodyScenarioAsync(anotherProcessTookThePort: false, keepGatewayWhenAlone: true) &&
            await OwnProcessThatServesNobodyScenarioAsync(anotherProcessTookThePort: true, keepGatewayWhenAlone: true);

        return parsing && offByDefault && escapeWins && servesNobody && withAnother && servesNobodyWithAnother;
    }

    // O modo só deixa o Gateway que o Hub viu funcionar e cujo listener é, sem dúvida, o dele: sem handshake, com o listener
    // que não dá para identificar ou com outro processo na porta, o Gateway é encerrado como na 0.4.0; numa rota de wrapper
    // com o Gateway vivo, o wrapper e o Gateway ficam (kept com o PID do wrapper, 0 outros).
    private static async Task<bool> KeepGatewayWhenAloneNeedsAGatewayThatServedTestAsync()
    {
        async Task<bool> ScenarioAsync(
            bool wrapper,
            int startupTimeoutSeconds,
            Func<string, string, Uri, BackendScript, List<int>, Task<bool>> run)
        {
            var root = NewTempRoot("GxMcpHub-keepalone-served-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var children = new List<int>();
            try
            {
                WriteProfile(
                    profilePath,
                    TestProfile(
                        root,
                        port,
                        "native",
                        withStartup: true,
                        wrapper: wrapper,
                        startupTimeoutSeconds: startupTimeoutSeconds,
                        connectTimeoutSeconds: 1));
                return await run(root, profilePath, endpoint, script, children);
            }
            catch
            {
                return false;
            }
            finally
            {
                KillLeftovers(children);
                DeleteQuietly(root);
            }
        }

        // O Gateway abriu a porta mas o handshake nunca deu certo: o Hub está offline e o processo ainda é dele.
        var neverAnswered = await ScenarioAsync(false, 20, async (root, profilePath, endpoint, script, children) =>
        {
            script.FailNextInitializes(1000);
            int pid;
            await using (var hub = NewHub(profilePath, script, keepGatewayWhenAlone: true))
            {
                await hub.StartAsync(CancellationToken.None);
                var backend = hub.StatusJson()["backends"]?[0];
                pid = backend?["processId"]?.GetValue<int>() ?? 0;
                if (pid != 0)
                {
                    children.Add(pid);
                }

                if (pid == 0 ||
                    Flag(backend?["online"]) != false ||
                    ListenerInspector.Find(endpoint).State != ListenerLookupState.Found)
                {
                    return false;
                }
            }

            var lines = ExitLogLines(Path.GetDirectoryName(profilePath)!);
            return WaitForExit(pid, TimeSpan.FromSeconds(10)) &&
                   lines.Length == 1 &&
                   lines[0].Contains($" decision=stopped gateway={pid} ", StringComparison.Ordinal) &&
                   !lines[0].Contains("others=", StringComparison.Ordinal);
        });

        // Conectou e atendeu, mas na saída o sistema não deixa dizer quem escuta: não há identificação, o Gateway é encerrado.
        var cannotInspect = await ScenarioAsync(false, 20, async (root, profilePath, endpoint, script, children) =>
        {
            var inspectable = true;
            ListenerLookup Lookup(Uri uri) => inspectable ? ListenerInspector.Find(uri) : ListenerLookup.NotInspectable();
            int pid;
            await using (var hub = NewHub(profilePath, script, listenerLookup: Lookup, keepGatewayWhenAlone: true))
            {
                await hub.StartAsync(CancellationToken.None);
                var backend = hub.StatusJson()["backends"]?[0];
                pid = backend?["processId"]?.GetValue<int>() ?? 0;
                if (pid != 0)
                {
                    children.Add(pid);
                }

                if (pid == 0 || Flag(backend?["online"]) != true)
                {
                    return false;
                }

                inspectable = false;
            }

            var lines = ExitLogLines(Path.GetDirectoryName(profilePath)!);
            return WaitForExit(pid, TimeSpan.FromSeconds(10)) &&
                   lines.Length == 1 &&
                   lines[0].Contains(" decision=stopped ", StringComparison.Ordinal);
        });

        // Rota de wrapper com o Gateway (neto) vivo: o wrapper e o Gateway ficam, e o registro traz o PID do wrapper.
        var wrapperKept = await ScenarioAsync(true, 20, async (root, profilePath, endpoint, script, children) =>
        {
            int wrapperPid;
            int listenerPid;
            await using (var hub = NewHub(profilePath, script, keepGatewayWhenAlone: true))
            {
                await hub.StartAsync(CancellationToken.None);
                var backend = hub.StatusJson()["backends"]?[0];
                wrapperPid = backend?["processId"]?.GetValue<int>() ?? 0;
                listenerPid = backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
                if (wrapperPid == 0 || listenerPid == 0 || listenerPid == wrapperPid || Flag(backend?["online"]) != true)
                {
                    return false;
                }

                children.Add(wrapperPid);
                children.Add(listenerPid);
            }

            var lines = ExitLogLines(Path.GetDirectoryName(profilePath)!);
            return IsAlive(wrapperPid) &&
                   IsAlive(listenerPid) &&
                   ListenerInspector.Find(endpoint).State == ListenerLookupState.Found &&
                   lines.Length == 1 &&
                   lines[0].Contains($" decision=kept gateway={wrapperPid} others=0 ", StringComparison.Ordinal);
        });

        // O Gateway caiu e OUTRO processo tomou a porta: o wrapper deste Hub não é o Gateway de ninguém e é encerrado.
        var foreignPort = await ScenarioAsync(true, 20, async (root, profilePath, endpoint, script, children) =>
        {
            TcpListener? foreign = null;
            HubService? hub = null;
            try
            {
                hub = NewHub(profilePath, script, keepGatewayWhenAlone: true);
                await hub.StartAsync(CancellationToken.None);
                var backend = hub.StatusJson()["backends"]?[0];
                var wrapperPid = backend?["processId"]?.GetValue<int>() ?? 0;
                var listenerPid = backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
                if (wrapperPid == 0 || listenerPid == 0 || listenerPid == wrapperPid)
                {
                    return false;
                }

                children.Add(wrapperPid);
                children.Add(listenerPid);
                KillProcess(listenerPid);
                if (!await WaitUntilAsync(
                        () => ListenerInspector.Find(endpoint).State == ListenerLookupState.NotFound,
                        TimeSpan.FromSeconds(10)))
                {
                    return false;
                }

                foreign = new TcpListener(IPAddress.Loopback, endpoint.Port);
                foreign.Start();
                await hub.DisposeAsync();
                hub = null;
                var lines = ExitLogLines(Path.GetDirectoryName(profilePath)!);
                return lines.Length == 1 &&
                       lines[0].Contains($" decision=stopped gateway={wrapperPid} ", StringComparison.Ordinal) &&
                       WaitForExit(wrapperPid, TimeSpan.FromSeconds(10));
            }
            finally
            {
                foreign?.Stop();
                if (hub is not null)
                {
                    await hub.DisposeAsync();
                }
            }
        });

        return neverAnswered && cannotInspect && wrapperKept && foreignPort;
    }

    private static async Task<bool> DeadHubPresenceDoesNotCountTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-stalepresence-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            bool counted;
            string stale;
            await using (var hub = NewHub(profilePath, script))
            {
                await hub.StartAsync(CancellationToken.None);
                var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (pid == 0)
                {
                    return false;
                }
                children.Add(pid);

                // O arquivo de um Hub que morreu sem o sistema apagá-lo (uma queda da máquina): existe e ninguém o
                // segura. Ele abre, então não conta, e quem olha o apaga.
                stale = Path.Combine(hub.Presence.FolderFor(endpoint), "4242-638000000000000000" + HubPresence.Extension);
                File.WriteAllText(stale, string.Empty);
                counted = OtherHubs(hub) == 0 && !File.Exists(stale);

                // De novo na hora de sair: o Hub decide olhando as presenças, e a que abre não o impede de encerrar.
                File.WriteAllText(stale, string.Empty);
            }

            return counted &&
                   children.All(pid => WaitForExit(pid, TimeSpan.FromSeconds(10))) &&
                   !File.Exists(stale);
        }
        catch
        {
            return false;
        }
        finally
        {
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    // Outro processo que registra presença num endpoint, como um Hub conectado. Devolve o processo e o PID do filho
    // que ele iniciou (0 quando não pedido).
    private static (Process Helper, int ChildId)? StartPresenceHelper(int port, int childPort)
    {
        var (command, prefix) = SelfTestChildCommand();
        var helper = Process.Start(new ProcessStartInfo
        {
            FileName = command,
            Arguments = $"{prefix}{SelfTestListener.PresenceSwitch} {port} 120 {childPort}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (helper is null)
        {
            return null;
        }

        // Nobody reads it: the helper has nothing to say there, and the pipe must not fill.
        helper.ErrorDataReceived += (_, _) => { };
        helper.BeginErrorReadLine();

        var line = helper.StandardOutput.ReadLineAsync();
        if (line.Wait(TimeSpan.FromSeconds(30)) &&
            line.Result is string text &&
            text.StartsWith(SelfTestListener.PresenceReady, StringComparison.Ordinal) &&
            int.TryParse(text[SelfTestListener.PresenceReady.Length..], out var childId))
        {
            return (helper, childId);
        }

        try
        {
            helper.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        helper.Dispose();
        return null;
    }

    private static async Task<bool> KilledHubLeavesNoPresenceTestAsync()
    {
        var port = ReserveFreePort();
        var childPort = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var childEndpoint = new Uri($"http://127.0.0.1:{childPort}/mcp");
        var children = new List<int>();
        Process? helper = null;

        try
        {
            using var observer = new HubPresence();
            // O outro processo registra a presença e SÓ DEPOIS inicia um filho do jeito que o Hub inicia um backend.
            var started = StartPresenceHelper(port, childPort);
            if (started is null)
            {
                return false;
            }
            helper = started.Value.Helper;
            var childId = started.Value.ChildId;
            children.Add(helper.Id);
            if (childId != 0)
            {
                children.Add(childId);
            }

            var seen = childId != 0 &&
                       observer.CountOthers(endpoint) == 1 &&
                       await WaitUntilAsync(
                           () => ListenerInspector.Find(childEndpoint).State == ListenerLookupState.Found,
                           TimeSpan.FromSeconds(20));

            // Morto à força, sem rodar saída nenhuma, e com o filho dele ainda vivo.
            helper.Kill();
            helper.WaitForExit(10000);
            var folder = observer.FolderFor(endpoint);
            var released = await WaitUntilAsync(() => observer.CountOthers(endpoint) == 0, TimeSpan.FromSeconds(10));
            var nothingLeft = !Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0;
            // O filho não recebeu o arquivo de presença: se tivesse recebido, a presença seguiria presa com ele vivo.
            var childOutlivedIt = IsAlive(childId);

            return seen && released && nothingLeft && childOutlivedIt;
        }
        catch
        {
            return false;
        }
        finally
        {
            helper?.Dispose();
            KillLeftovers(children);
        }
    }

    private static async Task<bool> NoAnswerLeavesTheGatewayRunningTestAsync()
    {
        // Sem a trava de início no prazo o Hub não consegue perguntar: sai sem encerrar, e não demora.
        async Task<bool> BusyLeaseAsync()
        {
            var root = NewTempRoot("GxMcpHub-busylease-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var children = new List<int>();
            HubService? hub = null;
            try
            {
                WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
                hub = NewHub(profilePath, script);
                hub.ExitLeaseWait = TimeSpan.FromMilliseconds(400);
                await hub.StartAsync(CancellationToken.None);
                var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (pid == 0)
                {
                    return false;
                }
                children.Add(pid);

                bool ok;
                using (var stderr = new StderrCapture())
                using (await BackendStartupLease.AcquireAsync(new[] { endpoint }, TimeSpan.FromSeconds(10), CancellationToken.None))
                {
                    var watch = Stopwatch.StartNew();
                    await hub.DisposeAsync();
                    hub = null;
                    watch.Stop();
                    ok = watch.Elapsed >= TimeSpan.FromMilliseconds(300) &&
                         watch.Elapsed < TimeSpan.FromSeconds(8) &&
                         IsAlive(pid) &&
                         stderr.Count("Gateway mantido em execução") == 1 &&
                         stderr.Count("a trava de início do endpoint não foi obtida") == 1;
                }

                return ok && IsAlive(pid);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hub is not null)
                {
                    await hub.DisposeAsync();
                }
                KillLeftovers(children);
                DeleteQuietly(root);
            }
        }

        // Uma presença que não pode ser examinada: pode ser de um Hub vivo. Também não encerra.
        async Task<bool> UnreadablePresenceAsync()
        {
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            var root = NewTempRoot("GxMcpHub-unreadable-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var children = new List<int>();
            HubService? hub = null;
            FileInfo? unreadable = null;
            System.Security.AccessControl.FileSystemAccessRule? deny = null;
            try
            {
                WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
                hub = NewHub(profilePath, script);
                await hub.StartAsync(CancellationToken.None);
                var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
                if (pid == 0)
                {
                    return false;
                }
                children.Add(pid);

                unreadable = new FileInfo(
                    Path.Combine(hub.Presence.FolderFor(endpoint), "4343-638000000000000000" + HubPresence.Extension));
                File.WriteAllText(unreadable.FullName, string.Empty);
                var user = System.Security.Principal.WindowsIdentity.GetCurrent().User
                           ?? throw new InvalidOperationException("Usuário atual sem identificador.");
                deny = new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.ReadData,
                    System.Security.AccessControl.AccessControlType.Deny);
                var security = unreadable.GetAccessControl();
                security.AddAccessRule(deny);
                unreadable.SetAccessControl(security);

                // O status diz que não sabe, em vez de dizer zero.
                var gateway = hub.StatusJson()["backends"]?[0]?["gateway"];
                var unknown = gateway is not null &&
                              gateway.AsObject().ContainsKey("otherHubsConnected") &&
                              gateway["otherHubsConnected"] is null;

                bool ok;
                using (var stderr = new StderrCapture())
                {
                    await hub.DisposeAsync();
                    hub = null;
                    ok = IsAlive(pid) &&
                         stderr.Count("Gateway mantido em execução") == 1 &&
                         stderr.Count("não foi possível saber se há outro Hub conectado") == 1;
                }

                return unknown && ok;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hub is not null)
                {
                    await hub.DisposeAsync();
                }
                if (unreadable is not null && deny is not null && unreadable.Exists)
                {
                    try
                    {
                        var security = unreadable.GetAccessControl();
                        security.RemoveAccessRule(deny);
                        unreadable.SetAccessControl(security);
                        unreadable.Delete();
                        Directory.Delete(unreadable.DirectoryName!);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
                KillLeftovers(children);
                DeleteQuietly(root);
            }
        }

        return await BusyLeaseAsync() && await UnreadablePresenceAsync();
    }

    private static async Task<bool> OtherHubsConnectedCountTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-count-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        // Um roteiro por Hub, para derrubar a conexão de um só.
        var scriptA = new BackendScript();
        var scriptB = new BackendScript();
        var scriptC = new BackendScript();
        HubService? hubC = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hubA = NewHub(profilePath, scriptA);
            // Antes de conectar ninguém está lá, nem este Hub.
            var before = OtherHubs(hubA) == 0 && !hubA.Presence.IsHeld(endpoint);
            await hubA.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var alone = OtherHubs(hubA) == 0 && hubA.Presence.IsHeld(endpoint);

            await using var hubB = NewHub(profilePath, scriptB);
            await hubB.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            hubC = NewHub(profilePath, scriptC);
            await hubC.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var three = OtherHubs(hubA) == 2 && OtherHubs(hubB) == 2 && OtherHubs(hubC) == 2 &&
                        PresenceFiles(hubA, endpoint).Length == 3;

            // Um Hub sai.
            await hubC.DisposeAsync();
            hubC = null;
            var two = OtherHubs(hubA) == 1 && OtherHubs(hubB) == 1;

            // Um Hub fica offline: deixa de contar para os outros, e continua contando os outros.
            scriptB.FailNextList();
            await hubB.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
            var offline = Flag(hubB.StatusJson()["backends"]?[0]?["online"]) == false &&
                          !hubB.Presence.IsHeld(endpoint) &&
                          OtherHubs(hubA) == 0 &&
                          OtherHubs(hubB) == 1;

            // Conecta de novo.
            await hubB.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var back = Flag(hubB.StatusJson()["backends"]?[0]?["online"]) == true &&
                       OtherHubs(hubA) == 1 && OtherHubs(hubB) == 1;

            return before && alone && three && two && offline && back;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hubC is not null)
            {
                await hubC.DisposeAsync();
            }
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> EscapeSwitchRestoresThePreviousExitTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-escape-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        HubService? owner = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            var startsBefore = ProcessSupervisor.TotalStarts;
            // A chave vem do ambiente do Hub, lida quando ele é criado.
            var previous = Environment.GetEnvironmentVariable(HubService.KeepSharedGatewayVariable);
            Environment.SetEnvironmentVariable(HubService.KeepSharedGatewayVariable, " OFF ");
            try
            {
                owner = NewHub(profilePath, script, keepSharedGateway: null);
            }
            finally
            {
                Environment.SetEnvironmentVariable(HubService.KeepSharedGatewayVariable, previous);
            }

            await owner.StartAsync(CancellationToken.None);
            var pid = owner.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (pid == 0)
            {
                return false;
            }
            children.Add(pid);

            await using var other = NewHub(profilePath, script);
            await other.StartAsync(CancellationToken.None);
            // A chave só muda a saída deste Hub: a presença dele é registrada e contada como a de qualquer outro.
            var seen = OtherHubs(owner) == 1 && OtherHubs(other) == 1 &&
                       ProcessSupervisor.TotalStarts == startsBefore + 1;

            bool stopped;
            using (var stderr = new StderrCapture())
            {
                await owner.DisposeAsync();
                owner = null;
                stopped = WaitForExit(pid, TimeSpan.FromSeconds(10)) &&
                          stderr.Count("Gateway mantido em execução") == 0;
            }

            // O outro Hub perdeu o Gateway, como antes da 0.3.0: a chamada seguinte o inicia de novo.
            var call = await other.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var newPid = other.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (newPid != 0)
            {
                children.Add(newPid);
            }

            return seen && stopped &&
                   ResultText(call) == "ok:test_tool" &&
                   newPid != 0 && newPid != pid &&
                   ProcessSupervisor.TotalStarts == startsBefore + 2;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> RefreshStillRestartsOwnGatewayTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-refreshshared-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        HubService? owner = null;
        HubService? other = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "before", withStartup: true));
            owner = NewHub(profilePath, script);
            await owner.StartAsync(CancellationToken.None);
            var firstPid = owner.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (firstPid == 0)
            {
                return false;
            }
            children.Add(firstPid);
            other = NewHub(profilePath, script);
            await other.StartAsync(CancellationToken.None);
            var shared = OtherHubs(owner) == 1 && OtherHubs(other) == 1;

            // Sem mudança no perfil o refresh só reconecta: o Gateway do próprio Hub continua o mesmo.
            var plainRefresh = await owner.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
            var sameGateway = !IsError(plainRefresh) &&
                              owner.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() == firstPid &&
                              IsAlive(firstPid) &&
                              owner.StatusJson()["lastProfileReloadTrigger"] is null &&
                              OtherHubs(owner) == 1;

            // gxhub_refresh é o ato deliberado: reinicia o Gateway deste Hub com o outro conectado. O status mostrou
            // antes que havia outro.
            WriteProfile(profilePath, TestProfile(root, port, "after", withStartup: true));
            var refresh = await owner.CallAsync("gxhub_refresh", new JsonObject(), CancellationToken.None);
            var secondPid = owner.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (secondPid != 0)
            {
                children.Add(secondPid);
            }
            var restarted = !IsError(refresh) &&
                            secondPid != 0 && secondPid != firstPid &&
                            WaitForExit(firstPid, TimeSpan.FromSeconds(10)) &&
                            MarkOf(owner) == "after" &&
                            // A presença deste Hub não foi solta nem refeita na troca dos backends.
                            owner.Presence.IsHeld(endpoint) && owner.Presence.Entries == 1;

            // O outro Hub nota o processo novo, recarrega o perfil e conecta de novo, com a mesma presença.
            var reconnected = await other.ReconcileAsync(fromTimer: true, CancellationToken.None) &&
                              Flag(other.StatusJson()["backends"]?[0]?["online"]) == true &&
                              other.StatusJson()["backends"]?[0]?["gateway"]?["listenerProcessId"]?.GetValue<int>() == secondPid &&
                              MarkOf(other) == "after" &&
                              other.Presence.IsHeld(endpoint) && other.Presence.Entries == 1 &&
                              OtherHubs(owner) == 1;

            // E na saída vale a regra nova para o Gateway novo.
            await owner.DisposeAsync();
            owner = null;
            var kept = IsAlive(secondPid);
            var call = await other.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);

            return shared && sameGateway && restarted && reconnected && kept && ResultText(call) == "ok:test_tool";
        }
        catch
        {
            return false;
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }
            if (other is not null)
            {
                await other.DisposeAsync();
            }
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> PresenceFollowsTheEndpointTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-presence-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var firstPort = ReserveFreePort();
        var secondPort = ReserveFreePort();
        var first = new Uri($"http://127.0.0.1:{firstPort}/mcp");
        var second = new Uri($"http://127.0.0.1:{secondPort}/mcp");
        var created = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var listener = ListenerLookup.Found(1001, created);
        var script = new BackendScript();

        try
        {
            using var observer = new HubPresence();
            WriteProfile(profilePath, TestProfile(root, firstPort, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script, _ => listener);
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var entered = observer.CountOthers(first) == 1 && hub.Presence.Entries == 1;

            // Conectar de novo ao mesmo endpoint mantém a presença que havia: por um refresh sem mudança, pela
            // reconexão passiva a um processo novo e pela recarga do perfil.
            await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
            var keptOnRefresh = observer.CountOthers(first) == 1 && hub.Presence.Entries == 1;
            listener = ListenerLookup.Found(2002, created.AddMinutes(1));
            var keptOnReconnect = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None) &&
                                  script.Clients == 3 &&
                                  observer.CountOthers(first) == 1 && hub.Presence.Entries == 1;
            WriteProfile(profilePath, TestProfile(root, firstPort, "outro nome", withStartup: false));
            await hub.RefreshFromProfileAsync(allowStartup: false, notify: false, CancellationToken.None);
            var keptOnReload = MarkOf(hub) == "outro nome" &&
                               observer.CountOthers(first) == 1 && hub.Presence.Entries == 1;

            // Backend offline não segura presença; quando volta, registra de novo.
            script.FailNextList();
            await hub.CallAsync("gxhub_status", new JsonObject(), CancellationToken.None);
            var releasedOffline = Flag(hub.StatusJson()["backends"]?[0]?["online"]) == false &&
                                  observer.CountOthers(first) == 0 && !hub.Presence.IsHeld(first);
            var backOnline = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None) &&
                             observer.CountOthers(first) == 1 && hub.Presence.Entries == 2;

            // O perfil troca o endpoint (gxhub_refresh): solta a presença do antigo e registra a do novo.
            WriteProfile(profilePath, TestProfile(root, secondPort, "outro nome", withStartup: false));
            await hub.RefreshFromProfileAsync(allowStartup: false, notify: false, CancellationToken.None);
            var moved = observer.CountOthers(first) == 0 && observer.CountOthers(second) == 1 &&
                        !hub.Presence.IsHeld(first) && hub.Presence.IsHeld(second) &&
                        hub.Presence.Entries == 3;

            // Backend desabilitado no perfil: sem conexão, sem presença.
            var disabled = TestProfile(root, secondPort, "outro nome", withStartup: false);
            disabled["backends"]![0]!["enabled"] = false;
            WriteProfile(profilePath, disabled);
            await hub.RefreshFromProfileAsync(allowStartup: false, notify: false, CancellationToken.None);
            var releasedDisabled = observer.CountOthers(second) == 0 && !hub.Presence.IsHeld(second);

            return entered && keptOnRefresh && keptOnReconnect && keptOnReload && releasedOffline && backOnline &&
                   moved && releasedDisabled;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> ArrivingHubRestartsAGatewayThatLeftTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-arriving-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var created = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var leavingGateway = ListenerLookup.Found(1001, created);
        var lookups = 0;
        var left = 0;
        var children = new List<int>();
        HubService? hub = null;
        // O Gateway do Hub que está de saída: escuta a porta até aquele Hub decidir.
        var leaving = new TcpListener(IPAddress.Loopback, port);
        var script = new BackendScript
        {
            Reachable = () => Volatile.Read(ref left) == 0 ||
                              ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };

        ListenerLookup Lookup(Uri uri)
        {
            if (Volatile.Read(ref left) == 1)
            {
                return ListenerInspector.Find(uri);
            }

            // A rodada de antes da chamada não vê ninguém; quem conecta é o início sob demanda.
            if (Interlocked.Increment(ref lookups) == 1)
            {
                return ListenerLookup.NotFound;
            }

            if (hub is not null && hub.Presence.IsHeld(uri))
            {
                // É a conferência logo depois de este Hub registrar a presença. O Hub que iniciou aquele Gateway
                // decidiu sair antes de enxergá-lo, não viu ninguém e o encerrou.
                leaving.Stop();
                Volatile.Write(ref left, 1);
                return ListenerInspector.Find(uri);
            }

            return leavingGateway;
        }

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            leaving.Start();
            var startsBefore = ProcessSupervisor.TotalStarts;
            hub = NewHub(profilePath, script, Lookup);

            var call = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            // A conferência aconteceu durante a chamada, não depois.
            var noticed = Volatile.Read(ref left) == 1;
            var backend = hub.StatusJson()["backends"]?[0];
            var pid = backend?["processId"]?.GetValue<int>() ?? 0;
            if (pid != 0)
            {
                children.Add(pid);
            }

            // Este Hub não ficou conectado a um Gateway que já tinha saído: iniciou o dele e a chamada foi atendida.
            return noticed &&
                   ResultText(call) == "ok:test_tool" && !IsError(call) &&
                   script.ToolCalls == 1 &&
                   ProcessSupervisor.TotalStarts == startsBefore + 1 &&
                   pid != 0 &&
                   Flag(backend?["online"]) == true &&
                   backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() == pid &&
                   Flag(backend["gateway"]?["startedByThisHub"]) == true &&
                   hub.Presence.IsHeld(endpoint);
        }
        catch
        {
            return false;
        }
        finally
        {
            leaving.Stop();
            if (hub is not null)
            {
                await hub.DisposeAsync();
            }
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> PresenceIsOnlyRegisteredUnderTheStartupLeaseTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-presencelease-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var created = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var script = new BackendScript();

        try
        {
            using var observer = new HubPresence();
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script, _ => ListenerLookup.Found(1001, created));
            hub.PresenceLeaseWait = TimeSpan.FromMilliseconds(300);
            // Every round tries again here; the pause between attempts is the subject of its own test.
            hub.PresenceRetryInterval = TimeSpan.Zero;
            var notifications = 0;
            hub.ToolsChanged += () => Interlocked.Increment(ref notifications);

            bool waiting;
            using (var stderr = new StderrCapture())
            {
                // Outro Hub está com a trava de início do endpoint (iniciando, entrando ou decidindo a saída).
                using (await BackendStartupLease.AcquireAsync(new[] { endpoint }, TimeSpan.FromSeconds(10), CancellationToken.None))
                {
                    await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
                    // Conecta e atende, mas não registra presença fora da trava; a rodada seguinte tenta de novo.
                    var connected = Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true &&
                                    !hub.Presence.IsHeld(endpoint) &&
                                    observer.CountOthers(endpoint) == 0;
                    var stillBusy = !await hub.ReconcileAsync(fromTimer: true, CancellationToken.None) &&
                                    !hub.Presence.IsHeld(endpoint) &&
                                    Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true;
                    waiting = connected && stillBusy;
                }

                // Dito uma vez, não a cada rodada.
                waiting = waiting && stderr.Count("presença deste Hub ainda não registrada") == 1;
            }

            // Trava livre: a rodada seguinte registra a presença, sem refazer a conexão nem avisar mudança de tools.
            var round = await hub.ReconcileAsync(fromTimer: true, CancellationToken.None);
            return waiting && !round &&
                   hub.Presence.IsHeld(endpoint) &&
                   observer.CountOthers(endpoint) == 1 &&
                   hub.Presence.Entries == 1 &&
                   script.Clients == 1 &&
                   Volatile.Read(ref notifications) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> FailedQuestionOnTheWayOutLeavesTheGatewayRunningTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-exitfail-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        var broken = 0;
        HubService? hub = null;

        // A tabela de listeners passa a falhar no instante em que o Hub sai: quem sai não consegue saber se o
        // Gateway tem outro usuário.
        ListenerLookup Lookup(Uri uri) =>
            Volatile.Read(ref broken) == 1
                ? throw new InvalidOperationException("tabela de listeners indisponível (falha sintética)")
                : ListenerInspector.Find(uri);

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            hub = NewHub(profilePath, script, Lookup);
            await hub.StartAsync(CancellationToken.None);
            var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (pid == 0)
            {
                return false;
            }
            children.Add(pid);

            bool ok;
            using (var stderr = new StderrCapture())
            {
                Volatile.Write(ref broken, 1);
                await hub.DisposeAsync();
                hub = null;
                // Sem resposta, o erro seguro é sobrar um Gateway: ele fica, e o Hub diz por quê.
                ok = IsAlive(pid) &&
                     stderr.Count("Gateway mantido em execução") == 1 &&
                     stderr.Count("a consulta aos outros Hubs falhou") == 1;
            }

            return ok && ListenerInspector.Find(endpoint).State == ListenerLookupState.Found;
        }
        catch
        {
            return false;
        }
        finally
        {
            Volatile.Write(ref broken, 0);
            if (hub is not null)
            {
                await hub.DisposeAsync();
            }
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static bool PresenceThatCannotBeRegisteredIsNotAnErrorTest()
    {
        var root = NewTempRoot("GxMcpHub-nopresence-test-");
        var endpoint = new Uri("http://127.0.0.1:65001/mcp");

        try
        {
            // A pasta das presenças não pode ser criada: no lugar dela existe um arquivo.
            var blocked = Path.Combine(root, "bloqueado");
            File.WriteAllText(blocked, "arquivo no lugar da pasta");
            using var stderr = new StderrCapture();
            using var presence = new HubPresence(blocked);

            var refused = !presence.TryEnter(endpoint);
            // Nem erro nem presença: o Hub segue como se ninguém o enxergasse, e diz isso uma vez, sem caminho.
            var quiet = !presence.IsHeld(endpoint) && presence.Entries == 0 &&
                        stderr.Count("a presença deste Hub não pôde ser registrada") == 1 &&
                        stderr.Count(root) == 0;
            // Olhar os outros nessa pasta não é um erro: não há presença de ninguém.
            return refused && quiet && presence.CountOthers(endpoint) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    // O que os autotestes leem de volta do sistema: o flag de herança de um identificador e o tipo dele.
    private static class NativeHandles
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int which);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetHandleInformation(IntPtr handle, out uint flags);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint GetFileType(IntPtr handle);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        public static extern IntPtr CreateEventW(
            ref SecurityAttributes attributes,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool manualReset,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool initialState,
            string? name);

        public static IntPtr CreateEvent(bool inheritable)
        {
            var attributes = new SecurityAttributes
            {
                Length = System.Runtime.InteropServices.Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = inheritable ? 1 : 0
            };
            return CreateEventW(ref attributes, true, false, null);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        public static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint size);

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        public static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, int length);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool IsProcessInJob(
            IntPtr process,
            IntPtr job,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool result);

        public const uint HandleFlagProtectFromClose = 2;
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JobLimitKillOnJobClose = 0x2000;
        public const uint JobLimitSilentBreakawayOk = 0x1000;

        // A job object with the given limit flags (x64 layout of JOBOBJECT_EXTENDED_LIMIT_INFORMATION: the flags sit
        // at offset 16 of a block of 144 bytes). Zero when it could not be made.
        public static IntPtr CreateJob(uint limitFlags)
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            const int size = 144;
            var information = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                for (var offset = 0; offset < size; offset += 8)
                {
                    System.Runtime.InteropServices.Marshal.WriteInt64(information, offset, 0);
                }

                System.Runtime.InteropServices.Marshal.WriteInt32(information, 16, unchecked((int)limitFlags));
                if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, information, size))
                {
                    return job;
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(information);
            }

            CloseHandle(job);
            return IntPtr.Zero;
        }

        public const uint FileTypeDisk = 1;
        public const uint FileTypePipe = 3;
        public const uint HandleFlagInherit = 1;
    }

    private static bool StandardHandlesAreNotInheritedTest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            static bool Inheritable(IntPtr handle) =>
                NativeHandles.GetHandleInformation(handle, out var flags) &&
                (flags & NativeHandles.HandleFlagInherit) != 0;

            static bool NotInheritable(IntPtr handle) =>
                NativeHandles.GetHandleInformation(handle, out var flags) &&
                (flags & NativeHandles.HandleFlagInherit) == 0;

            // Um identificador herdável, como os canais que quem lança o Hub lhe entrega: o ajudante tira o flag e o
            // sistema confirma, a segunda vez também.
            using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.Out,
                System.IO.HandleInheritability.Inheritable);
            var handle = pipe.ClientSafePipeHandle.DangerousGetHandle();
            var startedInheritable = Inheritable(handle);
            var cleared = StandardHandles.StopInheriting(handle) && NotInheritable(handle);
            var again = StandardHandles.StopInheriting(handle) && NotInheritable(handle);

            // O que não é identificador: nada acontece e nada é lançado. Nem o zero, nem INVALID_HANDLE_VALUE
            // (um Hub iniciado sem canais padrão), nem um número que não é identificador deste processo.
            var invalid =
                !StandardHandles.StopInheriting(IntPtr.Zero) &&
                !StandardHandles.StopInheriting(new IntPtr(-1)) &&
                !StandardHandles.StopInheriting(new IntPtr(0x7FFF0000));

            // E o que o Main fez com os canais deste próprio processo, que quem lançou a suíte lhe entregou: os que
            // são arquivo ou pipe (console fica de fora) já não são herdáveis.
            var standard = true;
            foreach (var which in new[] { -10, -11, -12 })
            {
                var channel = NativeHandles.GetStdHandle(which);
                if (channel == IntPtr.Zero || channel == new IntPtr(-1))
                {
                    continue;
                }

                var type = NativeHandles.GetFileType(channel);
                if (type is NativeHandles.FileTypeDisk or NativeHandles.FileTypePipe)
                {
                    standard = standard && NotInheritable(channel);
                }
            }

            return startedInheritable && cleared && again && invalid && standard;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HubThatLeavesAChildDoesNotHoldTheLauncherChannelsTestAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var children = new List<int>();
        Process? helper = null;
        System.IO.Pipes.AnonymousPipeServerStream? loose = null;

        try
        {
            // Quem lança o Hub tem, além dos três canais que lhe dá, um pipe só seu, que o Hub não usa: um identificador
            // herdável que vai junto (o pipe de saída que um chamador captura chega assim ao Hub).
            loose = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.In,
                System.IO.HandleInheritability.Inheritable);

            // Um "Hub" que inicia um processo que escuta a porta, do jeito que o Hub inicia um backend, e sai.
            var (command, prefix) = SelfTestChildCommand();
            helper = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = $"{prefix}{SelfTestListener.LeaveChildSwitch} {port} 120",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (helper is null)
            {
                return false;
            }

            // De agora em diante só o "Hub" (e o que ele passe adiante) segura a ponta de escrita do pipe solto.
            loose.DisposeLocalCopyOfClientHandle();

            // Quem o lançou lê a saída e o erro até o fim, como quem espera o Hub terminar.
            var output = helper.StandardOutput.ReadToEndAsync();
            var error = helper.StandardError.ReadToEndAsync();
            var both = Task.WhenAll(output, error);
            var ended = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20))) == both;

            // O processo que ele deixou está vivo e escutando: sem isso o fim dos canais não provaria nada.
            var listening = await WaitUntilAsync(
                () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found,
                TimeSpan.FromSeconds(20));
            if (ListenerInspector.Find(endpoint).ProcessId is int childId)
            {
                children.Add(childId);
            }

            var exited = helper.WaitForExit(10000);

            // A entrada: sem ninguém com a ponta de leitura aberta, escrever para um Hub que já saiu falha.
            var inputClosed = false;
            try
            {
                helper.StandardInput.WriteLine("ainda há quem leia?");
                helper.StandardInput.Flush();
            }
            catch (IOException)
            {
                inputClosed = true;
            }

            // O pipe solto: só chega ao fim se nenhuma cópia da ponta de escrita sobrou, nem no processo que ficou.
            var buffer = new byte[16];
            var looseRead = loose.ReadAsync(buffer, 0, buffer.Length);
            var looseEnded = await Task.WhenAny(looseRead, Task.Delay(TimeSpan.FromSeconds(20))) == looseRead &&
                             looseRead.IsCompletedSuccessfully && looseRead.Result == 0;

            return ended && listening && exited && inputClosed && looseEnded &&
                   output.IsCompletedSuccessfully &&
                   output.Result.Contains(SelfTestListener.LeaveChildReady, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
        finally
        {
            helper?.Dispose();
            KillLeftovers(children);
            loose?.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // 0.3.0 v2: identificadores herdáveis, presença sem reconciliação, trava na chegada e na saída, job, registro.
    // ------------------------------------------------------------------------------------------------------------

    private static string? _originalTemp;

    // A value above which a handle is "high": the table of the process must have thousands of entries below it.
    private const long HighHandleValue = 0x10000;

    private static bool SelfTestUsesATemporaryFolderOfItsOwnTest()
    {
        var current = Path.GetTempPath();
        // Both variables say the same folder: processes the tests start read either one.
        var sameVariables =
            string.Equals(
                Environment.GetEnvironmentVariable("TMP")?.TrimEnd('\\'),
                current.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                Environment.GetEnvironmentVariable("TEMP")?.TrimEnd('\\'),
                current.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        return sameVariables &&
               _originalTemp is not null &&
               current.Contains("GxMcpHub-selftest-", StringComparison.Ordinal) &&
               current.StartsWith(_originalTemp, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(
                   current.TrimEnd('\\'),
                   _originalTemp.TrimEnd('\\'),
                   StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(current);
    }

    // The startup lease and the presence of an endpoint are named by its absolute URI, as the profile writes it: the same
    // Gateway reached as localhost or as 127.0.0.1, or with a trailing slash, is another endpoint to the Hubs. A limit
    // the profiles of the operation do not meet (they all say 127.0.0.1 and /mcp); it is pinned here so that what the
    // documentation says stays true.
    private static bool EndpointKeyIsTheAbsoluteUriTest()
    {
        var plain = new Uri("http://127.0.0.1:5010/mcp");
        var sameAgain = new Uri("http://127.0.0.1:5010/mcp");
        var localhost = new Uri("http://localhost:5010/mcp");
        var slash = new Uri("http://127.0.0.1:5010/mcp/");
        var otherPort = new Uri("http://127.0.0.1:5011/mcp");
        static string Key(Uri uri) => BackendStartupLease.HashOf(new[] { uri });
        using var presence = new HubPresence(Path.Combine(Path.GetTempPath(), "GxMcpHub-keytest-" + Guid.NewGuid().ToString("N")));
        return Key(plain) == Key(sameAgain) &&
               Key(plain) != Key(localhost) && Key(plain) != Key(slash) && Key(plain) != Key(otherPort) &&
               Path.GetFileName(presence.FolderFor(plain)) == Key(plain) &&
               presence.FolderFor(plain) == presence.FolderFor(sameAgain) &&
               presence.FolderFor(plain) != presence.FolderFor(localhost) &&
               presence.FolderFor(plain) != presence.FolderFor(slash);
    }

    private static bool SweepClearsEveryInheritableHandleTest() =>
        !OperatingSystem.IsWindows() || SweepClearsInheritableHandles();

    // The handles a pass over the inheritable ones must find (and some it must leave alone), made after thousands of
    // others so that they have high values and the table is bigger than the first buffer of the system call: an
    // inheritable event, the inheritable end of an anonymous pipe, an inheritable file, an inheritable event that is
    // also protected from close, and three that are not inheritable (an event, a pipe and a file).
    private sealed class HandlesUnderTest : IDisposable
    {
        public List<IntPtr> Raw { get; } = new();

        public List<IDisposable> Objects { get; } = new();

        public IntPtr Protected { get; set; }

        public IntPtr[] Inheritable { get; set; } = Array.Empty<IntPtr>();

        public IntPtr[] Plain { get; set; } = Array.Empty<IntPtr>();

        public void Dispose()
        {
            if (Protected != IntPtr.Zero)
            {
                NativeHandles.SetHandleInformation(Protected, NativeHandles.HandleFlagProtectFromClose, 0);
            }

            foreach (var handle in Raw)
            {
                NativeHandles.CloseHandle(handle);
            }

            foreach (var disposable in Objects)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Already disposed.
                }
            }
        }
    }

    private static HandlesUnderTest CreateHandlesUnderTest()
    {
        var created = new HandlesUnderTest();
        try
        {
            var inheritableEvent = NativeHandles.CreateEvent(inheritable: true);
            created.Raw.Add(inheritableEvent);
            var pipe = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.In,
                System.IO.HandleInheritability.Inheritable);
            created.Objects.Add(pipe);
            var file = new FileStream(
                Path.Combine(Path.GetTempPath(), "inheritable-" + Guid.NewGuid().ToString("N")),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Inheritable | FileShare.Delete,
                1,
                FileOptions.DeleteOnClose);
            created.Objects.Add(file);
            created.Protected = NativeHandles.CreateEvent(inheritable: true);
            created.Raw.Add(created.Protected);
            NativeHandles.SetHandleInformation(
                created.Protected,
                NativeHandles.HandleFlagProtectFromClose,
                NativeHandles.HandleFlagProtectFromClose);

            var plainEvent = NativeHandles.CreateEvent(inheritable: false);
            created.Raw.Add(plainEvent);
            var plainPipe = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.In,
                System.IO.HandleInheritability.None);
            created.Objects.Add(plainPipe);
            var plainFile = new FileStream(
                Path.Combine(Path.GetTempPath(), "plain-" + Guid.NewGuid().ToString("N")),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            created.Objects.Add(plainFile);

            created.Inheritable = new[]
            {
                inheritableEvent,
                pipe.ClientSafePipeHandle.DangerousGetHandle(),
                file.SafeFileHandle.DangerousGetHandle(),
                created.Protected
            };
            created.Plain = new[]
            {
                plainEvent,
                plainPipe.SafePipeHandle.DangerousGetHandle(),
                plainFile.SafeFileHandle.DangerousGetHandle()
            };
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    // The same handles go through both paths: the table of the system and, forced, the scan up to the ceiling. The
    // sweep must take the inheritance from the inheritable ones, leave the others as they are, and touch nothing else
    // of a handle (here, protection from close).
    private static bool SweepClearsInheritableHandles()
    {
        var fillers = new List<IntPtr>();
        HandlesUnderTest? under = null;
        try
        {
            // The free slots of the table are used before new ones, whatever the tests before left behind. The table is
            // filled, and the handles under test made, until all of them are above the value that counts as high; the
            // ones made too low are closed and the filling goes on.
            for (var attempt = 0; attempt < 14 && under is null; attempt++)
            {
                for (var index = 0; index < 2000; index++)
                {
                    fillers.Add(NativeHandles.CreateEvent(inheritable: false));
                }

                var candidate = CreateHandlesUnderTest();
                if (candidate.Inheritable.Concat(candidate.Plain).Min(handle => handle.ToInt64()) > HighHandleValue)
                {
                    under = candidate;
                }
                else
                {
                    candidate.Dispose();
                }
            }

            if (under is null)
            {
                Console.Error.WriteLine("[self-test] varredura: a tabela de identificadores não chegou a valores altos.");
                return false;
            }

            // Holes below the handles under test, as in a table that has lived.
            for (var index = 0; index < fillers.Count; index += 2)
            {
                NativeHandles.CloseHandle(fillers[index]);
                fillers[index] = IntPtr.Zero;
            }

            bool Flag(IntPtr handle, uint bit) =>
                NativeHandles.GetHandleInformation(handle, out var flags) && (flags & bit) != 0;

            var handles = under;
            bool Run(bool forceScan)
            {
                // Inheritable again: the second pass starts from the same state as the first.
                foreach (var handle in handles.Inheritable)
                {
                    NativeHandles.SetHandleInformation(handle, NativeHandles.HandleFlagInherit, NativeHandles.HandleFlagInherit);
                }

                var before = handles.Inheritable.All(handle => Flag(handle, NativeHandles.HandleFlagInherit)) &&
                             handles.Plain.All(handle => !Flag(handle, NativeHandles.HandleFlagInherit));
                var sweep = StandardHandles.StopInheritingAll(forceScan);
                var after = handles.Inheritable.All(handle => !Flag(handle, NativeHandles.HandleFlagInherit)) &&
                            handles.Plain.All(handle => !Flag(handle, NativeHandles.HandleFlagInherit));
                var protectKept = Flag(handles.Protected, NativeHandles.HandleFlagProtectFromClose);
                var expectedPath = forceScan ? StandardHandles.PathScan : StandardHandles.PathNt;
                var ok = before && after && protectKept && sweep.Path == expectedPath &&
                         sweep.Cleared >= handles.Inheritable.Length;
                if (!ok)
                {
                    Console.Error.WriteLine(
                        $"[self-test] varredura ({(forceScan ? "scan" : "nt")}): antes={before} depois={after} " +
                        $"protecao={protectKept} caminho={sweep.Path} limpos={sweep.Cleared} " +
                        $"menor={handles.Inheritable.Min(handle => handle.ToInt64())}");
                }

                return ok;
            }

            return Run(forceScan: false) && Run(forceScan: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[self-test] varredura: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            under?.Dispose();
            foreach (var handle in fillers.Where(handle => handle != IntPtr.Zero))
            {
                NativeHandles.CloseHandle(handle);
            }
        }
    }

    private static bool HandleSweepLogicTest()
    {
        var ceiling = StandardHandles.ScanCeiling;

        // A table of handle values and their flags; one probe that throws, one handle that cannot be changed, one
        // that throws when changed, one at the ceiling itself and one just past it.
        Dictionary<long, uint> NewTable() => new()
        {
            [4] = 1, [8] = 0, [12] = 1, [16] = 1, [4096] = 1, [8192] = 1, [ceiling] = 1, [ceiling + 4] = 1
        };

        var cleared = new List<long>();
        var table = NewTable();

        uint? Read(IntPtr handle)
        {
            if (handle.ToInt64() == 4096)
            {
                throw new InvalidOperationException("sonda sintética que falha");
            }

            return table.TryGetValue(handle.ToInt64(), out var flags) ? flags : null;
        }

        bool Clear(IntPtr handle)
        {
            var value = handle.ToInt64();
            if (value == 12)
            {
                throw new InvalidOperationException("troca sintética que falha");
            }

            if (value == 16)
            {
                return false;
            }

            cleared.Add(value);
            table[value] = 0;
            return true;
        }

        // The fallback: every value up to the ceiling, none before, none after, and no probe or change that fails
        // stops the others.
        var scan = StandardHandles.StopInheritingAll(forceScan: true, getFlags: Read, clear: Clear);
        var scanOk = scan.Path == StandardHandles.PathScan &&
                     scan.Probes == ceiling / 4 &&
                     cleared.SequenceEqual(new long[] { 4, 8192, ceiling }) &&
                     scan.Cleared == 3 &&
                     table[ceiling + 4] == 1 &&
                     table[8] == 0;

        // The table of the system, when it can be read: only what it lists is changed, and no probing is done.
        cleared.Clear();
        // One handle that cannot be changed and one whose change throws do not stop the ones after them.
        var listed = new List<IntPtr> { new(4), new(20), new(0x7000), new(0x7004) };
        var nt = StandardHandles.StopInheritingAll(
            readTable: () => new HandleTable(250, listed),
            clear: handle =>
            {
                cleared.Add(handle.ToInt64());
                if (handle.ToInt64() == 0x7000)
                {
                    throw new InvalidOperationException("troca sintética que falha");
                }

                return handle.ToInt64() != 20;
            });
        var ntOk = nt.Path == StandardHandles.PathNt &&
                   nt.Probes == 250 &&
                   nt.Cleared == 2 &&
                   cleared.SequenceEqual(new long[] { 4, 20, 0x7000, 0x7004 });

        // Whatever makes the table unreadable (no table, or an exception), the scan takes over and still finds the handles.
        bool FallsBack(Func<HandleTable?> readTable)
        {
            table = NewTable();
            cleared.Clear();
            var result = StandardHandles.StopInheritingAll(readTable: readTable, getFlags: Read, clear: Clear);
            return result.Path == StandardHandles.PathScan && result.Cleared == 3 &&
                   cleared.SequenceEqual(new long[] { 4, 8192, ceiling });
        }

        var fallsBack = FallsBack(() => null) &&
                        FallsBack(() => throw new InvalidOperationException("tabela ilegível (sintético)"));

        // With nothing to clear and nothing that can be read, nothing is thrown either.
        var quiet = StandardHandles.StopInheritingAll(
            forceScan: true,
            ceiling: 64,
            getFlags: _ => throw new InvalidOperationException("sempre falha"),
            clear: _ => throw new InvalidOperationException("nunca chega aqui"));
        var quietOk = quiet.Path == StandardHandles.PathScan && quiet.Cleared == 0 && quiet.Probes == 16;

        return scanOk && ntOk && fallsBack && quietOk;
    }

    private static async Task<bool> ProcessStartWorksAfterTheSweepTestAsync()
    {
        try
        {
            // The sweep runs in Main, once; the pipes of each child are made at its own Process.Start, later.
            StandardHandles.StopInheritingAll();
            var (command, prefix) = SelfTestChildCommand();

            async Task<bool> RunHelpAsync()
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = prefix + "--help",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process is null)
                {
                    return false;
                }

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                return process.ExitCode == 0 && output.Contains("GxMcpHub " + HubInfo.Version, StringComparison.Ordinal);
            }

            // Four at once, as two backends starting together would be.
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(RunHelpAsync)));
            return results.All(ok => ok) && await HubStartMakesNoPassScenarioAsync();
        }
        catch
        {
            return false;
        }
    }

    // The Hub that starts a backend makes no pass over the handles of its own: the one of Main is the only one, and a
    // pass in the middle of another thread's Process.Start would invalidate the ends of the pipes it has just made.
    private static async Task<bool> HubStartMakesNoPassScenarioAsync()
    {
        var root = NewTempRoot("GxMcpHub-nopass-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        HubService? hub = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            hub = NewHub(profilePath, script);
            var before = StandardHandles.PassCount;
            await hub.StartAsync(CancellationToken.None);
            var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            if (pid != 0)
            {
                children.Add(pid);
            }

            return pid != 0 && IsAlive(pid) && StandardHandles.PassCount == before;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hub is not null)
            {
                await hub.DisposeAsync();
            }

            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    // A Hub (the real executable, --config) killed by force with its Gateway alive, started by a process that gave it a
    // loose inheritable handle of its own, like the pipe a caller captures. Once the Hub is gone, the caller sees that
    // pipe end, with the Gateway alive: the Gateway did not inherit it.
    private static async Task<bool> RealHubDoesNotHandLooseHandlesToItsGatewayTestAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var root = NewTempRoot("GxMcpHub-loosehandle-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var children = new List<int>();
        Process? hub = null;
        System.IO.Pipes.AnonymousPipeServerStream? loose = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            loose = new System.IO.Pipes.AnonymousPipeServerStream(
                System.IO.Pipes.PipeDirection.In,
                System.IO.HandleInheritability.Inheritable);
            var (command, prefix) = SelfTestChildCommand();
            hub = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = $"{prefix}--config \"{profilePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (hub is null)
            {
                return false;
            }

            // From here on the write end of the pipe is held by the Hub alone (and by whatever the Hub passes it to).
            loose.DisposeLocalCopyOfClientHandle();
            var listening = await WaitUntilAsync(
                () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found,
                TimeSpan.FromSeconds(30));
            if (ListenerInspector.Find(endpoint).ProcessId is int gatewayId)
            {
                children.Add(gatewayId);
            }

            // No shutdown path runs: the Gateway outlives the Hub.
            hub.Kill();
            hub.WaitForExit(10000);
            var buffer = new byte[16];
            var read = loose.ReadAsync(buffer, 0, buffer.Length);
            var ended = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(20))) == read;
            var gatewayAlive = ListenerInspector.Find(endpoint).State == ListenerLookupState.Found;
            return listening && ended && read.IsCompletedSuccessfully && read.Result == 0 && gatewayAlive;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                hub?.Kill(entireProcessTree: false);
            }
            catch
            {
                // Already gone.
            }

            hub?.Dispose();
            KillLeftovers(children);
            loose?.Dispose();
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> StatusShowsSweepAndTempRootTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-tempid-test-");
        var otherFolder = NewTempRoot("GxMcpHub-tempid-other-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var script = new BackendScript();
        var originalTmp = Environment.GetEnvironmentVariable("TMP");
        var originalTemp = Environment.GetEnvironmentVariable("TEMP");

        // The identifier of the TEMP folder when the environment says it with another spelling.
        string IdWith(string temp)
        {
            Environment.SetEnvironmentVariable("TMP", temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
            try
            {
                return HubPresence.TempRootId();
            }
            finally
            {
                Environment.SetEnvironmentVariable("TMP", originalTmp);
                Environment.SetEnvironmentVariable("TEMP", originalTemp);
            }
        }

        string? ShortForm(string path)
        {
            var buffer = new char[1024];
            var length = NativeHandles.GetShortPathNameW(path, buffer, (uint)buffer.Length);
            return length == 0 || length > buffer.Length ? null : new string(buffer, 0, (int)length);
        }

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script);
            // The status says what the last pass was, whatever it was (the real one here is almost always "nt").
            var realSweep = StandardHandles.LastSweep;
            var shownAsReported = true;
            try
            {
                foreach (var (shownPath, shownCleared) in new[]
                         {
                             (StandardHandles.PathScan, 7),
                             (StandardHandles.PathNone, 0),
                             (StandardHandles.PathNt, 31)
                         })
                {
                    StandardHandles.LastSweep = new HandleSweep(shownPath, shownCleared, 100, 1.5);
                    var shown = hub.StatusJson();
                    shownAsReported = shownAsReported &&
                                      Text(shown["inheritableHandlesPath"]) == shownPath &&
                                      shown["inheritableHandlesCleared"]?.GetValue<int>() == shownCleared;
                }
            }
            finally
            {
                StandardHandles.LastSweep = realSweep;
            }

            var status = hub.StatusJson();
            var path = Text(status["inheritableHandlesPath"]);
            var cleared = status["inheritableHandlesCleared"]?.GetValue<int>();
            var id = Text(status["hubTempRootId"]);

            var current = Path.GetTempPath();
            var variants = new List<string>
            {
                current.TrimEnd('\\'),
                current.ToLowerInvariant(),
                current.ToUpperInvariant(),
                current.Replace('\\', '/')
            };
            if (OperatingSystem.IsWindows() && ShortForm(current.TrimEnd('\\')) is string shortForm)
            {
                variants.Add(shortForm);
            }

            var sweepShown = shownAsReported && path == StandardHandles.LastSweep.Path &&
                             (path is StandardHandles.PathNt or StandardHandles.PathScan or StandardHandles.PathNone) &&
                             cleared == StandardHandles.LastSweep.Cleared && cleared >= 0;
            var idShown = id is not null && id.Length == 12 && id.All(character => Uri.IsHexDigit(character) && !char.IsUpper(character)) &&
                          // Never the path, in any part.
                          !current.Contains(id, StringComparison.OrdinalIgnoreCase);
            // The same folder spelled in another way is the same identifier; another folder is another one.
            var sameFolder = variants.All(variant => IdWith(variant) == id);
            var otherFolderId = IdWith(otherFolder) != id;
            return sweepShown && idShown && sameFolder && otherFolderId;
        }
        catch
        {
            return false;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", originalTmp);
            Environment.SetEnvironmentVariable("TEMP", originalTemp);
            DeleteQuietly(root);
            DeleteQuietly(otherFolder);
        }
    }

    private static async Task<bool> PresenceIsRetriedBeforeACallWithReconcileOffTestAsync() =>
        await PresenceRetryScenarioAsync(viaGenericCall: false) &&
        await PresenceRetryScenarioAsync(viaGenericCall: true) &&
        await PresenceRetryPauseScenarioAsync() &&
        await PresenceRetryFindsTheGatewayGoneScenarioAsync();

    // The Hub connected while the startup lease was busy, and the Gateway went away before its presence could be
    // registered: the call that comes next finds out, the backend goes offline, and the call is not sent to a Gateway
    // that is not there.
    private static async Task<bool> PresenceRetryFindsTheGatewayGoneScenarioAsync()
    {
        var root = NewTempRoot("GxMcpHub-presencegone-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var created = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var script = new BackendScript();
        var gone = 0;

        ListenerLookup Lookup(Uri _) =>
            Volatile.Read(ref gone) == 1 ? ListenerLookup.NotFound : ListenerLookup.Found(1001, created);

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script, Lookup, autoReconcile: false);
            hub.PresenceLeaseWait = TimeSpan.FromMilliseconds(200);
            hub.PresenceRetryInterval = TimeSpan.Zero;

            bool unseen;
            using (await BackendStartupLease.AcquireAsync(new[] { endpoint }, TimeSpan.FromSeconds(10), CancellationToken.None))
            {
                await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
                unseen = Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true && !hub.Presence.IsHeld(endpoint);
            }

            var notifications = 0;
            hub.ToolsChanged += () => Interlocked.Increment(ref notifications);
            Volatile.Write(ref gone, 1);
            var result = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var backend = hub.StatusJson()["backends"]?[0];
            return unseen && IsError(result) &&
                   Flag(backend?["online"]) == false &&
                   !hub.Presence.IsHeld(endpoint) &&
                   script.ToolCalls == 0 &&
                   BackendToolNames(hub).Length == 0 &&
                   Volatile.Read(ref notifications) == 1;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    // While the startup lease stays busy, the calls that come within the pause between attempts are not held up
    // waiting for it: the wait for the lease is for the attempt, not for every call.
    private static async Task<bool> PresenceRetryPauseScenarioAsync()
    {
        var root = NewTempRoot("GxMcpHub-presencepause-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var created = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var script = new BackendScript();

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script, _ => ListenerLookup.Found(1001, created), autoReconcile: false);
            hub.PresenceLeaseWait = TimeSpan.FromMilliseconds(1500);
            hub.PresenceRetryInterval = TimeSpan.FromSeconds(30);

            using (await BackendStartupLease.AcquireAsync(new[] { endpoint }, TimeSpan.FromSeconds(10), CancellationToken.None))
            {
                // The connection waits for the lease and gives up: one attempt, 1.5 s.
                await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
                var connected = Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true &&
                                !hub.Presence.IsHeld(endpoint);

                var watch = Stopwatch.StartNew();
                var first = await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
                var second = await hub.CallAsync(
                    "gxhub_call",
                    new JsonObject
                    {
                        ["backend"] = "native",
                        ["tool"] = "test_tool",
                        ["arguments"] = new JsonObject()
                    },
                    CancellationToken.None);
                var quick = watch.Elapsed < TimeSpan.FromMilliseconds(1000);
                return connected && quick &&
                       ResultText(first) == "ok:test_tool" && ResultText(second) == "ok:test_tool" &&
                       !hub.Presence.IsHeld(endpoint);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    // The switch of the reconciliation is off, so no round and no reconnection will ever run; the startup lease of the
    // endpoint was busy when the Hub connected, so it is connected and not seen. The call that comes next must
    // register the presence before it is dispatched.
    private static async Task<bool> PresenceRetryScenarioAsync(bool viaGenericCall)
    {
        var root = NewTempRoot("GxMcpHub-presenceretry-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var created = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var script = new BackendScript();

        try
        {
            using var observer = new HubPresence();
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script, _ => ListenerLookup.Found(1001, created), autoReconcile: false);
            hub.PresenceLeaseWait = TimeSpan.FromMilliseconds(150);
            hub.PresenceRetryInterval = TimeSpan.FromMilliseconds(100);

            bool unseen;
            using (await BackendStartupLease.AcquireAsync(new[] { endpoint }, TimeSpan.FromSeconds(10), CancellationToken.None))
            {
                await hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
                unseen = Flag(hub.StatusJson()["backends"]?[0]?["online"]) == true &&
                         !hub.Presence.IsHeld(endpoint) && observer.CountOthers(endpoint) == 0;
            }

            // Past the interval the Hub waits between attempts, with the lease free again.
            await Task.Delay(250);
            var round = await hub.ReconcileAsync(fromTimer: false, CancellationToken.None);
            var stillUnseen = !hub.Presence.IsHeld(endpoint);
            var result = viaGenericCall
                ? await hub.CallAsync(
                    "gxhub_call",
                    new JsonObject
                    {
                        ["backend"] = "native",
                        ["tool"] = "test_tool",
                        ["arguments"] = new JsonObject()
                    },
                    CancellationToken.None)
                : await hub.CallAsync("native_test_tool", new JsonObject(), CancellationToken.None);
            var seen = hub.Presence.IsHeld(endpoint) && observer.CountOthers(endpoint) == 1;

            return unseen && !round && stillUnseen && ResultText(result) == "ok:test_tool" && seen;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    // A Hub that arrives while another Hub is stopping the Gateway and holding the startup lease for longer than the
    // arriving Hub waits for it: the lease times out, and the arriving Hub still looks at the listener. The Gateway
    // that went away is not a connection to keep.
    private static async Task<bool> ArrivingHubWithBusyLeaseChecksTheListenerTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-busyarrival-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        Process? gateway = null;

        try
        {
            // A Gateway that another Hub started: a real process that listens.
            var (command, prefix) = SelfTestChildCommand();
            gateway = Process.Start(ProcessSupervisor.BuildStartInfo(
                new StartupConfig
                {
                    Command = command,
                    Arguments = $"{prefix}{SelfTestListener.Switch} {port} 60",
                    WorkingDirectory = root
                },
                null));
            if (gateway is null)
            {
                return false;
            }

            children.Add(gateway.Id);
            if (!await WaitUntilAsync(
                    () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found,
                    TimeSpan.FromSeconds(20)))
            {
                return false;
            }

            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: false));
            await using var hub = NewHub(profilePath, script);
            hub.PresenceLeaseWait = TimeSpan.FromMilliseconds(1500);

            using (await BackendStartupLease.AcquireAsync(new[] { endpoint }, TimeSpan.FromSeconds(20), CancellationToken.None))
            {
                var connecting = hub.RefreshAsync(allowStartup: false, notify: false, CancellationToken.None);
                await Task.Delay(500);
                // The Gateway goes away while the arriving Hub waits for the lease.
                gateway.Kill(entireProcessTree: true);
                gateway.WaitForExit(10000);
                await connecting;
            }

            var backend = hub.StatusJson()["backends"]?[0];
            return Flag(backend?["online"]) == false &&
                   !hub.Presence.IsHeld(endpoint) &&
                   script.ToolCalls == 0 &&
                   BackendToolNames(hub).Length == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            gateway?.Dispose();
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static bool LeaseIsHeld(Uri endpoint)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "GxMcpHub",
            "backend-startup",
            BackendStartupLease.HashOf(new[] { endpoint }) + ".lock");
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
            }

            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32)
        {
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static async Task<bool> OwnerReleasesTheLeaseOnlyWhenNobodyListensTestAsync() =>
        await OwnerLeaseScenarioAsync(wrapper: false) && await OwnerLeaseScenarioAsync(wrapper: true);

    // The Hub that stops its Gateway on the way out keeps the startup lease until nothing listens any more. With a
    // wrapper script the process it started is the script, and what listens is the process the script started, still
    // closing when Stop returns. The scenario with the scripted listener makes that visible: after Stop it still
    // "listens" for a few looks, and the lease must be held at every one of them, including the one that finds nobody.
    private static async Task<bool> OwnerLeaseScenarioAsync(bool wrapper)
    {
        var root = NewTempRoot("GxMcpHub-ownerlease-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        var shutdownStarted = 0;
        var lookups = 0;
        var observed = new List<bool>();

        ListenerLookup Lookup(Uri uri)
        {
            if (Volatile.Read(ref shutdownStarted) == 0)
            {
                return ListenerInspector.Find(uri);
            }

            var number = Interlocked.Increment(ref lookups);
            if (number == 1)
            {
                // The first look of the decision, before the lease.
                return ListenerInspector.Find(uri);
            }

            // After Stop: the listener "is still there" for five looks, then it is gone for real.
            observed.Add(LeaseIsHeld(uri));
            return number <= 6 && !wrapper ? ListenerLookup.Found(424242, DateTime.UtcNow) : ListenerInspector.Find(uri);
        }

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true, wrapper: wrapper));
            var hub = NewHub(profilePath, script, Lookup);
            await hub.StartAsync(CancellationToken.None);
            var backend = hub.StatusJson()["backends"]?[0];
            var rootId = backend?["processId"]?.GetValue<int>() ?? 0;
            var listenerId = backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
            if (rootId == 0)
            {
                return false;
            }

            children.Add(rootId);
            if (listenerId != 0)
            {
                children.Add(listenerId);
            }

            Volatile.Write(ref shutdownStarted, 1);
            await hub.DisposeAsync();

            var released = !LeaseIsHeld(endpoint);
            var nobodyListens = ListenerInspector.Find(endpoint) == ListenerLookup.NotFound;
            var gone = children.All(id => !IsAlive(id));
            if (wrapper)
            {
                // The process that listened was the grandchild: nothing is left of it when the lease is let go.
                return released && nobodyListens && gone;
            }

            // Five looks that still found a listener, and one that did not, all with the lease held.
            return released && nobodyListens && gone && observed.Count >= 6 && observed.All(held => held);
        }
        catch
        {
            return false;
        }
        finally
        {
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> StartedInJobFollowsTheSystemTestAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var root = NewTempRoot("GxMcpHub-injob-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript
        {
            Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
        };
        var children = new List<int>();
        HubService? owner = null;
        HubService? other = null;
        var foreign = 0;

        // For a moment, another process (this one) is what listens on the port.
        ListenerLookup Lookup(Uri uri) =>
            Volatile.Read(ref foreign) == 1
                ? ListenerLookup.Found(Environment.ProcessId, DateTime.UtcNow)
                : ListenerInspector.Find(uri);

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
            owner = NewHub(profilePath, script, Lookup);
            // Nothing started yet: nothing to say.
            var beforeStart = owner.StatusJson()["backends"]?[0]?["gateway"];
            var nullBefore = beforeStart is not null && beforeStart.AsObject().ContainsKey("startedInJob") &&
                             beforeStart["startedInJob"] is null;

            await owner.StartAsync(CancellationToken.None);
            var started = owner.StatusJson()["backends"]?[0];
            var processId = started?["processId"]?.GetValue<int>() ?? 0;
            if (processId == 0)
            {
                return false;
            }

            children.Add(processId);

            // What the system says about that very process, asked here and not through the Hub.
            using var child = Process.GetProcessById(processId);
            var inJob = NativeHandles.IsProcessInJob(child.Handle, IntPtr.Zero, out var system) ? system : (bool?)null;
            var reported = started?["gateway"]?["startedInJob"];
            var matches = inJob is bool expected && reported is not null && reported.GetValue<bool>() == expected;

            // A process this Hub started, while another process holds the port, is not "its Gateway": nothing is said.
            Volatile.Write(ref foreign, 1);
            var lost = owner.StatusJson()["backends"]?[0];
            Volatile.Write(ref foreign, 0);
            var nullForLost = Flag(lost?["startedByHub"]) == true &&
                              Flag(lost?["gateway"]?["startedByThisHub"]) == false &&
                              lost?["gateway"]?.AsObject().ContainsKey("startedInJob") == true &&
                              lost["gateway"]!["startedInJob"] is null &&
                              lost["gateway"]!.AsObject().ContainsKey("startMode") &&
                              lost["gateway"]!["startMode"] is null;

            // A Hub that only connected to a Gateway it did not start says nothing about it.
            other = NewHub(profilePath, script);
            await other.StartAsync(CancellationToken.None);
            var connectOnly = other.StatusJson()["backends"]?[0]?["gateway"];
            var nullForOthers = Flag(connectOnly?["startedByThisHub"]) == false &&
                                connectOnly!.AsObject().ContainsKey("startedInJob") &&
                                connectOnly["startedInJob"] is null;

            return nullBefore && matches && nullForLost && nullForOthers && await StartedInJobEndsWithTheProcessAsync();
        }
        catch
        {
            return false;
        }
        finally
        {
            if (other is not null)
            {
                await other.DisposeAsync();
            }

            if (owner is not null)
            {
                await owner.DisposeAsync();
            }

            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    // What the supervisor says about the process it started describes that process while the supervisor has it: once it
    // is stopped, nothing is said.
    private static async Task<bool> StartedInJobEndsWithTheProcessAsync()
    {
        var port = ReserveFreePort();
        var (command, prefix) = SelfTestChildCommand();
        var backend = new BackendConfig
        {
            Id = "injob",
            Enabled = true,
            Startup = new StartupConfig
            {
                Command = command,
                Arguments = $"{prefix}{SelfTestListener.Switch} {port} 60",
                WorkingDirectory = Environment.CurrentDirectory
            }
        };
        await using var supervisor = new ProcessSupervisor(backend);
        try
        {
            using var self = Process.GetCurrentProcess();
            var supported = ProcessSupervisor.IsInJob(self) is not null;
            supervisor.Start();
            var running = supervisor.StartedByHub && (!supported || supervisor.StartedInJob is not null);
            supervisor.Stop();
            return running && !supervisor.StartedByHub && supervisor.StartedInJob is null;
        }
        catch
        {
            return false;
        }
        finally
        {
            supervisor.Stop();
        }
    }

    // A "Hub" started inside a real job object, as the clients start theirs. The first job kills what is in it when it
    // is closed; the Gateway the Hub starts is in the same job and the field says so. The second lets its processes
    // start others outside of it (silent breakaway), as a client of that kind does: the field is whatever the system
    // says of that process, and the process is not in that job.
    private static async Task<bool> StartedInJobInsideRealJobsTestAsync()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
        {
            return true;
        }

        var killing = await RunHubInJobAsync(NativeHandles.JobLimitKillOnJobClose);
        var breakaway = await RunHubInJobAsync(NativeHandles.JobLimitKillOnJobClose | NativeHandles.JobLimitSilentBreakawayOk);
        if (killing is null || breakaway is null)
        {
            // The system did not let this process make or use a job here: say so, and do not pretend it was proven.
            Console.Error.WriteLine("[self-test] startedInJob dentro de job: ambiente não permitiu criar o job de teste; não provado aqui.");
            return true;
        }

        return killing.Value.Reported == "true" && killing.Value.ChildInTestJob &&
               breakaway.Value.Reported == (breakaway.Value.ChildInAnyJob ? "true" : "false") &&
               !breakaway.Value.ChildInTestJob;
    }

    private static async Task<(string Reported, bool ChildInTestJob, bool ChildInAnyJob)?> RunHubInJobAsync(uint limitFlags)
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
            var (command, prefix) = SelfTestChildCommand();
            helper = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = $"{prefix}{SelfTestListener.JobReportSwitch} {port} 60",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (helper is null)
            {
                return null;
            }

            // Right after the start: the helper takes far longer than this to start its own child.
            if (!NativeHandles.AssignProcessToJobObject(job, helper.Handle))
            {
                return null;
            }

            helper.ErrorDataReceived += (_, _) => { };
            helper.BeginErrorReadLine();
            var line = helper.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(30))) != line ||
                line.Result is not string text ||
                !text.StartsWith(SelfTestListener.JobReportReady, StringComparison.Ordinal))
            {
                return null;
            }

            var parts = text[SelfTestListener.JobReportReady.Length..].Split(' ');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var childId))
            {
                return null;
            }

            using var child = Process.GetProcessById(childId);
            var inTestJob = NativeHandles.IsProcessInJob(child.Handle, job, out var inside) && inside;
            var inAnyJob = NativeHandles.IsProcessInJob(child.Handle, IntPtr.Zero, out var any) && any;
            return (parts[0], inTestJob, inAnyJob);
        }
        catch
        {
            return null;
        }
        finally
        {
            // Closing the job takes everything in it (the helper and the child it started), when it was made to.
            NativeHandles.CloseHandle(job);
            try
            {
                helper?.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }

            helper?.Dispose();
        }
    }

    private static async Task<bool> ExitLogRecordsEachDecisionTestAsync()
    {
        static string[] LogOf(string root)
        {
            var path = Path.Combine(root, "GxMcpHub", "runtime", "hub-exit.log");
            return File.Exists(path) ? File.ReadAllLines(path).Where(line => line.Length > 0).ToArray() : Array.Empty<string>();
        }

        // hub=<pid> backend=native decision=<d> gateway=<pid or -> [others=<n>] [mode=<how the Gateway was started>],
        // with no path in it. The mode is the last field, and only for a Hub that started the Gateway (not-owner has none).
        static bool Is(string line, string decision, bool withGateway, string? others, string? mode) =>
            line.Contains($" hub={Environment.ProcessId} ", StringComparison.Ordinal) &&
            line.Contains(" backend=native ", StringComparison.Ordinal) &&
            line.Contains($" decision={decision} ", StringComparison.Ordinal) &&
            (withGateway ? !line.Contains("gateway=- ", StringComparison.Ordinal) && !line.EndsWith("gateway=-", StringComparison.Ordinal)
                         : line.Contains("gateway=-", StringComparison.Ordinal)) &&
            (others is null ? !line.Contains("others=", StringComparison.Ordinal)
                            : line.Contains(" others=" + others, StringComparison.Ordinal)) &&
            (mode is null ? !line.Contains("mode=", StringComparison.Ordinal) && (others is null || line.EndsWith(" others=" + others, StringComparison.Ordinal))
                          : line.EndsWith(" mode=" + mode, StringComparison.Ordinal) &&
                            (others is null || line.EndsWith(" others=" + others + " mode=" + mode, StringComparison.Ordinal))) &&
            !line.Contains('\\') && !line.Contains('/') &&
            DateTime.TryParse(
                line[..line.IndexOf(' ')],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var at) &&
            at.Kind == DateTimeKind.Utc && Math.Abs((DateTime.UtcNow - at).TotalMinutes) < 5;

        async Task<bool> ScenarioAsync(Func<string, string, int, Uri, BackendScript, Task<bool>> run)
        {
            var root = NewTempRoot("GxMcpHub-exitlog-test-");
            var profilePath = Path.Combine(root, "profile.json");
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            try
            {
                WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true));
                return await run(root, profilePath, port, endpoint, script);
            }
            catch
            {
                return false;
            }
            finally
            {
                KillLeftovers(ProcessesOn(endpoint));
                DeleteQuietly(root);
            }
        }

        // The owner alone: the Gateway is stopped.
        var stopped = await ScenarioAsync(async (root, profile, _, _, script) =>
        {
            var hub = NewHub(profile, script);
            await hub.StartAsync(CancellationToken.None);
            var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            await hub.DisposeAsync();
            var lines = LogOf(root);
            return pid != 0 && lines.Length == 1 && Is(lines[0], "stopped", true, null, "hub-tree") &&
                   lines[0].Contains($"gateway={pid}", StringComparison.Ordinal);
        });

        // The owner with another Hub connected keeps the Gateway; the other one has nothing of its own to decide.
        var kept = await ScenarioAsync(async (root, profile, _, _, script) =>
        {
            var owner = NewHub(profile, script);
            await owner.StartAsync(CancellationToken.None);
            var other = NewHub(profile, script);
            await other.StartAsync(CancellationToken.None);
            await owner.DisposeAsync();
            await other.DisposeAsync();
            var lines = LogOf(root);
            return lines.Length == 2 && Is(lines[0], "kept", true, "1", "hub-tree") && Is(lines[1], "not-owner", false, null, null);
        });

        // The mode is how the Hub started the Gateway it is leaving: by the launcher outside the job of the client,
        // or the usual way after the launcher failed. The Hub that only connects has no mode to tell.
        var viaLauncher = await ScenarioAsync(async (root, profile, _, _, script) =>
        {
            var owner = NewHub(
                profile,
                script,
                outsideJob: NewStarter(root, OutsideJobPolicy.Always, StartLauncherNormally));
            await owner.StartAsync(CancellationToken.None);
            var ownerPid = owner.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            var visitor = NewHub(profile, script);
            await visitor.StartAsync(CancellationToken.None);
            await owner.DisposeAsync();
            await visitor.DisposeAsync();
            var lines = LogOf(root);
            return ownerPid != 0 && lines.Length == 2 &&
                   Is(lines[0], "kept", true, "1", "outside-job") &&
                   lines[0].Contains($" gateway={ownerPid} ", StringComparison.Ordinal) &&
                   Is(lines[1], "not-owner", false, null, null);
        });
        var launcherAlone = await ScenarioAsync(async (root, profile, _, _, script) =>
        {
            var hub = NewHub(
                profile,
                script,
                outsideJob: NewStarter(root, OutsideJobPolicy.Always, StartLauncherNormally));
            await hub.StartAsync(CancellationToken.None);
            var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            await hub.DisposeAsync();
            var lines = LogOf(root);
            return pid != 0 && lines.Length == 1 && Is(lines[0], "stopped", true, null, "outside-job") &&
                   lines[0].Contains($" gateway={pid} ", StringComparison.Ordinal) && WaitForExit(pid, TimeSpan.FromSeconds(10));
        });
        var fellBack = await ScenarioAsync(async (root, profile, _, _, script) =>
        {
            var hub = NewHub(
                profile,
                script,
                outsideJob: NewStarter(root, OutsideJobPolicy.Always, (_, _) => new WmiCreateOutcome(false, 0, "wmi-refused-2")));
            await hub.StartAsync(CancellationToken.None);
            await hub.DisposeAsync();
            var lines = LogOf(root);
            return lines.Length == 1 && Is(lines[0], "stopped", true, null, "hub-tree-fallback");
        });

        // The startup lease is busy for longer than the Hub waits on its way out.
        var lease = await ScenarioAsync(async (root, profile, _, endpoint, script) =>
        {
            var hub = NewHub(profile, script);
            hub.ExitLeaseWait = TimeSpan.FromMilliseconds(300);
            await hub.StartAsync(CancellationToken.None);
            using (await BackendStartupLease.AcquireAsync(new[] { endpoint }, TimeSpan.FromSeconds(10), CancellationToken.None))
            {
                await hub.DisposeAsync();
            }

            var lines = LogOf(root);
            return lines.Length == 1 && Is(lines[0], "left-for-lease", true, null, "hub-tree");
        });

        // The question about the other Hubs cannot be asked.
        var broken = 0;
        var failed = await ScenarioAsync(async (root, profile, _, _, script) =>
        {
            ListenerLookup Lookup(Uri uri) =>
                Volatile.Read(ref broken) == 1
                    ? throw new InvalidOperationException("tabela de listeners indisponível (falha sintética)")
                    : ListenerInspector.Find(uri);
            var hub = NewHub(profile, script, Lookup);
            await hub.StartAsync(CancellationToken.None);
            Volatile.Write(ref broken, 1);
            await hub.DisposeAsync();
            var lines = LogOf(root);
            return lines.Length == 1 && Is(lines[0], "error", true, null, "hub-tree");
        });

        // The previous behaviour, asked for with the switch: the Gateway is stopped whoever else is connected.
        var escape = await ScenarioAsync(async (root, profile, _, _, script) =>
        {
            var owner = NewHub(profile, script, keepSharedGateway: false);
            await owner.StartAsync(CancellationToken.None);
            var other = NewHub(profile, script, keepSharedGateway: false);
            await other.StartAsync(CancellationToken.None);
            var pid = owner.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            await owner.DisposeAsync();
            await other.DisposeAsync();
            var lines = LogOf(root);
            return pid != 0 && lines.Length == 2 &&
                   Is(lines[0], "stopped", true, null, "hub-tree") && lines[0].Contains($"gateway={pid}", StringComparison.Ordinal) &&
                   Is(lines[1], "not-owner", false, null, null);
        });

        // A process of this Hub that serves nobody (a wrapper whose Gateway is gone) is stopped, and the record says so.
        var nobody = await NobodyListensScenarioAsync();

        return stopped && kept && viaLauncher && launcherAlone && fellBack && lease && failed && escape && nobody;
    }

    private static async Task<bool> NobodyListensScenarioAsync()
    {
        var root = NewTempRoot("GxMcpHub-exitlog-nobody-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        var script = new BackendScript();
        var children = new List<int>();
        HubService? hub = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true, wrapper: true));
            hub = NewHub(profilePath, script);
            await hub.StartAsync(CancellationToken.None);
            var backend = hub.StatusJson()["backends"]?[0];
            var wrapperPid = backend?["processId"]?.GetValue<int>() ?? 0;
            var listenerPid = backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
            if (wrapperPid == 0 || listenerPid == 0 || listenerPid == wrapperPid)
            {
                return false;
            }

            children.Add(wrapperPid);
            children.Add(listenerPid);
            KillProcess(listenerPid);
            if (!await WaitUntilAsync(
                    () => ListenerInspector.Find(endpoint).State == ListenerLookupState.NotFound,
                    TimeSpan.FromSeconds(10)))
            {
                return false;
            }

            await hub.DisposeAsync();
            hub = null;
            var path = Path.Combine(root, "GxMcpHub", "runtime", "hub-exit.log");
            var lines = File.Exists(path) ? File.ReadAllLines(path).Where(line => line.Length > 0).ToArray() : Array.Empty<string>();
            return lines.Length == 1 &&
                   lines[0].Contains(" backend=native decision=stopped ", StringComparison.Ordinal) &&
                   lines[0].Contains($" gateway={wrapperPid}", StringComparison.Ordinal) &&
                   !lines[0].Contains("others=", StringComparison.Ordinal) &&
                   lines[0].EndsWith($" gateway={wrapperPid} mode=hub-tree", StringComparison.Ordinal) &&
                   WaitForExit(wrapperPid, TimeSpan.FromSeconds(10));
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hub is not null)
            {
                await hub.DisposeAsync();
            }

            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    // The Gateways of a test, by the process that listens on its port, for the cleanup of a scenario.
    private static IEnumerable<int> ProcessesOn(Uri endpoint)
    {
        var listener = ListenerInspector.Find(endpoint);
        return listener.ProcessId is int id ? new[] { id } : Array.Empty<int>();
    }

    private static async Task<bool> ExitLogRotatesAndNeverBreaksTheExitTestAsync()
    {
        var root = NewTempRoot("GxMcpHub-exitlogfile-test-");
        try
        {
            // Formatting: only ids and numbers, even when the id of the backend is hostile.
            var decision = new ShutdownDecision(BackendRuntime.DecisionKept, 4321, 2);
            var line = HubExitLog.Format(
                new DateTime(2026, 11, 16, 14, 3, 11, DateTimeKind.Utc),
                99,
                "C:\\perfil secreto\\x y" + new string('z', 40),
                decision);
            // The hostile id is kept to letters, digits, dots, dashes and underscores, and to 32 characters.
            var sanitized = HubExitLog.MaxBytes == 256 * 1024 &&
                            line.Contains("backend=C__perfil_secreto_x_y" + new string('z', 11) + " ", StringComparison.Ordinal) &&
                            HubExitLog.Format(DateTime.UtcNow, 1, string.Empty, decision).Contains(" backend=- ", StringComparison.Ordinal) &&
                            line.EndsWith("decision=kept gateway=4321 others=2", StringComparison.Ordinal) &&
                            line.StartsWith("2026-11-16T14:03:11", StringComparison.Ordinal) &&
                            !line.Contains('\\') && !line.Contains('/');
            var unknown = HubExitLog.Format(DateTime.UtcNow, 1, "native", new ShutdownDecision(BackendRuntime.DecisionKept, 5, null))
                .EndsWith(" others=?", StringComparison.Ordinal);
            var noOthers = !HubExitLog.Format(DateTime.UtcNow, 1, "native", new ShutdownDecision(BackendRuntime.DecisionStopped, 5, null))
                .Contains("others=", StringComparison.Ordinal);
            var notOwner = HubExitLog.Format(DateTime.UtcNow, 1, "native", new ShutdownDecision(BackendRuntime.DecisionNotOwner, null, null))
                .EndsWith(" decision=not-owner gateway=-", StringComparison.Ordinal);

            // The start mode is the last field of every line of a Hub that started the Gateway, after others= when
            // there is one, and a line without a Gateway of this Hub has none.
            var at = new DateTime(2026, 11, 16, 14, 3, 11, DateTimeKind.Utc);
            var modeLast =
                HubExitLog.Format(at, 99, "native", new ShutdownDecision(BackendRuntime.DecisionKept, 4321, 2, StartModes.OutsideJob))
                    .EndsWith("decision=kept gateway=4321 others=2 mode=outside-job", StringComparison.Ordinal) &&
                HubExitLog.Format(at, 99, "native", new ShutdownDecision(BackendRuntime.DecisionKept, 4321, null, StartModes.HubTree))
                    .EndsWith("decision=kept gateway=4321 others=? mode=hub-tree", StringComparison.Ordinal) &&
                HubExitLog.Format(at, 99, "native", new ShutdownDecision(BackendRuntime.DecisionStopped, 5, null, StartModes.HubTree))
                    .EndsWith("decision=stopped gateway=5 mode=hub-tree", StringComparison.Ordinal) &&
                HubExitLog.Format(at, 99, "native", new ShutdownDecision(BackendRuntime.DecisionLeftForLease, 5, null, StartModes.HubTreeFallback))
                    .EndsWith("decision=left-for-lease gateway=5 mode=hub-tree-fallback", StringComparison.Ordinal) &&
                HubExitLog.Format(at, 99, "native", new ShutdownDecision(BackendRuntime.DecisionError, 5, null, StartModes.OutsideJob))
                    .EndsWith("decision=error gateway=5 mode=outside-job", StringComparison.Ordinal) &&
                !HubExitLog.Format(at, 99, "native", new ShutdownDecision(BackendRuntime.DecisionNotOwner, null, null))
                    .Contains("mode=", StringComparison.Ordinal) &&
                !HubExitLog.Format(at, 99, "native", new ShutdownDecision(BackendRuntime.DecisionStopped, 5, null))
                    .Contains("mode=", StringComparison.Ordinal);

            // Rotation: at the limit the file becomes .1, replacing the one that was there, and a new one starts.
            // Ten lines of ten bytes fill the 100-byte limit: 35 lines rotate three times (before the lines 10, 20 and 30).
            var path = Path.Combine(root, "runtime", "hub-exit.log");
            for (var index = 0; index < 35; index++)
            {
                HubExitLog.Append(path, "linha " + index.ToString("D2"), maxBytes: 100);
            }

            var current = File.ReadAllText(path);
            var previous = File.ReadAllText(path + ".1");
            var rotated = File.Exists(path + ".1") &&
                          !File.Exists(path + ".2") &&
                          new FileInfo(path).Length == 50 &&
                          current.Contains("linha 30", StringComparison.Ordinal) &&
                          current.Contains("linha 34", StringComparison.Ordinal) &&
                          !current.Contains("linha 29", StringComparison.Ordinal) &&
                          // The one before holds the last full round only: the earlier ones were replaced, not accumulated.
                          previous.Contains("linha 20", StringComparison.Ordinal) &&
                          previous.Contains("linha 29", StringComparison.Ordinal) &&
                          !previous.Contains("linha 19", StringComparison.Ordinal) &&
                          !previous.Contains("linha 00", StringComparison.Ordinal);

            // Whatever fails, nothing is thrown: the folder cannot be made (a file in its place), and the file
            // cannot be written (a folder in its place).
            var blocked = Path.Combine(root, "arquivo");
            File.WriteAllText(blocked, "x");
            HubExitLog.Append(Path.Combine(blocked, "runtime", "hub-exit.log"), "linha");
            var folderAsFile = Path.Combine(root, "pasta", "hub-exit.log");
            Directory.CreateDirectory(folderAsFile);
            HubExitLog.Append(folderAsFile, "linha");
            var neverThrows = true;

            // And the exit of a Hub does not depend on it: the log cannot be written, and the Gateway is stopped all the same.
            var port = ReserveFreePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var script = new BackendScript
            {
                Reachable = () => ListenerInspector.Find(endpoint).State == ListenerLookupState.Found
            };
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "GxMcpHub"), "um arquivo no lugar da pasta");
            var profilePath = Path.Combine(workspace, "profile.json");
            WriteProfile(profilePath, TestProfile(workspace, port, "native", withStartup: true));
            var hub = NewHub(profilePath, script);
            await hub.StartAsync(CancellationToken.None);
            var pid = hub.StatusJson()["backends"]?[0]?["processId"]?.GetValue<int>() ?? 0;
            await hub.DisposeAsync();
            var exitUnaffected = pid != 0 && WaitForExit(pid, TimeSpan.FromSeconds(10)) &&
                                 !File.Exists(Path.Combine(workspace, "GxMcpHub", "runtime", "hub-exit.log"));

            return sanitized && unknown && noOthers && notOwner && modeLast && rotated && neverThrows && exitUnaffected;
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static async Task<bool> OwnProcessThatServesNobodyIsStoppedOnTheWayOutTestAsync() =>
        await OwnProcessThatServesNobodyScenarioAsync(anotherProcessTookThePort: false) &&
        await OwnProcessThatServesNobodyScenarioAsync(anotherProcessTookThePort: true);

    private static async Task<bool> OwnProcessThatServesNobodyScenarioAsync(
        bool anotherProcessTookThePort,
        bool keepGatewayWhenAlone = false)
    {
        var root = NewTempRoot("GxMcpHub-nobody-test-");
        var profilePath = Path.Combine(root, "profile.json");
        var port = ReserveFreePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        // Sem "Reachable": o outro Hub segue se achando conectado, com a presença registrada.
        var script = new BackendScript();
        var children = new List<int>();
        HubService? owner = null;
        TcpListener? foreign = null;

        try
        {
            WriteProfile(profilePath, TestProfile(root, port, "native", withStartup: true, wrapper: true));
            owner = NewHub(profilePath, script, keepGatewayWhenAlone: keepGatewayWhenAlone);
            await owner.StartAsync(CancellationToken.None);
            var backend = owner.StatusJson()["backends"]?[0];
            var wrapperPid = backend?["processId"]?.GetValue<int>() ?? 0;
            var listenerPid = backend?["gateway"]?["listenerProcessId"]?.GetValue<int>() ?? 0;
            if (wrapperPid == 0 || listenerPid == 0 || listenerPid == wrapperPid)
            {
                return false;
            }
            children.Add(wrapperPid);
            children.Add(listenerPid);

            await using var other = NewHub(profilePath, script);
            await other.StartAsync(CancellationToken.None);

            // O Gateway cai e o wrapper deste Hub segue vivo; num dos casos outro processo toma a porta.
            KillProcess(listenerPid);
            if (anotherProcessTookThePort)
            {
                foreign = new TcpListener(IPAddress.Loopback, port);
                foreign.Start();
            }

            var registered = OtherHubs(owner) == 1 && IsAlive(wrapperPid);

            // O wrapper não é o Gateway de ninguém: é encerrado como antes, sem depender da presença do outro Hub.
            bool silent;
            using (var stderr = new StderrCapture())
            {
                await owner.DisposeAsync();
                owner = null;
                silent = stderr.Count("Gateway mantido em execução") == 0;
            }

            return registered && silent && WaitForExit(wrapperPid, TimeSpan.FromSeconds(10));
        }
        catch
        {
            return false;
        }
        finally
        {
            foreign?.Stop();
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }
            KillLeftovers(children);
            DeleteQuietly(root);
        }
    }

    // Segura a trava como quem promove: o arquivo aberto sem compartilhamento.
    private static FileStream HoldPromotionLock(string root)
    {
        var path = PromotionLock.PathFor(new HubConfig { WorkspaceRoot = root });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Apoio dos testes da 0.2.0.
    // ------------------------------------------------------------------------------------------------------------

    // A entrada do transporte nos testes: uma linha por mensagem, fim de arquivo quando o teste encerra.
    private sealed class ChannelLineReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Send(string line) => _lines.Writer.TryWrite(line);

        public void Complete() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    // A saída do transporte nos testes: cada mensagem escrita vira uma linha consultável de outra thread.
    private sealed class CapturingWriter : TextWriter
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public override Encoding Encoding => Encoding.UTF8;

        public IReadOnlyCollection<string> Lines => _lines.ToArray();

        public override void WriteLine(string? value) => _lines.Enqueue(value ?? string.Empty);

        public override Task WriteLineAsync(string? value)
        {
            _lines.Enqueue(value ?? string.Empty);
            return Task.CompletedTask;
        }
    }

    private const string ProfileMarkVariable = "GXHUB_SELFTEST_MARK";

    private static HubService NewHub(
        string profilePath,
        BackendScript script,
        Func<Uri, ListenerLookup>? listenerLookup = null,
        bool autoReconcile = true,
        Func<string, LoadedHubConfig>? profileLoader = null,
        LoadedHubConfig? loaded = null,
        bool? keepSharedGateway = true,
        OutsideJobStarter? outsideJob = null,
        bool? keepGatewayWhenAlone = false) =>
        new(
            loaded ?? HubConfigLoader.Load(profilePath),
            (hubConfig, backendConfig) => new BackendRuntime(
                hubConfig,
                backendConfig,
                _ => script.NewHandler(),
                listenerLookup),
            autoReconcile,
            profileLoader,
            keepSharedGateway,
            // The tests start what they start the usual way, whatever job the suite runs in, unless one asks otherwise.
            outsideJob ?? new OutsideJobStarter(OutsideJobPolicy.Never),
            // Off unless a test asks: the suite does not depend on the environment it runs in.
            keepGatewayWhenAlone);

    // "mark" distingue uma versão do perfil de teste da outra. Com bloco de startup ela vai no ambiente do startup,
    // que é o que uma promoção reescreve e a recarga automática aplica sozinha; sem ele, é o nome de exibição, que
    // só o gxhub_refresh aplica.
    private static JsonObject TestProfile(
        string root,
        int port,
        string mark,
        bool withStartup,
        string profileName = "reconcile-test",
        int? childPort = null,
        int startupTimeoutSeconds = 20,
        int connectTimeoutSeconds = 2,
        bool wrapper = false)
    {
        var backend = new JsonObject
        {
            ["id"] = "native",
            ["displayName"] = withStartup ? "native" : mark,
            ["kind"] = "fixed",
            ["enabled"] = true,
            ["toolPrefix"] = "native_",
            ["protocolVersion"] = "2025-11-25",
            ["endpoint"] = $"http://127.0.0.1:{port}/mcp",
            ["useRemoteReadOnlyAnnotations"] = true
        };
        if (withStartup)
        {
            // No lugar do Gateway, o próprio executável do Hub num modo que só escuta a porta por um tempo limitado;
            // com wrapper, num modo que inicia esse processo e fica vivo, como um script que lança o Gateway.
            var (command, argumentPrefix) = SelfTestChildCommand();
            var mode = wrapper ? SelfTestListener.WrapperSwitch : SelfTestListener.Switch;
            backend["startup"] = new JsonObject
            {
                ["command"] = command,
                ["arguments"] = $"{argumentPrefix}{mode} {childPort ?? port} 120",
                ["workingDirectory"] = root,
                ["startupTimeoutSeconds"] = startupTimeoutSeconds,
                ["environment"] = new JsonObject { [ProfileMarkVariable] = mark }
            };
        }

        return new JsonObject
        {
            ["profileName"] = profileName,
            ["workspaceRoot"] = root,
            ["knowledgeBasePath"] = root,
            ["connectTimeoutSeconds"] = connectTimeoutSeconds,
            ["callTimeoutSeconds"] = 30,
            ["writePolicy"] = new JsonObject
            {
                ["mode"] = "read-only",
                ["hideBlockedTools"] = true
            },
            ["backends"] = new JsonArray { backend }
        };
    }

    // A marca do perfil que o Hub tem carregado agora.
    private static string MarkOf(HubService hub)
    {
        var backend = hub.Config.Backends[0];
        return backend.Startup is not null &&
               backend.Startup.Environment.TryGetValue(ProfileMarkVariable, out var mark)
            ? mark
            : backend.DisplayName;
    }

    // O que o Hub escreve no stderr enquanto o teste roda, repassado ao stderr de verdade.
    private sealed class StderrCapture : TextWriter
    {
        private readonly TextWriter _previous;
        private readonly StringBuilder _text = new();

        public StderrCapture()
        {
            _previous = Console.Error;
            Console.SetError(this);
        }

        public override Encoding Encoding => _previous.Encoding;

        public override void Write(char value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
            _previous.Write(value);
        }

        public override void Write(string? value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
            _previous.Write(value);
        }

        public override void WriteLine(string? value)
        {
            lock (_text)
            {
                _text.Append(value).Append('\n');
            }
            _previous.WriteLine(value);
        }

        public string Text()
        {
            lock (_text)
            {
                return _text.ToString();
            }
        }

        public int Count(string fragment)
        {
            string text;
            lock (_text)
            {
                text = _text.ToString();
            }

            var count = 0;
            for (var at = text.IndexOf(fragment, StringComparison.Ordinal);
                 at >= 0;
                 at = text.IndexOf(fragment, at + fragment.Length, StringComparison.Ordinal))
            {
                count++;
            }
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Console.SetError(_previous);
            }
            base.Dispose(disposing);
        }
    }

    private static (string Command, string ArgumentPrefix) SelfTestChildCommand()
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Executável do Hub indisponível.");
        var assemblyPath = typeof(SelfTests).Assembly.Location;
        var hostedByDotnet = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return hostedByDotnet && !string.IsNullOrEmpty(assemblyPath)
            ? (processPath, $"\"{assemblyPath}\" ")
            : (processPath, string.Empty);
    }

    private static void WriteProfile(string path, JsonObject profile) =>
        File.WriteAllText(path, profile.ToJsonString());

    private static string NewTempRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteQuietly(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Antivírus pode atrasar a liberação da pasta temporária.
        }
    }

    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    private static bool? Flag(JsonNode? node) => node?.GetValue<bool>();

    private static string? ResultText(JsonNode? result) => result?["content"]?[0]?["text"]?.GetValue<string>();

    private static bool IsError(JsonNode? result) => result?["isError"]?.GetValue<bool>() == true;

    private static string[] BackendToolNames(HubService hub) =>
        hub.GetExposedToolDefinitions()
            .Select(definition => definition["name"]?.GetValue<string>() ?? string.Empty)
            .Where(name => name.StartsWith("native_", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool WaitForExit(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(processId))
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return !IsAlive(processId);
    }

    // Só para processos que o próprio teste mandou o Hub iniciar.
    private static void KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit(10000);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void KillLeftovers(IEnumerable<int> processIds)
    {
        foreach (var processId in processIds)
        {
            if (IsAlive(processId))
            {
                KillProcess(processId);
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return condition();
    }

    // Um backend MCP simulado cujo roteiro o teste muda entre uma conexão e outra. Cada conexão do Hub cria um
    // handler novo; os contadores ficam no roteiro.
    private sealed class BackendScript
    {
        private int _clients;
        private int _initializeCalls;
        private int _toolCalls;
        private int _listFailures;
        private int _initializeFailures;

        public string[] Tools { get; set; } = { "test_tool" };
        public Func<bool>? Reachable { get; init; }
        public TaskCompletionSource<bool>? CallEntered { get; init; }
        public Task? CallGate { get; init; }
        public Task? InitializeGate { get; set; }
        public int Clients => Volatile.Read(ref _clients);
        public int InitializeCalls => Volatile.Read(ref _initializeCalls);
        public int ToolCalls => Volatile.Read(ref _toolCalls);

        public HttpMessageHandler NewHandler()
        {
            Interlocked.Increment(ref _clients);
            return new ScriptedBackendHandler(this);
        }

        public void CountInitialize() => Interlocked.Increment(ref _initializeCalls);

        public void CountToolCall() => Interlocked.Increment(ref _toolCalls);

        // O próximo tools/list falha, uma vez: é o que o gxhub_status vê quando o backend para de responder.
        public void FailNextList() => Interlocked.Exchange(ref _listFailures, 1);

        // Os próximos initialize falham: um processo que escuta a porta e ainda não responde MCP.
        public void FailNextInitializes(int count) => Interlocked.Exchange(ref _initializeFailures, count);

        public bool TakeListFailure() => TakeOne(ref _listFailures);

        public bool TakeInitializeFailure() => TakeOne(ref _initializeFailures);

        private static bool TakeOne(ref int counter)
        {
            while (true)
            {
                var current = Volatile.Read(ref counter);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
                {
                    return true;
                }
            }
        }
    }

    private sealed class ScriptedBackendHandler : HttpMessageHandler
    {
        private readonly BackendScript _script;

        public ScriptedBackendHandler(BackendScript script)
        {
            _script = script;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_script.Reachable is not null && !_script.Reachable())
            {
                throw new HttpRequestException("conexão recusada (backend simulado fora do ar)");
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = JsonNode.Parse(body) as JsonObject
                          ?? throw new InvalidOperationException("Payload de teste inválido.");
            var method = payload["method"]?.GetValue<string>();

            switch (method)
            {
                case "initialize":
                    _script.CountInitialize();
                    if (_script.TakeInitializeFailure())
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("backend not ready")
                        };
                    }
                    if (_script.InitializeGate is Task initializeGate)
                    {
                        await initializeGate.WaitAsync(cancellationToken);
                    }
                    return JsonResponse(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = payload["id"]?.DeepClone(),
                        ["result"] = new JsonObject
                        {
                            ["protocolVersion"] = "2025-11-25",
                            ["capabilities"] = new JsonObject(),
                            ["serverInfo"] = new JsonObject { ["name"] = "scripted-test", ["version"] = "1.0.0" }
                        }
                    }, "scripted-session");
                case "notifications/initialized":
                    return new HttpResponseMessage(HttpStatusCode.Accepted)
                    {
                        Content = new StringContent(string.Empty)
                    };
                case "tools/list":
                    if (_script.TakeListFailure())
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("backend stopped")
                        };
                    }

                    var tools = new JsonArray();
                    foreach (var tool in _script.Tools)
                    {
                        tools.Add(new JsonObject
                        {
                            ["name"] = tool,
                            ["annotations"] = new JsonObject { ["readOnlyHint"] = true }
                        });
                    }
                    return JsonResponse(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = payload["id"]?.DeepClone(),
                        ["result"] = new JsonObject { ["tools"] = tools }
                    });
                case "tools/call":
                    _script.CountToolCall();
                    var name = payload["params"]?["name"]?.GetValue<string>();
                    _script.CallEntered?.TrySetResult(true);
                    if (_script.CallGate is not null)
                    {
                        await _script.CallGate.WaitAsync(cancellationToken);
                    }
                    if (name == "failing_tool")
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("backend failed")
                        };
                    }
                    return JsonResponse(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = payload["id"]?.DeepClone(),
                        ["result"] = new JsonObject
                        {
                            ["content"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = "ok:" + name
                                }
                            }
                        }
                    });
                default:
                    throw new InvalidOperationException($"Método inesperado no teste: {method}");
            }
        }

        private static HttpResponseMessage JsonResponse(JsonObject payload, string? sessionId = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                response.Headers.TryAddWithoutValidation("MCP-Session-Id", sessionId);
            }
            return response;
        }
    }

    private sealed class ExpiringSessionHandler : HttpMessageHandler
    {
        public int InitializeCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int ToolCalls { get; private set; }
        public bool ListUsedOldSession { get; private set; }
        public bool FirstCallUsedOldSession { get; private set; }
        public bool SecondCallUsedNewSession { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = JsonNode.Parse(body) as JsonObject
                          ?? throw new InvalidOperationException("Payload de teste inválido.");
            var method = payload["method"]?.GetValue<string>();

            if (method == "initialize")
            {
                InitializeCalls++;
                return JsonResponse(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = payload["id"]?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = "2025-11-25",
                        ["capabilities"] = new JsonObject(),
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "session-test",
                            ["version"] = "1.0.0"
                        }
                    }
                }, InitializeCalls == 1 ? "old-session" : "new-session");
            }

            if (method == "notifications/initialized")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(string.Empty)
                };
            }

            if (method == "tools/list")
            {
                ListCalls++;
                var sessionId = request.Headers.TryGetValues("MCP-Session-Id", out var values)
                    ? values.FirstOrDefault()
                    : null;
                ListUsedOldSession = sessionId == "old-session";
                return JsonResponse(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = payload["id"]?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["tools"] = new JsonArray
                        {
                            new JsonObject { ["name"] = "test_tool" }
                        }
                    }
                });
            }

            if (method == "tools/call")
            {
                ToolCalls++;
                var sessionId = request.Headers.TryGetValues("MCP-Session-Id", out var values)
                    ? values.FirstOrDefault()
                    : null;

                if (ToolCalls == 1)
                {
                    FirstCallUsedOldSession = sessionId == "old-session";
                    return JsonResponse(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = payload["id"]?.DeepClone(),
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32001,
                            ["message"] = "Unknown or expired MCP session."
                        }
                    });
                }

                SecondCallUsedNewSession = sessionId == "new-session";
                return JsonResponse(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = payload["id"]?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = "ok"
                            }
                        }
                    }
                });
            }

            throw new InvalidOperationException($"Método inesperado no teste: {method}");
        }

        private static HttpResponseMessage JsonResponse(JsonObject payload, string? sessionId = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                response.Headers.TryAddWithoutValidation("MCP-Session-Id", sessionId);
            }
            return response;
        }
    }

    private sealed class FailingHealthHandler : HttpMessageHandler
    {
        private int _listCalls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = JsonNode.Parse(body) as JsonObject
                          ?? throw new InvalidOperationException("Payload de teste inválido.");
            var method = payload["method"]?.GetValue<string>();

            if (method == "initialize")
            {
                return JsonResponse(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = payload["id"]?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = "2025-11-25",
                        ["capabilities"] = new JsonObject(),
                        ["serverInfo"] = new JsonObject { ["name"] = "health-test", ["version"] = "1.0.0" }
                    }
                }, "health-session");
            }

            if (method == "notifications/initialized")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(string.Empty)
                };
            }

            if (method == "tools/list" && Interlocked.Increment(ref _listCalls) == 1)
            {
                return JsonResponse(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = payload["id"]?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["tools"] = new JsonArray(new JsonObject { ["name"] = "test_tool" })
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("backend stopped")
            };
        }

        private static HttpResponseMessage JsonResponse(JsonObject payload, string? sessionId = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                response.Headers.TryAddWithoutValidation("MCP-Session-Id", sessionId);
            }
            return response;
        }
    }

    private sealed class StableBackendHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = JsonNode.Parse(body) as JsonObject
                          ?? throw new InvalidOperationException("Payload de teste inválido.");
            var method = payload["method"]?.GetValue<string>();

            if (method == "initialize")
            {
                return JsonResponse(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = payload["id"]?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = "2025-11-25",
                        ["capabilities"] = new JsonObject(),
                        ["serverInfo"] = new JsonObject { ["name"] = "stable-test", ["version"] = "1.0.0" }
                    }
                }, "stable-session");
            }

            if (method == "notifications/initialized")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(string.Empty)
                };
            }

            if (method == "tools/list")
            {
                return JsonResponse(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = payload["id"]?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["tools"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "test_tool",
                                ["annotations"] = new JsonObject { ["readOnlyHint"] = true }
                            }
                        }
                    }
                });
            }

            throw new InvalidOperationException($"Método inesperado no teste: {method}");
        }

        private static HttpResponseMessage JsonResponse(JsonObject payload, string? sessionId = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                response.Headers.TryAddWithoutValidation("MCP-Session-Id", sessionId);
            }
            return response;
        }
    }
}
