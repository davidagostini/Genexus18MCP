# Movimentação segura de objetos

`genexus_properties action=move` move um objeto para uma Folder ou um Module sem
alterar seu conteúdo. O contrato cobre `Source`, `Rules`, `Events`, `Variables`,
documentação, propriedades e todas as demais partes expostas pelo SDK GeneXus.

## Chamada recomendada

```json
{
  "action": "move",
  "name": "MonitorIntegracaoConteudoArmazenar",
  "type": "Procedure",
  "targetModule": "operacional",
  "baseVersion": "<versionToken retornado por genexus_read>",
  "dryRun": false,
  "rollbackOnFailure": true
}
```

Use `destination` para autodetectar Folder ou Module. Use `targetModule` quando o
destino for explicitamente um Module. `destKind=Folder|Module` resolve somente a
ambiguidade de nomes iguais.

## Garantias

- `dryRun=true` valida origem, destino, tipo, conflito e snapshot, mas não chama
  save nem altera o token. A resposta contém os estados anterior e projetado.
- `baseVersion` é um token opaco de concorrência otimista. Um token antigo retorna
  `VersionConflict` antes da mutação e é conferido novamente dentro da transação.
- Antes do movimento, todas as partes e propriedades autorais são capturadas e
  resumidas em SHA-256.
- O destino e o snapshot são comparados ainda dentro da transação. Qualquer
  divergência aborta o commit.
- Depois do commit, o objeto é relido pelo SDK e comparado novamente. Só então a
  resposta retorna `ObjectMovedAndVerified`, `persisted=true` e `verified=true`.
- Com `rollbackOnFailure=true`, uma divergência pós-save restaura o pai e o
  conteúdo anteriores e informa se o rollback foi verificado.
- A operação nunca executa implicitamente Specify, Generate, Build, Rebuild,
  compilação, reorganização, execução ou testes.

## Resposta de sucesso

```json
{
  "status": "ok",
  "code": "ObjectMovedAndVerified",
  "result": {
    "from": "Root Module",
    "to": "operacional",
    "saved": true,
    "persisted": true,
    "verified": true,
    "preservedParts": ["Procedure", "Rules", "Variables"],
    "requestedHash": "<sha256>",
    "persistedHash": "<mesmo sha256>",
    "versionToken": "<novo token opaco>",
    "generated": false,
    "implicitOperations": []
  }
}
```

Falhas tipadas incluem `VersionConflict`, `MoveSnapshotFailed`, `MoveFailed`,
`MoveNotPersisted`, `MoveContentNotPreserved` e `MoveVerificationFailed`. Nenhuma
delas deve ser interpretada como movimento confirmado.
