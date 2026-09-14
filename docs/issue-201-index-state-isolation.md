# Issue #201 — isolamento do espelho de estado do índice

## Problema

O Gateway mantinha o último `GetIndexState` em um campo estático único. Em uma
sessão com mais de uma KB aberta, o worker que terminasse por último substituía
o estado das demais KBs. Assim, `genexus_whoami` e o bloqueio preventivo de
ferramentas de leitura podiam exibir `Ready`, contagem e timestamp de outra KB.

## Correção

- O espelho agora é indexado pelo alias normalizado da KB.
- `whoami` resolve o mesmo alias que aparece em `kb.active` antes de ler ou
  atualizar o estado.
- Em modo estrito, múltiplas KBs sem seleção não usam um worker arbitrário.
- O refresh temporário do worker exige que o alias esteja realmente aberto;
  não há fallback silencioso para a primeira KB do pool.
- `open` invalida apenas o alias reaberto e `close` remove apenas o alias
  fechado, evitando transportar estado entre KBs com o mesmo alias.
- O fast-fail de ferramentas dependentes do índice consulta o estado da KB da
  própria requisição.

## Compatibilidade

Chamadas internas antigas sem alias continuam funcionando no contexto
unívoco/unscoped usado por testes e por chamadas fora de uma requisição de KB.
Nenhum conteúdo de KB é gravado, selecionado ou alterado por essa correção.

## Verificação

- Teste de isolamento com duas KBs e estados/timestamps distintos.
- Suíte direcionada de `WhoamiVersionTests` e `WhoamiPlaybooksTests`.
- Compilação do Gateway e testes com GeneXus 18 U16.
- `git diff --check`.

Os testes são determinísticos e não executam Specify, Generate, Build, Rebuild,
reorganização, publicação ou execução de KB.
