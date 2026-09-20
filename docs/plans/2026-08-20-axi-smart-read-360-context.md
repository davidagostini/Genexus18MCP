# AXI Smart Read & 360° Context Engine Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eliminate multi-roundtrip context discovery for AI agents by providing complete object context on `genexus_read` by default, a 360° dependency-bundled `genexus_analyze mode=context`, and proactive AXI guidance across tool definitions and playbooks.

**Architecture:** 
1. `ObjectService.ReadFullObject` detects object type and extracts all primary parts (`rules`/`parm`, `source`/`events`, `variables`, `structure`, `calledSignatures`) in a single JSON envelope when `part` is omitted on `genexus_read`.
2. `AnalyzeService.Get360Context` bundles the target object plus inlined `parm` signatures of called procedures, schemas/PKs of referenced tables, referenced SDT structures, and callers into a unified `mode=context` payload.
3. AXI layers (`tool_definitions.json`, `NextLegalActionsBuilder`, `Program.Whoami` playbooks, and error `nextSteps`) steer agents to use these single-roundtrip tools.

**Tech Stack:** C# .NET 8.0 (Gateway) / .NET Framework 4.8 (Worker / GeneXus 18 SDK COM), JSON-RPC MCP.

---

### Task 1: Worker Core — Implement `ReadFullObject` in `ObjectService`

**Files:**
- Modify: `src/GxMcp.Worker/Services/ObjectService.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Test: `src/GxMcp.Worker.Tests/SmartReadTests.cs`

**Step 1: Write unit tests for `ReadFullObject`**
- Test Procedure returns `{ rules, source, variables, signature, calledSignatures }`.
- Test WebPanel returns `{ rules, events, variables, uiStructure, calledSignatures }` (no empty `Source`).
- Test Transaction returns `{ structure, rules, events, variables }`.
- Test SDT returns `{ structure, isCollection, properties }`.

**Step 2: Implement `ReadFullObject` in `ObjectService`**
- Detect KBObject type and map to appropriate multi-part extractor.
- Include compact variable table (`name`, `type`, `isCollection`).
- Extract inlined `parmRule` and called object names with signatures.

**Step 3: Wire `ExtractFullObject` in `CommandDispatcher`**
- Action `ExtractFullObject` maps to `_objectService.ReadFullObject`.

**Step 4: Verify tests pass**
- Run `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SmartReadTests"`.

---

### Task 2: Gateway Router & Default Routing for `genexus_read`

**Files:**
- Modify: `src/GxMcp.Gateway/Routers/ObjectRouter.cs`
- Test: `src/GxMcp.Gateway.Tests/McpRouterTests.cs`

**Step 1: Update `ObjectRouter.cs`**
- When `toolName == "genexus_read"` and `args["part"]` is null/omitted and `args["parts"]` is null:
  - Route to `module: "Read", action: "ExtractFullObject", target: target, type: type`.
- When `args["part"]` is explicitly provided, preserve targeted single-part extraction (`action: "ExtractSource"`).

**Step 2: Verify Gateway router tests**
- Run `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~McpRouterTests"`.

---

### Task 3: 360° Context Engine — Implement `genexus_analyze mode=context`

**Files:**
- Modify: `src/GxMcp.Worker/Services/AnalyzeService.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Gateway/Routers/AnalyzeRouter.cs`
- Test: `src/GxMcp.Worker.Tests/Context360Tests.cs`

**Step 1: Write unit tests for 360° context**
- Test that `Get360Context` bundles target object + called procedure signatures + referenced tables with PKs + SDTs + callers.

**Step 2: Implement `Get360Context` in `AnalyzeService`**
- Reuse `ReadFullObject` for base object.
- Scan outgoing references:
  - For called Procedures/DataProviders: fetch signature via `GetParametersInternal`.
  - For referenced Tables: fetch PK and column definitions.
  - For SDT variables: fetch SDT structure.
- Scan incoming references: fetch top callers with context.

**Step 3: Wire `mode=context` (and alias `deep_context`) in `AnalyzeRouter` and `CommandDispatcher`**

**Step 4: Verify tests pass**
- Run `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Context360Tests"`.

---

### Task 4: AXI Guidance & Tool Definitions

**Files:**
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Modify: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`
- Modify: `src/GxMcp.Gateway/NextLegalActionsBuilder.cs`
- Modify: `src/GxMcp.Gateway/Program.Whoami.cs`
- Test: `src/GxMcp.Gateway.Tests/ToolSchemaSizeTests.cs`

**Step 1: Update tool descriptions in `tool_definitions.json`**
- `genexus_read`: explicitly highlight that omitting `part` returns the entire object in 1 call.
- `genexus_analyze`: add `"context"` to `mode` enum with description of 360° bundling.

**Step 2: Synchronize golden contract fixture `tools-list.response.json`**
- Keep alphabetically sorted.

**Step 3: Update AXI Playbooks & Suggestions**
- In `Program.Whoami.cs`: add playbook `context_efficient_playbook` explaining the 1-call context pattern.
- In `NextLegalActionsBuilder.cs`: suggest `genexus_read(name: "...")` after queries/creates.

---

### Task 5: Live Verification on Real KB (`C:\KBs\KBTeste`)

**Files:**
- Create: `scratchpad/live_test_context.ps1`

**Step 1: Build solution and deploy to `publish/`**
- Run `powershell -File .\build.ps1`.

**Step 2: Execute live test against real GeneXus KB**
- Open scratch gateway on port 5005.
- Call `genexus_read(name="...")` without `part` on a Procedure and WebPanel -> verify all parts returned in 1 roundtrip.
- Call `genexus_analyze(name="...", mode="context")` -> verify 360° graph (called signatures, tables, SDTs, callers) in 1 roundtrip.
- Clean up test objects.

---

### Task 6: Full Suite Verification & CHANGELOG

**Step 1: Run all test suites**
- `dotnet test Genexus18MCP.sln`
- `npm test`

**Step 2: Update `CHANGELOG.md`**
- Add entry in `## Unreleased` under `### Added` / `### Changed`.
