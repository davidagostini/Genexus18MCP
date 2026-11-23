# GxMcpHub

Camada local **opcional** entre um cliente de IA (qualquer cliente MCP por STDIO) e o Genexus18MCP. Cada Hub atende **uma KB** descrita por um perfil JSON e faz três coisas que o Gateway sozinho não faz:

1. **Escolhe a KB e a política de escrita por tarefa**, antes de o cliente ver qualquer ferramenta (`read-only`, `single-writer` com trava entre processos por KB, ou `open-writer`).
2. **Cuida do ciclo de vida do Gateway**: início sob demanda, compartilhamento entre sessões, recarga do perfil sem reiniciar a sessão, e (opcional) Gateway e Worker mantidos aquecidos entre chamadas.
3. **Diagnostica**: `gxhub_status`, `--doctor` e um registro de saída dizem qual Gateway atende, quem o iniciou e por quê.

É um executável .NET 8 para Windows. Não abre KB, não referencia o SDK do GeneXus e não muda nada no Gateway nem no Worker: quem abre a KB continua sendo o Worker do Genexus18MCP, e o Hub só fala com o Gateway por HTTP (MCP) e com o cliente por STDIO.

> Esta pasta é uma **proposta de inclusão**, descrita na issue de origem. Nada aqui é chamado pelo build, pelo CI nem pelos scripts do repositório; ela compila e testa sozinha.

## Por que existe

Quem usa o Genexus18MCP com várias KBs e várias sessões de IA ao mesmo tempo esbarra sempre nos mesmos problemas, e o Hub nasceu para resolvê-los:

| Problema | O que o Hub faz |
|---|---|
| Uma conversa pode acabar numa KB ou política que não era a da tarefa | O cliente inicia o Hub com **um** perfil (uma KB, uma porta, uma política). `gxhub_status` e `native_genexus_whoami` confirmam KB, versão e política antes da operação. |
| A política de escrita é uma convenção, não uma barreira | A política é aplicada **no Hub**, inclusive na chamada genérica `gxhub_call`, e pode esconder do cliente as ferramentas bloqueadas (menos tokens em `tools/list`). |
| Cada chamada avulsa (script, tarefa agendada, sessão nova) paga de novo o Gateway e a abertura da KB | O Gateway é **compartilhado** entre Hubs (presença por arquivo) e, com `GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE`, fica no ar com o Worker quando o último Hub sai. |
| Clientes que rodam o servidor MCP num *job* do Windows que encerra os filhos ao fechar levam o Gateway junto | Com `GXMCP_HUB_START_OUTSIDE_JOB=auto` o Hub inicia o Gateway **fora do job** quando o filho ficaria preso a ele. |
| Atualizar o pacote do Gateway derruba as sessões abertas | O Hub **recarrega o perfil** sozinho, reconecta quando o Gateway é substituído e inicia o backend na hora da chamada; uma trava de implantação impede que um backend suba pelo pacote antigo enquanto alguém promove o novo. |
| "Por que está lento / qual Gateway é esse?" | `gxhub_status` mostra listener, quem iniciou, modo de início, outros Hubs conectados e se o perfil em disco já foi aplicado; `hub-exit.log` registra a decisão de saída de cada Hub. |

### O que isso rendeu (medições do autor)

Uma máquina de desenvolvimento, de 16 a 23/11/2026, com amostras pequenas e o uso caindo no fim do período (vale a taxa, não o total):

- Hubs que **subiram e derrubaram** um Gateway ao sair, por chamada: **31%** com o Hub 0.4.0 (1.274 de 4.093) contra **0,4%** com o 0.5.0 (1 de 249, mais 5 Gateways deixados de propósito).
- Chamada avulsa pelo `scripts/call.ps1`: **12,4 s** quando a rota está fria (Gateway, Worker e KB) contra **0,9 s** (`gxhub_status`, `native_genexus_whoami`) a **1,7 s** (`native_genexus_list_objects`) com o Gateway e o Worker de pé.
- A partida a frio do Worker tem mediana de 8,2 s (Service Manager 3,1 s mais abertura da KB 5 s) e chega a 17 a 23 s na primeira partida depois de horas de máquina parada: evitá-la é o que o Gateway compartilhado e o aquecido compram.

## Início rápido

```powershell
# 1) compilar
dotnet build src/GxMcpHub/GxMcpHub.csproj -c Release

# 2) copiar e ajustar o perfil e a configuração de exemplo (KB, porta, caminhos)
#    src/GxMcpHub/profiles/profile.example.json   e   gateway.example.json

# 3) validar sem abrir a KB
src/GxMcpHub/bin/Release/net8.0/GxMcpHub.exe --config <seu-perfil.json> --doctor

# 4) registrar no cliente como servidor MCP STDIO:
#    comando  = GxMcpHub.exe
#    argumentos = --config <seu-perfil.json>
```

Chamadas avulsas, sem cliente MCP (recuperação de sessão, scripts):

```powershell
src/GxMcpHub/scripts/call.ps1 -Profile <seu-perfil.json> -Tool gxhub_status
src/GxMcpHub/scripts/call.ps1 -Profile <seu-perfil.json> -Tool native_genexus_whoami
```

`call.ps1` abre um Hub auxiliar por chamada e só deixa passar ferramentas de leitura; uma escrita exige `-AllowWrite`.

## Perfil

| Campo | Significado |
|---|---|
| `profileName`, `workspaceRoot`, `knowledgeBasePath` | Nome, raiz usada pelos marcadores `${WORKSPACE_ROOT}` e KB (precisa existir). Caminhos relativos valem a partir da pasta do perfil (`${CONFIG_DIR}`). |
| `connectTimeoutSeconds`, `callTimeoutSeconds` | Prazo de conexão e de cada chamada. |
| `writePolicy.mode` | `read-only`, `single-writer` (no máximo uma instância escritora por KB, com trava exclusiva entre processos) ou `open-writer` (sem trava: **não** protege contra gravação concorrente). |
| `writePolicy.writerBackend`, `hideBlockedTools` | Backend escritor e se as ferramentas bloqueadas somem do `tools/list` (padrão: somem). |
| `backends[]` | Um backend `fixed` por HTTP: `endpoint`, `toolPrefix` (padrão `native_`), `protocolVersion`, `useRemoteReadOnlyAnnotations`, `readOnlyTools`, `bearerTokenEnvVar` (nome da variável com o token, nunca o token) e `startup` (comando, argumentos, diretório, prazo e ambiente do Gateway). |

## Ferramentas do Hub

| Ferramenta | Para quê |
|---|---|
| `gxhub_status` | Estado do Hub, do perfil e de cada backend (listener, quem o iniciou, modo de início, outros Hubs, perfil aplicado ou pendente). |
| `gxhub_refresh` | Relê o perfil e reconecta. Só reinicia o Gateway do próprio Hub quando o perfil mudou no disco. |
| `gxhub_list_tools` | Ferramentas expostas, com a classificação (leitura ou escrita) que a política usa. |
| `gxhub_call` | Chamada genérica a uma ferramenta do backend, sempre sob a mesma política. |
| `native_*` | As ferramentas do Genexus18MCP, com o prefixo do backend. |

## Variáveis de ambiente do Hub

| Variável | Efeito |
|---|---|
| `GXMCP_HUB_AUTO_RECONCILE=0` | Desliga a reconciliação periódica e o início sob demanda do backend. |
| `GXMCP_HUB_KEEP_SHARED_GATEWAY=0` | Volta ao comportamento antigo: o Hub que iniciou o Gateway sempre o encerra ao sair. |
| `GXMCP_HUB_START_OUTSIDE_JOB` | `auto` (padrão), `always` ou `never`: se o Gateway é iniciado fora do job do cliente. |
| `GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE` | Opt-in (`1`, `true`, `on`, `yes`): o Hub que sai sem outro Hub conectado deixa o Gateway e o Worker no ar. O `scripts/call.ps1` liga sozinho para o Hub auxiliar. |

## Testes

```powershell
dotnet build src/GxMcpHub/GxMcpHub.csproj -c Release
src/GxMcpHub/bin/Release/net8.0/GxMcpHub.exe --self-test          # 98 verificações, sem KB, ~3 min
pwsh -NoProfile -File src/GxMcpHub/scripts/call.policy.tests.ps1   # política do call.ps1
pwsh -NoProfile -File src/GxMcpHub/scripts/smoke.ps1 -Profile <perfil.json> -NoBuild   # com um Gateway real
```

O `--self-test` cria processos, *jobs* e lançadores de verdade (inclusive por WMI) e os encerra por PID, instante de criação e caminho; numa máquina com EDR ele pode gerar telemetria ou alerta.

## Limites e compatibilidade

- Só Windows (jobs, WMI e tabela TCP do sistema). Alvo .NET 8; se preferirem alinhar ao Gateway (.NET 10), a mudança é de `TargetFramework`.
- O Hub não substitui as travas do Genexus18MCP (por exemplo a trava por KB do Worker): ele organiza **quem** inicia, mantém e encerra o Gateway.
- Um Gateway deixado no ar fica sem dono até a próxima implantação e o Worker segura a KB por até `WorkerIdleTimeoutMinutes` depois da última chamada; duas rotas que abrem a mesma KB não podem ter Worker ao mesmo tempo.
- O protocolo detalhado de cada recurso e os limites conhecidos estão em [arquitetura](docs/ARQUITETURA.md) e [segurança](docs/SEGURANCA.md).
