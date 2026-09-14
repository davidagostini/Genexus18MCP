# Issue #200 — normalização de `KBObject.LastUpdate`

## Problema

O SDK GeneXus entrega `DateTime` por interoperabilidade COM e o `Kind` pode
ser `Utc`, `Local` ou `Unspecified`. O código anterior lia o valor cru no
watcher/HWM e aplicava `ToUniversalTime()` em algumas respostas. Assim, um
`Unspecified` podia receber silenciosamente o fuso da máquina, divergindo do
HWM e dos campos exibidos.

## Correção

- `SdkTimestamp` é a fronteira única para leitura e serialização de
  `KBObject.LastUpdate`.
- `Utc` permanece inalterado; `Local` é convertido para UTC; `Unspecified`
  preserva os ticks e recebe `DateTimeKind.Utc`, mantendo o contrato histórico
  do HWM sem aplicar um deslocamento de máquina não informado pelo SDK.
- O watcher, o delta, o índice, filtros de listagem, análise, explicação,
  settings, patch e recibos de escrita usam o accessor normalizado.
- `genexus_doctor` e `GetIndexState` expõem `lastUpdateTimestamp`, com contagem
  dos `Kind` realmente observados e a política usada para `Unspecified`.
  Antes de uma leitura do SDK, `observed=false`; depois, os contadores servem
  como preflight verificável no ambiente GeneXus em uso.

## Compatibilidade e segurança

O ajuste é aditivo para o diagnóstico e não muda o formato dos timestamps:
eles continuam em ISO-8601 UTC. Não altera seleção de KB, não chama Specify,
Generate, Build, Rebuild, Deploy, Publish ou execução automática, e não escreve
na KB. `KBVersion.LastUpdate` permanece fora do escopo porque é uma superfície
SDK diferente da `KBObject` tratada nesta issue.

## Validação

Na worktree `codex/issue-200-lastupdate-timezone`:

- 5 testes direcionados de normalização, falha de accessor e diagnóstico —
  5 passaram.
- A suíte completa Worker será executada antes do commit/PR.
- Os testes usam `GX_PATH=C:\Genexus\GeneXus18U16` e não abrem KB real.

Nenhuma KB, arquivo `.gx` ou artefato de usuário foi alterado.
