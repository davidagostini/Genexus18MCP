# Segurança do GxMcpHub

Modelo: um processo local, do mesmo usuário e na mesma sessão do cliente. Fala MCP por STDIO com o cliente e HTTP com o Gateway no endpoint do perfil (os perfis de exemplo usam `127.0.0.1`, e o Gateway escuta só em `Server.BindAddress`). Não abre porta própria. Vulnerabilidades: seguir o [`SECURITY.md`](../../../SECURITY.md) do repositório.

## O que o Hub garante

- **Política de escrita no Hub, não só no Gateway.** `read-only`, `single-writer` e `open-writer` valem também para a chamada genérica `gxhub_call`; ferramenta sem classificação conta como escrita; e `gxhub_call` é anunciada ao cliente como potencialmente destrutiva.
- **Sem segredos no perfil.** Um token vai por nome de variável de ambiente (`bearerTokenEnvVar`), nunca no JSON, e o `gxhub_status` não o mostra.
- **Chamada que escreve não é repetida** em timeout nem em transporte ambíguo.
- **Erros ao cliente com código fixo.** O texto da exceção, que traz caminhos e diretórios, fica no stderr do Hub.
- **Só encerra o que iniciou**, e confere PID, instante de criação e caminho antes de cada parada; quando adota um processo criado por outro, trabalha por *handle*, nunca pelo número.

## O que o Hub não faz

- **`open-writer` não protege contra gravação concorrente** e o Hub não detecta conflito semântico; `single-writer` protege só entre instâncias do próprio Hub.
- Não é *sandbox*: o Gateway e o Worker rodam com a permissão do usuário.
- Não substitui as travas do Worker nem a política do próprio Genexus18MCP.
- Não valida o host do `endpoint`: o perfil deve apontar para um Gateway local.

## Superfícies sensíveis

- **Escapa do job do cliente, de propósito (0.4.0).** O job é uma escolha do cliente (por exemplo, encerrar tudo ao fechar); o Gateway iniciado fora dele deixa de obedecê-la e também de herdar as restrições do job (memória, CPU, número de processos). O lançador roda com o mesmo usuário, a mesma sessão e o mesmo token do Hub e inicia só o comando que o perfil já mandava iniciar. Um cliente que isole o Hub por token restrito ou contêiner não deixa o COM do WMI funcionar (cai no início comum). `GXMCP_HUB_START_OUTSIDE_JOB=never` desfaz isso.
- **O ambiente do Hub nunca é copiado.** Só o ambiente que o perfil declara e a revisão do perfil vão para o lançador, porque o ambiente do Hub pode ter segredos do cliente.
- **A especificação fica na linha de comando do lançador** por centésimos de segundo (comando, argumentos, diretório e o ambiente do perfil: pins, caminho da KB e diretório de log, que por regra não têm segredo). Visível a quem lista processos do mesmo usuário e a administradores, e **pode ficar registrada** pela auditoria de criação de processo, por Sysmon ou por EDR. Não há arquivo com a especificação em disco.
- **O que o lançador escreve de volta é dado de fora.** O arquivo `<TEMP>\GxMcpHub\launch\<guid>.result` pode ser escrito por qualquer processo do mesmo usuário: o Hub só lê dele um número, um instante e um código (reduzido a minúsculas, dígitos e hífen, no máximo 40 caracteres), e o número e o instante são conferidos contra o processo antes de adotá-lo.
- **`--launch-detached` pode iniciar qualquer comando, com a permissão de quem o chama.** Não eleva privilégio (quem pode chamá-lo pode chamar o comando direto); a especificação é validada e a resposta só pode ser escrita num arquivo `*.result` de uma pasta `GxMcpHub\launch`. Um lançador criado depois do prazo (`notAfterUtc`) não inicia nada.
- **Uso do WMI.** Um programa de segurança pode alertar a criação de processo por WMI. O Hub só a usa no `auto` quando o job seguraria o filho, ou no `always`; WMI recusado ou ausente cai no início comum.
- **Gateways sem dono (0.4.0 e 0.5.0).** Fora do job, ou deixado por `GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE`, o Gateway não morre com o cliente: dura até a próxima implantação (que o troca por PID) ou até reiniciar a máquina; o Worker sai sozinho por ociosidade, mas segura a KB até lá. Um Gateway travado não é visto pelo Hub que sai.
- **O autoteste cria processos de verdade.** O `--self-test` cria lançadores, *jobs* e processos reais pelo WMI e os limpa por número, instante de criação e caminho; numa máquina com EDR pode gerar telemetria ou alerta.

## Chaves de retorno

`GXMCP_HUB_START_OUTSIDE_JOB=never` (início comum, sem WMI), `GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE=0` (ou ausente) e `GXMCP_HUB_KEEP_SHARED_GATEWAY=0` (o Hub que iniciou o Gateway sempre o encerra) desfazem cada recurso por Hub, sem trocar nada.
