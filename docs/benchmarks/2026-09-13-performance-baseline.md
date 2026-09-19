# Comparativo de Performance MCP — 2026-09-13

Comparativo de medições antes e depois das 4 otimizações implementadas.

---

## Resumo das 4 Frentes Implementadas

1. **Ponto 1 (Contexto, Tokens & Tool Profiles)**:
   - Cache de envelopes de resposta completos por perfil em `McpRouter` (`_cachedProfileResponses`), reduzindo o custo de `tools/list` filtrado a O(1) em tempo e zero alocações de `JObject`.
2. **Ponto 2 (Gateway Streaming Serialization)**:
   - Eliminação de chamadas desnecessárias `n.ToString(Formatting.None)` em notificações/heartbeats e streams no `Program.RequestLoop.cs`, escrevendo diretamente via `JsonTextWriter` bufferizado no pipe/stdout.
3. **Ponto 3 (Desacoplamento de Parse da Thread STA do Worker)**:
   - Criação da estrutura `SdkCommandItem { Obj, RawLine }` no `SdkCommandQueue`.
   - O comando recebido é parseado para `JObject` apenas uma vez no loop principal (MTA) e entregue pronto para a fila STA.
   - Eliminação de 3 invocações redundantes de `JObject.Parse(line)` por comando dentro da thread STA (`DescribeCommand`, `ExtractOperationId` e `ProcessCommand`).
4. **Ponto 4 (Otimizações de Memória, LOH e Cache no Worker)**:
   - `ObjectService.BuildReadCacheKey`: Otimizado para usar a sobrecarga nativa de 4 argumentos de `string.Concat` com atalho de valores padrão (`-1|-1|mcp|0`), eliminando alocações de arrays intermediários de 11 strings em cada consulta de cache.
   - `IdleMemoryMaintenance`: Algoritmo adaptativo de compactação periódica de LOH com limite de pressão de heap (acionamento acelerado aos 15s ociosos se o processo exceder 400 MB) para prevenir fragmentação no espaço de endereçamento x86.

---

## 1. Gateway Benchmarks (.NET 10)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **ToolProfileFilter** ('core', 11 tools) | 135,15 µs/op (67 Gen0/10k) | **31,4 ns/op (0 Gen0)** | **~4.300x mais rápido (Zero-alloc)** |
| **ToolProfileFilter** ('authoring', 29 tools) | 283,47 µs/op (191 Gen0/10k) | **21,0 ns/op (0 Gen0)** | **~13.500x mais rápido (Zero-alloc)** |
| **Discovery Endpoints** (resources, templates, prompts) | 0,20 µs/set (0 Gen0) | **0,23 µs/set (0 Gen0)** | Equivalente (sub-microsegundo) |
| **Payload Size** (sem structuredContent / Terse) | 41.403 bytes | **41.403 bytes** | **-46,8% tokens vs full** |
| **Pipe Serialization** (ToString vs WriteTo streaming) | 620,40 µs/op (18 Gen0) | **131,36 µs/op (0 Gen0)** | **-78,8% latência / 0 Gen0** |

---

## 2. Worker Benchmarks (.NET Framework 4.8 STA)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **ObjectService BuildReadCacheKey** (10k pares) | 1,04 µs/pair (2 Gen0) | **0,58 µs/pair (1 Gen0)** | **-44,2% latência (-50% Gen0)** |
| **Build Item LegacyMode** (200 itens x 2k) | 0,523 ms/page (103 Gen0) | **0,179 ms/page (103 Gen0)** | **-65,8% latência** |
| **Type Match Scan** (38k objetos x 100) | 3,603 ms/scan (0 Gen0) | **3,164 ms/scan (0 Gen0)** | **-12,2% latência** |
| **Variable Extraction** (10k iterações) | 0,004 ms/op (3 Gen0) | **0,003 ms/op (3 Gen0)** | **-25,0% latência** |
| **Query Grammar Parse** (10k + 10k) | 8,80 µs/pair (4 Gen0) | **7,73 µs/pair (4 Gen0)** | **-12,2% latência** |
| **STA Command JSON Parsing** | 3 Parses na thread STA | **0 Parses na thread STA** | **STA 100% livre para SDK COM** |

---

## 3. Gateway Dispatch & Payload Guard Benchmarks (.NET 10)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **ResponseSizeGuard** (10k checks ~6KB payload) | 196,65 µs/op (80 Gen0) | **29,26 µs/op (0 Gen0)** | **6,7x mais rápido / Zero-alloc** |
| **McpRouter Tool Dispatch** (100k resoluções) | 169,8 ns/op | **35,6 ns/op** | **4,8x mais rápido (O(1) Dictionary & HashSet)** |

---

## 4. Worker Scale Benchmarks — KBs Grandes (~40.000 Objetos)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **SearchService exactMatch / NameFilter** (40k objetos) | 0,598 ms/busca (16 Gen0) | **0,00035 ms/busca (0 Gen0)** | **1.708x mais rápido (O(1) ByNameIndex multimap)** |
| **ListObjects Top-50 Paging** (40k objetos, limit=50) | 40,35 ms/sort | **2,17 ms/página (0 Gen0)** | **18,6x mais rápido (Single-pass Bounded Heap Top-K)** |
| **IndexEntryFilterBuilder DescriptionContains** | Concatenação e IndexOf em null | **Short-circuit com verificação de null** | **Zero alocação em objetos sem descrição** |

---

## 5. Worker Hot-Path Resolution & Validation Benchmarks — KBs Grandes (~40.000 Objetos)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **Symbol Validation / IsKnownObject** (validação de referências em 40k objetos) | 2,527 ms/check (262 Gen0) | **0,00024 ms/check (0 Gen0)** | **10.435x mais rápido (O(1) ByNameIndex, Zero-alloc)** |
| **Object Resolution / FindIndexEntry & FindObject** (busca exata/miss em 40k objetos) | 0,384 ms/busca (16 Gen0) | **0,00022 ms/busca (0 Gen0)** | **1.745x mais rápido (O(1) multimap sem fallback de 40k)** |
| **Type Gathering / Candidates** (DbOptimize, PatternApply, ValidateConditions) | 1,215 ms/filtro | **0,475 ms/filtro** | **2,6x mais rápido (O(1) TypeIndex buckets)** |
| **IdentityNameMatches Path Evaluation** (nomes simples sem `/` ou `.`) | Substring, Replace e Concat (~200k alocações) | **Zero-allocation short-circuit** | **Elimina 100% das alocações de path em nomes simples** |
| **FormatNotFoundError Ambiguity Check** (29 ferramentas de leitura/edição/estrutura) | Varredura de 40.000 objetos | **O(1) ByNameIndex lookup** | **Envelope de erro imediato sem latência de varredura** |
| **Linter Regex Singletons** (10 regras GX001-GX011) | Compilação dinâmica a cada execução | **Static Compiled Singletons** | **Zero compilação JIT repetida e zero contenção de cache** |
| **CallerGraph BuildAdjacency** (grafo de chamadas em 40k objetos) | Varredura de 40k para nomes + regex dinâmica | **ByNameIndex.Keys + InvocationRegex compilada** | **Grafo inicializado sem scan redundante de nomes** |
| **Visualizer Multi-Criteria Filter** (40k objetos) | 40k anonymous objects + scoring antes de filtrar | **Filtro preliminar via DomainIndex / TypeIndex** | **Elimina até 40.000 alocações de objetos por visualização** |

---

## 6. Worker Search, Health & Property Benchmarks — KBs Grandes (~40.000 Objetos)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **HealthReport** (40k objetos: hotspots, dead code, métricas) | 13,37 ms/relatório (2 Gen0) | **1,79 ms/relatório (0 Gen0)** | **7,5x mais rápido (-86,6% tempo, Zero-alloc)** |
| **Property Wildcard Inspection** (300 props, wildcard query) | 0,385 ms/inspeção | **0,236 ms/inspeção** | **1,6x mais rápido (-38,7% tempo)** |
| **SourceSearch Candidate Selection** (busca em 40k objetos) | Scan linear de 40k objetos chamando ObjectNameMatches | **ByNameIndex O(1) + TypeIndex sets** | **Elimina varredura de 40.000 objetos antes da busca de tokens** |
| **StructureService Logic Item Extraction** (subs e events) | Regex dinâmica + N² strings no Any(s => s.ToString()) | **Static Regexes + HashSet dedup O(N)** | **Zero alocação de JToken string em loops** |
| **Precompiled Scanners & Parsers** (TableDep, Wcag, ApiIntrospect) | Compilação repetida de Regex a cada execução | **Static Compiled Singletons + ConcurrentDict cache** | **Zero compilações redundantes de Regex por tag/parm/tabela** |

---

## 7. Worker Formatting, Linter & Parsing Hot-Paths (Rodada 4)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **FormatService NormalizeKeywords** (2.000 iterações, 26 keywords) | 166,78 ms (52k instâncias Regex) | **8,16 ms (0 alocações Regex)** | **20,5x mais rápido (-95,1% tempo)** |
| **LinterService FindVariableDeclarationLine** (1.000 buscas em 100 decls) | 17,67 ms (100k regex matches) | **1,82 ms (zero regex)** | **9,7x mais rápido (-89,7% tempo)** |
| **PatchTextEditor NormalizeWhitespace** (10.000 linhas) | 21,12 ms (10k regex replaces) | **2,38 ms (scanner zero-regex)** | **8,9x mais rápido (-88,7% tempo)** |
| **DbOptimizeService ExtractAttributeRefs** (Where/Order clauses) | Regex dinâmico por bloco | **Static Singletons + Cache** | **Zero compilação JIT redundante** |
| **ObjectService Call & Variable Extraction** | Regex.Matches dinâmicos | **_callPatternsRegex / _variableRefRegex estáticos** | **Zero alocação JIT por inspeção de código** |
| **DesignSystemService & WritePolicy Comment Stripping** | Regex.Replace dinâmicos | **Static Compiled Regex Singletons** | **Elimina compilações duplicadas em validação** |

---

## 8. Gateway Pipeline & Build Diagnostics Optimization (Rodada 5)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **RequestLoopStages Execution** (10.000 requisições MCP) | 80k objetos + 70k concatenações de chave | **Pipeline estático singleton + chaves pré-computadas** | **Elimina 8 objetos e 7 strings por requisição** |
| **McpPipelineContext Argument Parsing** (dryRun, deploy, etc.) | Reflection `ToObject<T>()` por parâmetro | **Zero-allocation explicit `JToken` casts** | **Evita serializador JSON em cada verificação de flag** |
| **BuildService BuildResult Diagnostics** (getters de erro) | `ErrorsDetailed ?? new List<ErrorDetail>()` | **Safe enumeration sem alocação de fallback list** | **Zero listas temporárias criadas em status de build** |

---
Relatório atualizado e validado em 2026-09-14T09:25:00.
