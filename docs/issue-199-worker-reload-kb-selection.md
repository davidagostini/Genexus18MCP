# Issue #199 — seleção segura do Worker no reload

## Problema

O caminho `genexus_worker_reload mode=soft` recebia um contexto de KB pela
resolução de sessão, mas ignorava esse resultado e usava o primeiro item de
`ListOpen()`. Com várias KBs abertas, o reload podia atingir outra KB. Após
`genexus_kb action=open` sem seleção, também não havia um alvo persistente para
inferir com segurança.

## Correção

- o handler usa o alvo resolvido para a requisição e confirma que ele ainda
  possui Worker aberto;
- `kb` e `alias` passam a ser aceitos na superfície do `worker_reload`, com
  `alias` mantido como compatibilidade;
- quando não existe seleção nem alvo explícito, o handler não escolhe por ordem
  da lista: retorna orientação para `genexus_kb action=select` ou para o uso de
  `kb=<alias>`;
- o detalhe de erro identifica o comportamento seguro para sessões com várias
  KBs.

## Garantia

`WorkerReloadSelectionTests` cobre: seleção da segunda KB aberta, ausência de
seleção sem fallback arbitrário, alias explícito e sessão selecionada sem
Worker aberto. A alteração não muda a política de seleção da KB; apenas impede
que o reload ignore o contexto já resolvido.
