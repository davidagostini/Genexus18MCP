# Genexus18MCP Next-Gen Capabilities Implementation Plan

> **For Claude / Antigravity:** REQUIRED SUB-SKILL: Use superpowers:executing-plans / subagent-driven-development to implement this plan task-by-task.

**Goal:** Implement 6 high-impact architectural and authoring capabilities for Genexus18MCP: Dynamic Tool Gating/Profiles, Auto-Fix & Self-Correction Engine, OpenAPI 3.0 Import/Export, DSO & Chameleon Design System Support, GXtest Unit Test Generator, and MCP Resource Subscriptions.

**Architecture:** 
- **Gateway (.NET 8):** Dynamic Tool Filtering on `tools/list` via `GXMCP_PROFILE` / config; MCP Resource Subscriptions (`resources/subscribe`, `resources/unsubscribe`) with event dispatching over stdio & SSE.
- **Worker (.NET 4.8 STA):**
  - `ErrorDiagnoser`: Diagnoses GeneXus compiler/specifier error codes and attaches structured `suggestedFixes` to build/linter envelopes.
  - `ApiOpenApiService`: Parses OpenAPI 3.0 specs to generate GeneXus SDTs and API Objects, and serializes API Objects to canonical OpenAPI 3.0.
  - `DesignSystemService`: Manages DSO (`DesignSystem`) object parts, tokens, styling rules, and Chameleon/Mercury CSS classes.
  - `AutoTestService`: Inspects procedure signatures/parms and generates complete `ProcedureUnitTest` objects with assertions.
  - `ResourceEventService`: Emits resource mutation events (`genexus://objects/...`, `genexus://kb/...`) back to the Gateway.

**Tech Stack:** C# .NET 8.0 (`GxMcp.Gateway`), .NET Framework 4.8 (`GxMcp.Worker`), GeneXus 18 SDK COM/Interop (`Artech.*`), xUnit test suites (`GxMcp.Gateway.Tests`, `GxMcp.Worker.Tests`).

---

## Tasks Breakdown

### Task 1: Dynamic Tool Gating & Profiles (`GXMCP_PROFILE`)
**Files:**
- Modify: `src/GxMcp.Gateway/Program.cs`
- Modify: `src/GxMcp.Gateway/Config.cs`
- Create: `src/GxMcp.Gateway/Services/ToolProfileFilter.cs`
- Test: `src/GxMcp.Gateway.Tests/ToolProfileTests.cs`

**Step 1: Write failing tests for Tool Profiles**
Verify `ToolProfileFilter.Filter(tools, profile)` correctly filters:
- `all`: returns all tools.
- `core`: returns `genexus_query`, `genexus_read`, `genexus_edit`, `genexus_lifecycle`, `genexus_whoami`, `genexus_inspect`, `genexus_analyze`.
- `authoring`: returns core + `genexus_create`, `genexus_structure`, `genexus_variable`, `genexus_authoring`, `genexus_api`, `genexus_layout`, etc.
- `devops`: returns `genexus_lifecycle`, `genexus_gxserver`, `genexus_deploy`, `genexus_versioning`, `genexus_test`, `genexus_doctor`, etc.
- `ui`: returns `genexus_layout`, `genexus_edit_form`, `genexus_browser`, `genexus_wwp`, `genexus_apply_pattern`, `genexus_design_system`, etc.

**Step 2: Run test to verify failure**
`dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolProfileTests"`

**Step 3: Implement ToolProfileFilter and Gateway Integration**
- Add `Server.ToolProfile` in `Config.cs` (reads `GXMCP_PROFILE` env variable or `config.json`).
- Implement `ToolProfileFilter.cs` with category maps.
- Integrate into `Program.cs` `tools/list` handler.

**Step 4: Run tests to verify they pass**
`dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolProfileTests"`

---

### Task 2: Auto-Fix & Self-Correction Engine (`ErrorDiagnoser`)
**Files:**
- Create: `src/GxMcp.Worker/Services/ErrorDiagnoser.cs`
- Modify: `src/GxMcp.Worker/Services/BuildService.cs`
- Modify: `src/GxMcp.Worker/Services/LinterService.cs`
- Modify: `src/GxMcp.Worker/Services/AnalyzeService.cs`
- Test: `src/GxMcp.Worker.Tests/ErrorDiagnoserTests.cs`

**Step 1: Write failing tests for ErrorDiagnoser**
- Test diagnosis of `spc0005` (variable not defined) -> suggests `genexus_variable action=add`.
- Test diagnosis of `spc0011` (invalid parameter count/type) -> suggests signature fix or parm rule adjustment.
- Test diagnosis of `spc0026` (syntax error in source/rule) -> points to line and auto-fix edit.
- Test integration in `BuildService` returning `suggestedFixes` array on build failure.

**Step 2: Run test to verify failure**
`dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ErrorDiagnoserTests"`

**Step 3: Implement ErrorDiagnoser and attach to build/linter pipelines**
- Parse GeneXus output messages for standard `spcXXXX` patterns.
- Produce structured `AutoFixSuggestion` objects (`{ Tool, Arguments, Explanation, Confidence, Line }`).
- Wire into `BuildService.cs` and `AnalyzeService.cs`.

**Step 4: Run tests to verify they pass**
`dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ErrorDiagnoserTests"`

---

### Task 3: OpenAPI 3.0 Import & Export for GeneXus API Objects
**Files:**
- Create: `src/GxMcp.Worker/Services/ApiOpenApiService.cs`
- Modify: `src/GxMcp.Worker/Services/ApiIntrospectService.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Gateway/tool_definitions.json` (update `genexus_api` schema with `import_openapi` and `export_openapi` actions)
- Test: `src/GxMcp.Worker.Tests/ApiOpenApiServiceTests.cs`

**Step 1: Write failing tests for OpenAPI 3.0 Import/Export**
- Test `ExportOpenApi`: Converts GeneXus API Object with services and SDT parameters into valid OpenAPI 3.0 JSON.
- Test `ImportOpenApi`: Parses OpenAPI 3.0 YAML/JSON spec and produces structure for SDTs and API Object declaration.

**Step 2: Run test to verify failure**
`dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ApiOpenApiServiceTests"`

**Step 3: Implement ApiOpenApiService**
- Construct OpenAPI 3.0 JSON generator using SDK object metadata.
- Implement OpenAPI 3.0 parser converting schemas to GeneXus SDT structures and paths to API methods.
- Connect to `CommandDispatcher.cs` under `api_import_openapi` and `api_export_openapi`.

**Step 4: Run tests to verify they pass**
`dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ApiOpenApiServiceTests"`

---

### Task 4: Design System Object (DSO) & Chameleon / Mercury Styling Engine
**Files:**
- Create: `src/GxMcp.Worker/Services/DesignSystemService.cs`
- Modify: `src/GxMcp.Worker/Services/LayoutService.cs`
- Modify: `src/GxMcp.Worker/Services/StructureService.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Test: `src/GxMcp.Worker.Tests/DesignSystemServiceTests.cs`

**Step 1: Write failing tests for DSO tokens & style authoring**
- Test `InspectDso`: Extracts tokens (colors, font families, font sizes, margins, radius) and styles rules from a DSO.
- Test `EditDso`: Adds or updates style rules and design tokens with syntax verification.
- Test `ValidateDso`: Validates CSS/DSO syntax rules and flags invalid tokens or missing class definitions.

**Step 2: Run test to verify failure**
`dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~DesignSystemServiceTests"`

**Step 3: Implement DesignSystemService**
- Access `DesignSystemPart` in GeneXus SDK.
- Provide token extraction parser (`#tokens { ... }`) and style rule parser (`#styles { ... }`).
- Wire to `CommandDispatcher.cs` for `dso_inspect`, `dso_edit`, and `dso_validate`.

**Step 4: Run tests to verify they pass**
`dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~DesignSystemServiceTests"`

---

### Task 5: GXtest Automated Unit Test Generator (`genexus_test action=generate_unit`)
**Files:**
- Create: `src/GxMcp.Worker/Services/GxTestGeneratorService.cs`
- Modify: `src/GxMcp.Worker/Services/AutoTestService.cs`
- Modify: `src/GxMcp.Worker/Services/TestService.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Test: `src/GxMcp.Worker.Tests/GxTestGeneratorServiceTests.cs`

**Step 1: Write failing tests for Unit Test Generation**
- Test `GenerateProcedureUnitTest`: Inspects Procedure with `in:&Id, out:&Success, out:&Messages`, creates `ProcedureUnitTest` source with:
  - Setup parameters
  - Execution call
  - `Assert.IsTrue(&Success, 'Should succeed with valid ID')`
  - Boundary test case (e.g. `&Id = 0`)
  - Error assertion test case

**Step 2: Run test to verify failure**
`dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~GxTestGeneratorServiceTests"`

**Step 3: Implement GxTestGeneratorService**
- Analyze Procedure `parm` rules and variable definitions.
- Generate valid GXtest code structure adhering to GeneXus 18 Unit Testing standards.
- Expose via `genexus_test action=generate_unit` or `genexus_auto_test`.

**Step 4: Run tests to verify they pass**
`dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~GxTestGeneratorServiceTests"`

---

### Task 6: MCP Resource Subscriptions & Real-Time Event Notifications
**Files:**
- Create: `src/GxMcp.Gateway/Services/ResourceSubscriptionService.cs`
- Modify: `src/GxMcp.Gateway/Program.cs` (add `resources/subscribe` and `resources/unsubscribe` handlers)
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs` & `KbWatcherService.cs`
- Test: `src/GxMcp.Gateway.Tests/ResourceSubscriptionTests.cs`

**Step 1: Write failing tests for Resource Subscriptions**
- Test client subscribing to `genexus://kb/health` or `genexus://objects/{Name}/part/{Part}`.
- Test notification emitted on object save / lifecycle event.

**Step 2: Run test to verify failure**
`dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ResourceSubscriptionTests"`

**Step 3: Implement ResourceSubscriptionService & Notification Loop**
- Gateway maintains active subscriptions per session / transport.
- When worker reports mutation / build completion, gateway dispatches `notifications/resources/updated` with `uri`.

**Step 4: Run tests to verify they pass**
`dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ResourceSubscriptionTests"`

---

### Task 7: Full Solution Build, Contract Validation & CHANGELOG Update
**Files:**
- Modify: `CHANGELOG.md` (Add all new capabilities under `## Unreleased`)
- Modify: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json` (if schemas updated)
- Validate: `dotnet test Genexus18MCP.sln`
