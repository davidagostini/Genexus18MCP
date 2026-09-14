# Issue #196 — índice restaurado após open/reload

## Problema

Um worker recém-aberto restaurava `index-snapshot.bin`, publicava o catálogo
como `Ready` e atualizava `lastIndexedAt` para o horário do processo. Isso
confundia disponibilidade para leitura com atualização da KB. Como o
bootstrap do gateway era um one-shot process-wide, um `open` ou
`worker_reload` podia não iniciar o delta-on-open; objetos criados depois do
snapshot ficavam fora de `list_objects` sem um sinal de defasagem.

## Correção

- `OpenKB` inicia `BulkIndex(false)` depois da restauração. O caminho rápido
  valida o sidecar e inicia o delta em background; quando a baseline não é
  elegível, o fluxo existente inicia a reconstrução necessária.
- A restauração exige `CapturedAtUtc` válido e usa esse instante como
  `lastIndexedAt`/`lastSuccessfulScanAt`. Snapshots antigos sem esse dado
  seguem para fallback, em vez de receberem um horário inventado.
- O estado agora separa disponibilidade de frescor:
  - `unknown`: ainda não há scan ou timestamp confiável;
  - `stale`: catálogo carregado de disco, aguardando validação/atualização;
  - `refreshing`: delta ou indexação em andamento;
  - `current`: último scan concluído com sucesso.
- O gateway rearma o bootstrap por alias normalizado em `open`, reload suave,
  reload forçado e respawn. Assim cada worker recebe o próprio comando, sem
  depender do primeiro worker/default KB do processo.
- `genexus_whoami`, `genexus_doctor` e `genexus_list_objects` expõem
  `freshness` e `lastSuccessfulScanAt` de forma aditiva. A paginação existente
  permanece inalterada; a chave do cache da listagem inclui o estado de frescor
  para não reaproveitar uma página com diagnóstico antigo.

## Compatibilidade e segurança

Os campos novos são aditivos e não alteram `indexStatus`, `Ready`, os campos de
paginação nem a política de que o preview/leituras não executam Build,
Generate, Rebuild, Deploy ou Publish. A mudança não abre, grava ou altera KBs
nos testes unitários.

## Validação

Executado na worktree `codex/issue-196-index-freshness-after-open`:

- Worker: 24 testes direcionados de estado, snapshot e listagem — 24 passaram.
- Gateway: 32 testes direcionados de whoami e ciclo de respawn — 32 passaram.
- Os testes foram executados contra `GX_PATH=C:\Genexus\GeneXus18U16`.
- O ambiente reportou `GXMCP_SDK_FINGERPRINT_DRIFT` para DLLs instaladas e
  `GXMCP_SDK_COMPATIBLE` para a versão 18.0.16; isso é diagnóstico do SDK local,
  não falha dos testes.

Nenhuma KB, arquivo `.gx` ou artefato de usuário foi alterado.
