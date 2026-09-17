# Legacy GeneXus Dynamic Support Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable graceful, best-effort compatibility for legacy GeneXus versions (Evolution 1-3, GeneXus 15, and GeneXus 9.0) without requiring compile-time SDK DLL dependencies for those versions.

**Architecture:** Extend the Gateway and CLI version detection pipeline to identify legacy installations (Registry, FileVersionInfo, and KB formats like `.gxi` and `.gxw` 10.x), tag them with driver profiles (`native-sdk`, `dotnet-reflection`, `com-gxpublic`), and introduce dynamic reflection and COM late-binding adapters so core operations (read, query, edit, inspect, doc) function with graceful feature degradation on unsupported modern tools (DSO, API, GAM).

**Tech Stack:** C# .NET 10 (Gateway), C# .NET Framework 4.8 / STA (Worker), Node.js (CLI detection), COM IDispatch Interop.

---

### Task 1: Catalog & Version Profile Updates (`gx-versions.json`)

**Files:**
- Modify: `config/gx-versions.json`
- Test: `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs`

**Step 1: Write the failing test**
Add test in `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs` asserting that `GeneXusVersionCatalog` recognizes `10.3` (Evolution 3) and `9` (GeneXus 9.0) with their corresponding driver profiles.

**Step 2: Run test to verify it fails**
```powershell
dotnet test src\GxMcp.Gateway.Tests --filter WhoamiVersionTests
```
Expected: FAIL (10.3 and 9 not yet in catalog / recognized).

**Step 3: Update `config/gx-versions.json`**
Add entries for `10.3` (GeneXus Evolution 3), `10.2` (Ev2), `10.1` (Ev1), `15` (GeneXus 15), and `9` (GeneXus 9.0) with:
- `driver`: `"dotnet-reflection"` or `"com-gxpublic"`
- `defaultInstallPath`: standard directories
- `registryNames` and `legacyRegistryVersions`

**Step 4: Run test to verify it passes**
```powershell
dotnet test src\GxMcp.Gateway.Tests --filter WhoamiVersionTests
```

---

### Task 2: Gateway Version Detection & Probe Refinement

**Files:**
- Modify: `src/GxMcp.Gateway/GeneXusVersionCatalog.cs`
- Modify: `src/GxMcp.Gateway/WorkerSdkCompatibilityProbe.cs`
- Test: `src/GxMcp.Gateway.Tests/WorkerSdkCompatibilityProbeTests.cs`

**Step 1: Write the failing test**
In `WorkerSdkCompatibilityProbeTests.cs`, add test cases for:
- Detecting version `10.3.x.x` (Ev3) from `GeneXus.exe` or `Artech.Architecture.Common.dll`.
- Detecting version `9.0.x.x` when `Artech.Architecture.Common.dll` is absent but `gx.exe` / `gxdl32.dll` exists.

**Step 2: Run test to verify it fails**
```powershell
dotnet test src\GxMcp.Gateway.Tests --filter WorkerSdkCompatibilityProbeTests
```

**Step 3: Implement minimal code**
- Update `MajorRegex` in `GeneXusVersionCatalog.cs` to match decimal prefixes for the 10.x era: `@"^\s*(?<major>10\.[1-3]|\d+)"`.
- In `WorkerSdkCompatibilityProbe.cs`, add fallback anchor check: if `Artech.Architecture.Common.dll` does not exist, check for `gx.exe` or `gxdl32.dll`.
- Return appropriate diagnostic code `GXMCP_SDK_LEGACY_COMPATIBLE` or `GXMCP_SDK_COMPATIBLE`.

**Step 4: Run test to verify it passes**
```powershell
dotnet test src\GxMcp.Gateway.Tests --filter WorkerSdkCompatibilityProbeTests
```

---

### Task 3: CLI Detection Expansion (Registry & KB Identity)

**Files:**
- Modify: `cli/lib/config.js`
- Test: `cli/run.test.js`

**Step 1: Write the failing test**
Add tests in `cli/run.test.js`:
- Reading KB identity from `.gxi` file (GeneXus 9.0 format) when `.gxw` is absent.
- Reading KB identity from `.gxw` containing `<VersionNumber>10.3.0</VersionNumber>` -> major `10.3`.

**Step 2: Run test to verify it fails**
```powershell
npm test -- -t "KB identity"
```

**Step 3: Implement minimal code**
- In `cli/lib/config.js`:
  - Enhance `getGeneXusMajor(version)` to preserve `10.1`, `10.2`, `10.3` as distinct majors.
  - In `readGeneXusKbIdentity(kbPath)`: if no `.gxw` file is found, check for `.gxi` files. If present, return `{ version: '9.0', major: '9', source: 'gxi-classic' }`.
  - In `discoverGeneXusFromRegistry()`: search keys for Ev3 (`GeneXus\10.3`) and GX 9.0 (`GeneXus\9.0`).

**Step 4: Run test to verify it passes**
```powershell
npm test -- -t "KB identity"
```

---

### Task 4: Dynamic SDK Reflection Invoker & Graceful Degradation in Worker

**Files:**
- Create/Modify: `src/GxMcp.Worker/Compatibility/DynamicSdkBridge.cs`
- Modify: `src/GxMcp.Worker/Compatibility/OptionalSdkInvoker.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Test: `src/GxMcp.Worker.Tests/SdkCapabilityContractTests.cs`

**Step 1: Write the failing test**
Test that when a modern tool (e.g. `genexus_gam`, `genexus_apply_pattern`, `genexus_generator_reference`) is invoked on a worker configured with a legacy driver, it returns a structured degradation response `{ ok: false, error: "UNSUPPORTED_IN_GENEXUS_VERSION", major: "10.3" }` rather than throwing an unhandled exception.

**Step 2: Run test to verify it fails**
```powershell
dotnet test src\GxMcp.Worker.Tests --filter SdkCapabilityContractTests
```

**Step 3: Implement minimal code**
- In `CommandDispatcher.cs`, add a capability matrix check against the active GeneXus major.
- For unsupported tools in that version, return early with an informative diagnostic error explaining which version introduced the feature.
- Ensure `OptionalSdkInvoker` provides dynamic late-binding fallbacks for `KBObject` part extraction (`Parts.Get()` vs `Parts.GetPart()`).

**Step 4: Run test to verify it passes**
```powershell
dotnet test src\GxMcp.Worker.Tests --filter SdkCapabilityContractTests
```

---

### Task 5: COM Driver Architecture for GeneXus 9.0

**Files:**
- Create: `src/GxMcp.Worker/Drivers/ComGxPublicDriver.cs`
- Modify: `src/GxMcp.Worker/Program.cs`
- Test: `src/GxMcp.Worker.Tests/ComDriverContractTests.cs`

**Step 1: Write the failing test**
Create contract tests asserting that when `--driver=com` is specified, `Program` selects `ComGxPublicDriver` and handles basic lifecycle requests (`open`, `read`, `query`).

**Step 2: Run test to verify it fails**
```powershell
dotnet test src\GxMcp.Worker.Tests --filter ComDriverContractTests
```

**Step 3: Implement minimal code**
- Implement `ComGxPublicDriver` using C# `dynamic` over `Type.GetTypeFromProgID("GXPublic.Application")`.
- Map MCP core operations (`query`, `read`, `edit`) to GXPublic COM methods.

**Step 4: Run test to verify it passes**
```powershell
dotnet test src\GxMcp.Worker.Tests --filter ComDriverContractTests
```

---

### Task 6: End-to-End Validation & Documentation

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/mcp_capabilities_inventory.md`
- Run full test suite:
  ```powershell
  dotnet test Genexus18MCP.sln
  npm test
  ```
