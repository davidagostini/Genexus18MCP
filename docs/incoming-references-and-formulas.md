# Referências de entrada e fórmulas

`genexus_impact direction=up` e `both` combinam as referências de entrada do
SDK com o grafo do índice. A caminhada mantém os limites `maxDepth` e
`maxNodes`; as referências do SDK têm limite de 2.000 por objeto e uma janela
de cinco segundos, verificada antes e entre as leituras. Uma leitura síncrona já iniciada
depende do SDK para retornar. Falha, objeto não resolvido ou limite de leitura
impedem `fieldTrust=complete`; `coverageNote` explica a cobertura parcial.
As arestas recuperadas também participam da ordem de dependências. Essa
consulta é somente de leitura e não executa lifecycle.

`genexus_analyze mode=callers` varre as partes disponíveis canônicas. Quando
`Source` é alias de `Events`, somente `Events` é varrida; uma ocorrência em
`Rules` na mesma linha continua sendo um sítio independente.
Painéis Smart Device mantêm as partes canônicas `SDEvents` e `SDRules`;
eliminar aliases não elimina esses sítios de chamada.

No fallback de referências do SDK, `sdkCallers` continua sendo a lista de nomes
existente. `sdkCallerDetails` acrescenta nome e tipo; `formulaDependents`
apresenta o atributo e o texto de sua fórmula. A lista de dependentes por
fórmula também combina referências do SDK no resultado de sítios obtidos pelo
índice; `formulaDependentsCoverage` informa se essa cobertura foi confirmada.
Fórmulas
não são apresentadas como sítios de `Source`/`Events`/`Rules`.

`genexus_search_source fields=[formula]` procura no texto da fórmula de
atributos, usando a mesma expressão de busca e paginação dos demais campos de
metadados. O resultado contém `field=formula` e `matchedValue` com a fórmula;
não inventa número de linha. O campo é opt-in: o padrão `fields=[source]`
permanece igual. Por exemplo, use um alvo sintético `ComputeValue` como
`pattern` para localizar atributos cuja fórmula faz referência a ele.
