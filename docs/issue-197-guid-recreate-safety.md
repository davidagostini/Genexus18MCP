# Issue #197 — segurança no delete/recreate do índice

## Problema

O índice usa `Type:Name` como chave de armazenamento e mantém um mapa reverso
`GuidToKey`. Quando um objeto era removido e recriado com o mesmo tipo e nome,
o novo objeto podia ocupar a chave antiga enquanto o mapa ainda apontava o GUID
apagado para ela. A lista hierárquica também podia conservar a instância antiga.

## Correção

- a atualização substitui o item existente na lista hierárquica, sem duplicar a
  chave;
- ao publicar um novo GUID na mesma chave, o mapeamento reverso anterior é
  removido somente quando ainda aponta para essa chave;
- `RemoveEntryByGuid` valida o GUID da entrada atual antes de remover a chave;
  uma referência reversa obsoleta é descartada sem tocar no objeto recriado;
- as mesmas regras foram aplicadas ao caminho de atualização em lote usado pelo
  lite walk.

## Garantia

`GuidKeyMapTests` cobre tanto a troca normal quanto o estado obsoleto que o
sweep de deleção poderia encontrar. `ParentIndexDedupTests` continua cobrindo
a ausência de duplicação e a sincronia entre a lista e o conjunto de chaves.

A correção é somente de consistência do índice e não altera a seleção de KB nem
executa operações de authoring.
