# Open Issues Fixes (#115, #116, #117, #118, #119) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Resolve all 5 open issues (#115, #116, #117, #118 via PR #120, #119) across `GxMcp.Worker` and `GxMcp.Gateway`, with full automated test coverage and live validation on a real GeneXus Knowledge Base.

**Architecture:**
- **Issue #115 (Build Table Homonym):** Disambiguate build targets in `InProcessBuildRunner` by supporting `Type:Name` qualifiers, GUIDs, and prioritizing primary logic objects (`Transaction`, `Procedure`, `WebPanel`, etc.) over auto-generated physical `Table` entities when `ObjectNameHelper.Get` returns null or ambiguous matches.
- **Issue #116 (DataSelector `save_as` Conditions):** Implement native object-model cloning for `DataSelectorStructurePart` in `ObjectService` and wire it into `SaveAsService`, faithfully copying Parameters, Conditions, Orders, and DefinedBy collections.
- **Issue #117 (Attribute Domain Property Persistence):** Route `Domain` / `DomainBasedOn` / `BasedOn` property mutations on Attributes through `AttributeTypeApplier.ApplyDomain` / `DomainPropertyApplier`, ensuring domain existence validation and post-save verification.
- **Issue #118 (WebPanel Partial Clone Rollback):** Integrate validated PR #120 to enforce atomic cleanup and rollback of incomplete clones upon part write failure.
- **Issue #119 (Design System `save_as` Styles):** Replace naive `kop.Name` part discovery in `SaveAsService.FindSource` with `PartAccessor.GetAvailableParts`, ensuring both `Tokens` and `Styles` parts are individually discovered, read, and cloned.
- **Live Validation:** Execute automated live end-to-end tests against a real GeneXus KB over the Streamable HTTP MCP transport.

**Tech Stack:** C#, .NET 8.0 (`GxMcp.Gateway`), .NET Framework 4.8 STA (`GxMcp.Worker`), GeneXus 18 SDK (`Artech.*`), xUnit.

---

### Task 1: Integrate PR #120 (Issue #118 - WebPanel partial clone rollback)

**Files:**
- Modify: `src/GxMcp.Worker/Services/SaveAsService.cs`
- Modify: `src/GxMcp.Worker.Tests/Services/SaveAsServiceTests.cs`
- Modify: `src/GxMcp.Worker.Tests/TestCollections.cs`

**Step 1: Apply PR #120 changes from `origin/pull/120/head`**
- Merge / apply the changes from PR #120 into `SaveAsService.cs`, `SaveAsServiceTests.cs`, and `TestCollections.cs`.
- Ensure `IObjectCloner.DeleteTarget` is called when `!partRes.IsSuccess` on non-skipped parts.

**Step 2: Run test to verify it passes**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SaveAsServiceTests"`
Expected: PASS

**Step 3: Commit**
```bash
git add src/GxMcp.Worker/Services/SaveAsService.cs src/GxMcp.Worker.Tests/Services/SaveAsServiceTests.cs src/GxMcp.Worker.Tests/TestCollections.cs
git commit -m "fix(worker): rollback and clean up target on save_as part failure (#118, #120)"
```

---

### Task 2: Fix Issue #119 (Design System `save_as` missing Styles)

**Files:**
- Modify: `src/GxMcp.Worker/Services/SaveAsService.cs:285-315`
- Test: `src/GxMcp.Worker.Tests/Services/SaveAsServiceTests.cs`

**Step 1: Write the failing test in `SaveAsServiceTests.cs`**
- Test that cloning a `DesignSystem` includes both `Tokens` and `Styles` parts in `partsCloned` and calls `ClonePart` for each.

**Step 2: Run test to verify it fails**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~DesignSystem"`
Expected: FAIL (missing `Styles` in parts list)

**Step 3: Implement fix in `SaveAsService.cs`**
- In `SaveAsService.FindSource`:
  Use `PartAccessor.GetAvailableParts(obj)` to discover parts rather than iterating `obj.Parts` with `kop.Name ?? kop.TypeDescriptor?.Name`.
  When `PartAccessor.IsDesignSystem(obj)`, ensure `["Documentation", "Tokens", "Styles"]` (or available parts) are returned.

**Step 4: Run test to verify it passes**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~DesignSystem"`
Expected: PASS

**Step 5: Commit**
```bash
git add src/GxMcp.Worker/Services/SaveAsService.cs src/GxMcp.Worker.Tests/Services/SaveAsServiceTests.cs
git commit -m "fix(worker): discover and clone both Tokens and Styles in DesignSystem save_as (#119)"
```

---

### Task 3: Fix Issue #116 (DataSelector `save_as` missing conditions & structure)

**Files:**
- Modify: `src/GxMcp.Worker/Services/ObjectService.cs`
- Modify: `src/GxMcp.Worker/Services/SaveAsService.cs:315-345`
- Test: `src/GxMcp.Worker.Tests/Services/SaveAsServiceTests.cs`
- Test: `src/GxMcp.Worker.Tests/Services/DataSelectorReadServiceTests.cs`

**Step 1: Write the failing test**
- Test `SaveAsService` cloning a DataSelector, verifying `CloneDataSelectorStructurePart` copies parameters, conditions, orders, and definedBy.

**Step 2: Run test to verify it fails**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~DataSelector"`
Expected: FAIL

**Step 3: Implement `CloneDataSelectorStructurePart` in `ObjectService.cs` and wire in `SaveAsService.cs`**
- In `ObjectService.cs`:
  Implement `public string CloneDataSelectorStructurePart(string sourceName, string targetName)`:
  1. Retrieve `srcObj` and `tgtObj` as `DataSelector`.
  2. Access `srcPart` (`DataSelectorStructurePart`) and `tgtPart`.
  3. Copy `Parameters`, `Conditions` (`tgtPart.Root.AddCondition`), `Orders`, and `DefinedByAttributes`.
  4. Save `tgtObj` and update index cache.
- In `SaveAsService.cs`:
  Intercept `DataSelectorStructure` or `partName.Equals("Structure", ...)` when `obj is DataSelector`, delegating to `_objects.CloneDataSelectorStructurePart(sourceName, newName)`.

**Step 4: Run test to verify it passes**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~DataSelector"`
Expected: PASS

**Step 5: Commit**
```bash
git add src/GxMcp.Worker/Services/ObjectService.cs src/GxMcp.Worker/Services/SaveAsService.cs src/GxMcp.Worker.Tests/Services/SaveAsServiceTests.cs
git commit -m "fix(worker): preserve conditions and parameters during DataSelector save_as (#116)"
```

---

### Task 4: Fix Issue #117 (Attribute Domain assignment persistence & verification)

**Files:**
- Modify: `src/GxMcp.Worker/Services/PropertyService.cs`
- Test: `src/GxMcp.Worker.Tests/Services/PropertyServiceTests.cs`

**Step 1: Write the failing test in `PropertyServiceTests.cs`**
- Test `SetProperty` on an `Attribute` with `propertyName="Domain"` and `value="SampleDomain"`, verifying that `DomainBasedOn` is assigned and `VerifyPropertyPersisted` confirms persistence.

**Step 2: Run test to verify it fails**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~PropertyServiceTests"`
Expected: FAIL

**Step 3: Implement Domain assignment in `PropertyService.cs`**
- In `PropertyService.SetProperty` and `ApplyPropertyValue`:
  - When target is `Attribute` or `Domain` and `propName` is `"Domain"`, `"DomainBasedOn"`, or `"BasedOn"`:
    - If `value` is non-empty, lookup domain via `_objectService.FindObject(value, "Domain")`. If not found, return `ObjectNotFound` / `DomainNotFound`.
    - Apply via `DomainPropertyApplier.ApplyDomainBasedOn(container, domainObj)` / `AttributeTypeApplier.ApplyDomain(container, domainObj)`.
    - If `value` is empty, call `DomainPropertyApplier.ClearDomainBasedOn(container)`.
- In `PropertyService.VerifyPropertyPersisted`:
  - When `propName` is `"Domain"`, `"DomainBasedOn"`, or `"BasedOn"`, read `DomainPropertyApplier.GetDomainBasedOnName(fresh)` and compare against requested value.

**Step 4: Run test to verify it passes**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~PropertyServiceTests"`
Expected: PASS

**Step 5: Commit**
```bash
git add src/GxMcp.Worker/Services/PropertyService.cs src/GxMcp.Worker.Tests/Services/PropertyServiceTests.cs
git commit -m "fix(worker): persist and verify Attribute Domain assignment via PropertyService (#117)"
```

---

### Task 5: Fix Issue #115 (Build target resolution on Table homonym)

**Files:**
- Modify: `src/GxMcp.Worker/Services/InProcessBuildRunner.cs`
- Test: `src/GxMcp.Worker.Tests/Services/InProcessBuildRunnerTests.cs`
- Test: `src/GxMcp.Worker.Tests/Services/BuildServiceTests.cs`

**Step 1: Write failing tests for Table homonym target resolution**
- Test `CountResolvableTargets` and target resolution when both Transaction and Table exist with the name `SampleEntity`.
- Test type-qualified targets e.g. `Transaction:SampleEntity` and GUID strings.

**Step 2: Run test to verify it fails**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~InProcessBuildRunner"`
Expected: FAIL

**Step 3: Implement target resolution in `InProcessBuildRunner.cs`**
- Implement `ResolveTargetKBObject(object kbHandle, object designModel, string target)`:
  1. Parse `Type:Name` prefix (e.g. `Transaction:SampleEntity`) if present.
  2. Try `Objects.Get(Guid)` if target is a Guid string.
  3. Try `ObjectNameHelper.Get(designModel, simpleName)`. If it returns a `Table` or `null`, probe primary candidate logic types (`Transaction`, `Procedure`, `WebPanel`, `SDPanel`, `SDT`, `DataProvider`, `DataSelector`) using SDK typed getters or `designModel.Objects.GetByName(null, null, simpleName)`.
  4. Ensure `CountResolvableTargets`, `ExecuteBuildBatch`, `ExecuteBuildWithTheseOnly`, `ExecuteCompileOnly`, and `ExecuteBuildOne` use this resolved `KBObject` (and pass clean `ObjectName` to MSBuild).

**Step 4: Run test to verify it passes**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~InProcessBuildRunner"`
Expected: PASS

**Step 5: Commit**
```bash
git add src/GxMcp.Worker/Services/InProcessBuildRunner.cs src/GxMcp.Worker.Tests/Services/InProcessBuildRunnerTests.cs
git commit -m "fix(worker): resolve build targets with Table homonyms and type qualifiers (#115)"
```

---

### Task 6: Run Full Solution Test Suite & CHANGELOG update

**Files:**
- Modify: `CHANGELOG.md`

**Step 1: Run complete test suite across Gateway, Worker, and CLI**
Run:
```powershell
dotnet test Genexus18MCP.sln
npm test
```
Expected: All tests pass.

**Step 2: Update `CHANGELOG.md` under `## Unreleased`**
- Add entries under `### Fixed`:
  - Build target resolution when a Transaction shares its name with an auto-generated Table (#115).
  - DataSelector `save_as` condition and parameter cloning (#116).
  - Attribute `Domain` property assignment persistence and verification (#117).
  - WebPanel `save_as` atomic cleanup on partial failure (#118, PR #120).
  - DesignSystem `save_as` Styles part cloning (#119).

**Step 3: Commit**
```bash
git add CHANGELOG.md
git commit -m "docs: update CHANGELOG for issues #115, #116, #117, #118, #119"
```

---

### Task 7: Live KB Validation on Real GeneXus Knowledge Base

**Validation Procedure (via Streamable HTTP MCP on scratch gateway or live session):**
1. **Build & Deploy:** Run `.\build.ps1` to produce fresh `publish/` binaries.
2. **Issue #115 Live Test:**
   - Call `genexus_lifecycle action=build target=SampleEntity` where `SampleEntity` has an associated Table with the same name.
   - Assert: Target is resolved as `Transaction` and build succeeds without `"none of the 1 requested target(s) resolved to a KBObject"`.
3. **Issue #116 Live Test:**
   - Create DataSelector `LiveSelectorSrc` with parameter and condition `CustomerId = &CustomerId`.
   - Call `genexus_create action=save_as name=LiveSelectorSrc type=DataSelector newName=LiveSelectorDst`.
   - Call `genexus_read name=LiveSelectorDst part=structure`.
   - Assert: `conditions` and `parameters` are present and non-empty.
4. **Issue #117 Live Test:**
   - Create Domain `LiveDomain` and Attribute `LiveAttr`.
   - Call `genexus_properties action=set name=LiveAttr type=Attribute propertyName=Domain value=LiveDomain`.
   - Re-read `genexus_properties action=get name=LiveAttr type=Attribute`.
   - Assert: `DomainBasedOn` is `LiveDomain` and verified.
5. **Issue #119 Live Test:**
   - Create DesignSystem `LiveDsoSrc` with `styles ThemeName { .Class1 { color: red; } }`.
   - Call `genexus_create action=save_as name=LiveDsoSrc type=DesignSystem newName=LiveDsoDst`.
   - Call `genexus_read name=LiveDsoDst part=Styles`.
   - Assert: `Styles` part contains `.Class1 { color: red; }`.
6. **Issue #118 Live Test:**
   - Trigger partial failure during `save_as` and verify target object is cleanly removed from the KB.
7. **Cleanup:** Delete all test objects from the KB.
