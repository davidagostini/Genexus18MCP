# Issue #249 — PostgreSQL com `dbmsCode=15` e provider vazio

## Sintoma

Em ambientes GeneXus modernos, o datastore ativo pode expor o provider textual
vazio e ainda assim informar `dbmsCode=15`. O inventário conseguia devolver o
código, mas `records_query` classificava a família como `unknown` e falhava com
`DataStoreProviderUnsupported` antes de avaliar a metadata de conexão.

## Correção

O resolver único de famílias agora reconhece o código GeneXus 15 como
PostgreSQL. A reprodução sanitizada na própria [issue #249](https://github.com/lennix1337/Genexus18MCP/issues/249)
observa `provider=""`, `dbmsCode=15` e o environment `NETPostgreSQL` numa KB
real; a tabela pública de códigos do GeneXus também lista `POSTGRESQL = 15`
([referência](https://wiki.genexus.com/commwiki/wiki?30642%2CGAM+deploy+tool+command+line+%28only+windows%29=)). A mesma resolução é usada pelo inventário, pelo adapter nativo de
`records_query` e pelo diagnóstico de planos de execução, inclusive quando o
datastore ativo só expõe a propriedade bag. Quando o código é reconhecido, mas
a metadata de servidor/banco continua ausente, o erro correto permanece
`DataStoreConnectionUnavailable`; o MCP não inventa conexão nem expõe
credenciais.

## Garantias

- provider textual vazio continua sendo aceito somente quando o código é
  reconhecido;
- provider ADO.NET PostgreSQL continua usando a fábrica `Npgsql` instalada no
  Worker;
- nenhuma consulta, gravação, alteração de configuração ou ciclo de vida
  GeneXus é executado pelos diagnósticos;
- código desconhecido continua resultando em `DataStoreProviderUnsupported`;
- `records_query` não inclui usuário, senha ou connection string no retorno;
  `db_info` continua projetando somente os campos de inventário previstos no
  contrato, sem credenciais ou strings de conexão.

## Validação

Foram adicionadas regressões para `dbmsCode=15` no resolver de registros, no
property bag do inventário, no diagnóstico de planos, na seleção do datastore
ativo e para códigos desconhecidos/conflitantes falhando de forma segura. A
validação live da KB e do banco continua separada e exige ambiente autorizado
com PostgreSQL configurado; os testes unitários não conectam nem alteram dados.
