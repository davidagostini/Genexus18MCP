# Issue #198 — leitura binária de `File`/`WikiFileKBObject`

## Problema

`genexus_read` e `export_part` tratavam `WikiBlob` como uma part textual. O
resultado era apenas o XML de propriedades (`FileName`, `IsDefault`, etc.),
sem os bytes do arquivo, e o export podia aparentar sucesso com o conteúdo
errado.

## Correção

Foi adicionada a ação `genexus_io action=read_file_content`, somente leitura da
KB (a variante com `outputPath` grava apenas o destino externo solicitado):

```json
{
  "action": "read_file_content",
  "name": "MeuArquivo",
  "type": "File",
  "outputPath": "C:\\tmp\\meu-arquivo.json",
  "includeBase64": true,
  "maxBytes": 1048576
}
```

- Localiza a part `WikiBlobPart` do objeto File.
- Invoca `Artech.Genexus.Common.Wiki.BlobKBObjectHelper.SaveWikiBlobPartFile`
  por reflexão, escolhendo a sobrecarga `(part, string)` presente no SDK.
- Retorna `bytes`, SHA-256, tipo, GUID, origem SDK e, quando solicitado,
  base64 limitado com `inlineTruncated` explícito.
- `outputPath` respeita `overwrite`; sem `outputPath`, um arquivo temporário é
  usado somente para materializar o blob e removido ao final da chamada. A
  leitura inline permanece segura para cache/retry; com `outputPath`, o
  classificador reconhece a escrita externa e não aplica esse caminho seguro.
- Se a part/helper não estiver disponível, retorna erro diagnóstico e nunca
  converte o XML de metadados em falso conteúdo binário.

## Compatibilidade e segurança

O método foi confirmado por reflexão nas instalações GeneXus U11, U12 e U16.
A ação não chama Specify, Generate, Build, Rebuild, Deploy, Publish ou execução,
e não altera a KB. A única escrita possível é o arquivo externo solicitado
explicitamente em `outputPath` (ou o temporário interno removido ao final).

## Validação

- Contrato de descoberta atualizado (`tool_definitions.json` + golden fixture).
- Roteamento coberto para `genexus_io` e para as opções de limite/base64.
- Reflexão do SDK confirmou a sobrecarga de leitura em U11/U12/U16.
- A suíte Worker e os builds Release U11/U12/U16 serão executados antes do
  commit/PR.

Nenhuma KB, arquivo `.gx` ou artefato de usuário foi alterado.

## Gate de contrato

O baseline deste branch é de 54 ferramentas e 227 ações. A contagem é
intencionalmente verificada no CI para impedir que uma nova ação seja
publicada sem schema, fixture e documentação alinhados; alterações futuras do
schema devem atualizar esse baseline no mesmo commit.
