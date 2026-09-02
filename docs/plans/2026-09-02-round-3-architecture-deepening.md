# Implementation Plan — Round 3 Architecture Deepening & Live HTTP Validation

Deepen the remaining fresh architectural subsystems in Genexus18MCP, followed by full solution validation and a live end-to-end test against the local test KB (`C:\KBs\KBTeste`).

## Proposed Changes

### 1. Candidate 5: Variable Domain & Type Binding Engine
- Create `src/GxMcp.Worker/Services/ITypeBindingEngine.cs` and `src/GxMcp.Worker/Services/TypeBindingEngine.cs` to encapsulate the 6-tier type binding algorithm:
  1. Primitive DB types (`VarChar`, `Numeric`, `Date`, etc.).
  2. Domain references (`DomainReference`, `ExpectedDomainBinding`).
  3. Dotted SDT item types (`Messages.Message`).
  4. Full SDT object references (`SDT`).
  5. Business Component references (`BusinessComponent`).
  6. Built-in GeneXus types (~137 built-in types like `HttpClient`, `WebSession`).
- Deepen `VariableService.cs` to absorb variable resolution, validation, framework protection rules (`FrameworkManagedVariables`), and CRUD logic.
- Create `src/GxMcp.Worker.Tests/VariableDomainTests.cs` covering type binding and protection rules.

### 2. Candidate 1: Object Reading & Virtual DSL Synthesis (`IObjectReader`)
- Create `src/GxMcp.Worker/Services/IObjectReader.cs` and `src/GxMcp.Worker/Services/ObjectReader.cs` encapsulating:
  - Read caching (`_readCache` with TTL and invalidation).
  - Virtual part synthesis (Transaction / SDT structure DSL, DesignSystem Tokens+Styles split, DataSelector parts).
  - Unified line- and byte-budget pagination delegating to `ReadPagination`.
- Deepen `ObjectInspectionModule.cs` to use `IObjectReader`.
- Create `src/GxMcp.Worker.Tests/ObjectReaderTests.cs`.

### 3. Candidate 2: Multi-Mode Analysis Engine & Signature Parser
- Create `src/GxMcp.Worker/Services/GeneXusSignatureParser.cs` as a pure domain parser for parameter rules `parm(...)`, direction tokens `in`, `out`, `inout`, and call signatures, eliminating duplication across `AnalyzeService` and `ObjectService`.
- Create `src/GxMcp.Worker/Services/IAnalysisEngine.cs` and `src/GxMcp.Worker/Services/AnalysisEngine.cs` with pluggable `IAnalysisModeHandler` strategy registry.
- Create `src/GxMcp.Worker.Tests/AnalysisEngineTests.cs`.

### 4. Candidate 4: Layered Build Engine & Reorganization Analyzer
- Create `src/GxMcp.Worker/Services/IProcessExecutor.cs` and `src/GxMcp.Worker/Services/SystemProcessExecutor.cs` encapsulating process launching, streaming, and termination behind a mockable seam.
- Create `src/GxMcp.Worker/Services/IReorganizationAnalyzer.cs` and `src/GxMcp.Worker/Services/ReorganizationAnalyzer.cs` encapsulating schema impact and DDL classification.
- Create `src/GxMcp.Worker.Tests/BuildEngineTests.cs`.

### 5. Verification & Live HTTP Server Test with Test KB
- Run `.\build.ps1`.
- Run `dotnet test Genexus18MCP.sln`.
- Run `npm test` and `npm run lint`.
- Launch the Gateway HTTP server on port 5000 with `config.json` (`KBTeste`).
- Execute live MCP HTTP handshake: `initialize`, `tools/list`, `genexus_whoami`, `genexus_read` or `genexus_query` against `KBTeste`.
- Stop the background server cleanly.
- Document entries in `CHANGELOG.md`.
