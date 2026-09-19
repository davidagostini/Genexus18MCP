# GeneXus 16 SDK Support Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extend Genexus18MCP to fully support GeneXus 16 SDK alongside GeneXus 17 and 18, enabling the Worker to build and run against GeneXus 16 without breaking or degrading GeneXus 17/18 capabilities.

**Architecture:** Isolate GeneXus-17/18-specific SDK types (`DSObject`, `API`, `GeneratorsPart`, `GeneXus.TeamDevClient.Architecture.BL`) behind compatibility adapters, dynamic dispatch, and conditional compilation. Update the version catalog (`config/gx-versions.json`), SDK compatibility validator, and gateway version probe to recognize GeneXus 16 as an officially supported SDK major.

**Tech Stack:** C# (`net48` Worker, `net10.0-windows` Gateway), GeneXus Artech SDK 16/17/18, Newtonsoft.Json, xUnit, PowerShell 7+.

---

### Task 1: Update Version Catalog and Validator for GeneXus 16

**Files:**
- Modify: `config/gx-versions.json`
- Modify: `config/sdk-compatibility.json`
- Modify: `scripts/validate-gx-sdk.ps1`
- Modify: `src/GxMcp.Worker/SdkCompatibilityValidator.cs`
- Modify: `src/GxMcp.Gateway/GeneXusVersionCatalog.cs`
- Test: `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs`
- Test: `src/GxMcp.Worker.Tests/SdkCompatibilityValidatorTests.cs`

**Step 1: Write the failing tests**
Add GeneXus 16 test cases to `WhoamiVersionTests.cs` and `SdkCompatibilityValidatorTests.cs`.
- `WhoamiVersionTests`: `[InlineData("16.0.11.144151", "16", true)]`, assert `supportedMajors` contains `"16"`.
- `SdkCompatibilityValidatorTests`: include `"16"` in test fixtures and assert compatibility.

**Step 2: Run test to verify it fails**
`dotnet test src/GxMcp.Gateway.Tests --filter WhoamiVersionTests`
Expected: FAIL on `InlineData("16.0.11.144151", "16", true)`.

**Step 3: Implement catalog and validator updates**
- Add GeneXus 16 entry to `config/gx-versions.json`.
- Add fallback entry for `"16"` to `GeneXusVersionCatalog.cs`.
- In `config/sdk-compatibility.json`: mark `GeneXus.TeamDevClient.Architecture.BL.dll` as `"optional": true`.
- In `scripts/validate-gx-sdk.ps1`: respect `$assembly.optional -eq $true`.
- In `SdkCompatibilityValidator.cs`: respect `(bool?)token["optional"] == true`.

**Step 4: Run test to verify it passes**
`dotnet test src/GxMcp.Gateway.Tests --filter WhoamiVersionTests`
`dotnet test src/GxMcp.Worker.Tests --filter SdkCompatibilityValidatorTests`
Expected: PASS.

---

### Task 2: Decouple DesignSystem from Static `DSObject` Reference

**Files:**
- Modify: `src/GxMcp.Worker/Compatibility/DesignSystemSdkAdapter.cs`
- Modify: `src/GxMcp.Worker/Services/DesignSystemService.cs`
- Test: `src/GxMcp.Worker.Tests/DesignSystemCompatibilityTests.cs`

**Step 1: Inspect existing tests**
Run `dotnet test src/GxMcp.Worker.Tests --filter DesignSystemCompatibilityTests` to ensure existing tests pass.

**Step 2: Refactor `DesignSystemSdkAdapter.cs` and `DesignSystemService.cs`**
- In `DesignSystemSdkAdapter.cs`:
  - Remove `using Artech.Genexus.Common.Objects.DesignSystem;`
  - Change `Read(DSObject dso)` signature to `Read(KBObject dso)`.
  - Use `PartAccessor.GetDesignSystemPart(dso, styles)` which already takes `KBObject`.
- In `DesignSystemService.cs`:
  - Remove `using Artech.Genexus.Common.Objects.DesignSystem;`
  - Replace `DSObject` references with `KBObject`.

**Step 3: Run tests to verify they pass**
`dotnet test src/GxMcp.Worker.Tests --filter DesignSystemCompatibilityTests`
Expected: PASS.

---

### Task 3: Decouple API Object from Static `Artech.Genexus.Common.Objects.API`

**Files:**
- Modify: `src/GxMcp.Worker/Services/ApiIntrospectService.cs`
- Test: `src/GxMcp.Worker.Tests/ApiIntrospectServiceTests.cs` (or relevant API tests)

**Step 1: Refactor `ApiIntrospectService.cs`**
- Remove `using GeneXusApi = Artech.Genexus.Common.Objects.API;`.
- Replace `GeneXusApi` with `KBObject`.
- Resolve `ServiceGroupSource` part via `dynamic` property access or `obj.Parts.FirstOrDefault(...)` safely.
- In `EnumerateApis`: check `string.Equals(o?.TypeDescriptor?.Name, "API", StringComparison.OrdinalIgnoreCase)`.

**Step 2: Verify existing tests pass**
`dotnet test src/GxMcp.Worker.Tests --filter Api`
Expected: PASS.

---

### Task 4: Conditional Compilation and Fallback for `GeneXus.TeamDevClient.Architecture.BL`

**Files:**
- Modify: `src/GxMcp.Worker/GxMcp.Worker.csproj`
- Modify: `src/GxMcp.Worker/Services/CiPipelineService.cs`

**Step 1: Update `GxMcp.Worker.csproj`**
- Make `<Reference Include="GeneXus.TeamDevClient.Architecture.BL">` conditional on `Condition="Exists('$(GX_PATH)\GeneXus.TeamDevClient.Architecture.BL.dll')"`.
- Add `<PropertyGroup Condition="Exists('$(GX_PATH)\GeneXus.TeamDevClient.Architecture.BL.dll')"><DefineConstants>$(DefineConstants);HAS_TEAMDEV_CI</DefineConstants></PropertyGroup>`.

**Step 2: Update `CiPipelineService.cs`**
- Wrap `using Ci = GeneXus.TeamDevClient.Architecture.BL.Services;` and CI service calls in `#if HAS_TEAMDEV_CI ... #else ... #endif`.
- In `#else`, return `McpResponse.Err("ContinuousIntegrationServiceUnavailable", "GeneXus.TeamDevClient.Architecture.BL.dll is not present in this GeneXus version.", "CI pipelines require GeneXus 17+.")`.

---

### Task 5: Adapt `GeneratorReferenceService` for EnvironmentsPart / GxEnvironment

**Files:**
- Modify: `src/GxMcp.Worker/Services/GeneratorReferenceService.cs`
- Test: `src/GxMcp.Worker.Tests/GeneratorReferenceServiceTests.cs`

**Step 1: Inspect `GeneratorReferenceService.cs`**
- Remove static imports: `using Artech.Genexus.Common.ModelParts;` and `using Artech.Genexus.Common.Entities;` where they conflict.
- Update `Capture` in `SdkGeneratorConfigurationStore` to resolve either `GeneratorsPart` (`part.Generators`) or `EnvironmentsPart` (`part.Environments`).
- Handle `GxGenerator` and `GxEnvironment` polymorphically via `dynamic` properties (`Properties`, `Description`, `ToString()`, etc.).

**Step 2: Run tests to verify it passes**
`dotnet test src/GxMcp.Worker.Tests --filter GeneratorReference`
Expected: PASS.

---

### Task 6: Multi-SDK Build Verification Across GX16, GX17, and GX18

**Files:**
- Scripts: `scripts/sync-release-metadata.py`, `scripts/test-live-matrix.ps1`
- Docs: `CHANGELOG.md`, `README.md`, `AGENTS.md`

**Step 1: Build Worker against GeneXus 16**
Run:
`$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus16'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj`
Expected: Build SUCCEEDED with 0 errors.

**Step 2: Build Worker against GeneXus 17**
Run:
`$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus17Trial'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj`
Expected: Build SUCCEEDED with 0 errors.

**Step 3: Build Worker against GeneXus 18**
Run:
`$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj`
Expected: Build SUCCEEDED with 0 errors.

**Step 4: Sync release metadata and update documentation**
Run `python scripts/sync-release-metadata.py` and update `CHANGELOG.md`.

**Step 5: Run complete test suites**
- `dotnet test Genexus18MCP.sln`
- `python -m unittest discover -s scripts/tests`
- `npm test`
