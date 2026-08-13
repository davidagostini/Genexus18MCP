using System.Collections.Generic;

namespace GxMcp.Gateway
{
    internal static class ToolHelpCatalog
    {
        private static readonly Dictionary<string, string> _helpTexts = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["genexus_query"] =
                "# genexus_query\n\n" +
                "Search objects in the active Knowledge Base.\n\n" +
                "## Query prefixes\n" +
                "- `usedby:<name>` — objects that reference <name>\n" +
                "- `type:<ObjectType>` — filter by Transaction, Procedure, WebPanel, etc.\n" +
                "- `description:<text>` — search inside object descriptions\n" +
                "- `parent:<folder>` — filter by direct parent folder\n" +
                "- `parentPath:<a/b/c>` — filter by full folder path\n\n" +
                "## Index behaviour\n" +
                "- The first call on a fresh install triggers the KB index build.\n" +
                "- `_meta.partial=true` means more results are still being indexed.\n" +
                "- Literal-name queries (no prefix) skip the index entirely.\n" +
                "- `genexus_read`, `genexus_edit`, `genexus_list_objects`, and `genexus_lifecycle` are index-independent.\n\n" +
                "## Defaults\n" +
                "- `axiCompact: true` — pass `false` to get the full payload.\n" +
                "- `limit: 50`, `offset: 0`.\n\n" +
                "## Examples\n" +
                "- `{ query: 'type:Procedure', limit: 20 }`\n" +
                "- `{ query: 'usedby:InvoiceProc' }`\n" +
                "- `{ query: 'OrderTrn', fields: 'name,type,path,description' }`\n",

            ["genexus_lifecycle"] =
                "# genexus_lifecycle\n\n" +
                "Build, validate, index, or poll the active Knowledge Base.\n\n" +
                "## Actions\n" +
                "- `build` — non-blocking when `estimated_seconds >= 20`; returns `{ operationId, job_id, status: 'running', pollTarget: 'op:<id>' }` and surfaces `_meta.background_jobs` on the next call. Pass `wait_until_done: true` to block until terminal (single turn instead of polling).\n" +
                "- `validate` — inline validation/specifier check. Returns the result in the same call; it does not currently use the background-job path.\n" +
                "- `index` — rebuilds the search index. Pass `force=true` to ignore the on-disk cache.\n" +
                "- `status` — accepts either a `taskId` or `job_id` via `target`; pass `wait_seconds > 0` to long-poll up to 600s.\n" +
                "- `result` — fetch the completion payload of a finished operation.\n" +
                "- `stop-worker` — gracefully recycle the worker process for the active KB.\n\n" +
                "## target format\n" +
                "- Build/validate: object name(s), comma- or semicolon-separated.\n" +
                "- Status/result on a background op: `op:<operationId>` or just `<job_id>`.\n\n" +
                "## Build-evidence checklist (issue #42)\n" +
                "A GeneXus build can report `Status: Succeeded` (0 errors/0 warnings) without the generated `.cs` actually landing on disk. Do NOT treat `Succeeded` alone as proof your edit was compiled. On every build result:\n" +
                "1. **Check `effective_status`.** `SucceededWithGaps` means the build reported success but the evidence gate found no fresh generated `.cs` for one or more targets — the code you edited may NOT be regenerated. Treat it as a soft failure and investigate before moving on.\n" +
                "2. **Read `generateEvidence`.** `{ ok, objectsChecked, objectsBuilt, filesWritten[], staleOrMissing[], referencedButNotBuilt[]? }`. `staleOrMissing` lists targets whose `.cs` is older than the build start (or absent). `referencedButNotBuilt` appears when `includeCallees: none` dropped objects your target calls — rebuild with `includeCallees: direct|transitive` to regenerate them.\n" +
                "3. **Check `staleGenerated`.** Objects edited via the MCP this session that have not been successfully rebuilt since. Build them (or a full build) before you rely on the generated output.\n" +
                "4. **A second build is refused with `status: BuildAlreadyRunning`** while one is in flight (builds serialize per worker). Poll `activeTaskId` or cancel it first; opt out with env `GXMCP_ALLOW_CONCURRENT_BUILDS=1`.\n" +
                "5. A build that stops making progress (phase/counts frozen) is force-failed after `GXMCP_BUILD_NOPROGRESS_SEC` (default 180s; 0 disables) instead of sitting `Running` for the full timeout.\n\n" +
                "## Examples\n" +
                "- `{ action: 'build', target: 'InvoiceProc' }`\n" +
                "- `{ action: 'status', target: 'op:abc123', wait_seconds: 600 }`\n" +
                "- `{ action: 'build', target: 'InvoiceProc', wait_until_done: true }`\n" +
                "- `{ action: 'index', force: true }`\n",

            ["genexus_edit"] =
                "# genexus_edit\n\n" +
                "Edit the source or metadata of a GeneXus object.\n\n" +
                "## Required\n" +
                "- Either `name` (single object) **or** `targets` (array) — never both.\n" +
                "- `mode`: `full` (replace whole part) or `patch` (Replace/Insert_After/Append over a context anchor).\n" +
                "- `dryRun: true` with `mode: 'patch'` first to preview without persisting.\n\n" +
                "## Output\n" +
                "- Returns `post_state.diff` (unified diff) by default.\n" +
                "- `verbose: true` adds slices with ±15 lines of context.\n" +
                "- `return_post_state: false` opts out of the post-state block to save tokens.\n" +
                "- `async: true` returns immediately with `operationId` / `job_id`; poll `genexus_lifecycle action=status|result target=op:<id>` for completion.\n\n" +
                "## Patch persistence verification\n" +
                "Source and Rules are always re-read after the single SDK save. `verifyMode: 'normalized'` is the default and tolerates EOL, encoding marker, trailing-whitespace, and repeated-blank-line rendering by the SDK; `exact` preserves comments, whitespace, and blank lines but treats CRLF/LF as the same logical Source representation; `semantic` also tolerates harmless SDK casing/spacing changes. Comment-only Replace writes require `baseVersion`, are verified against the requested comment, report active old-statement presence, and return `CommentOnlyWriteNotPersisted` if the SDK re-read diverges. The response separates `saved` from `verified` and includes raw/normalized hashes, `normalizationApplied`, `diffNormalized`, `matchCount`, `persistedMatchCount`, `oldContentPresent`, `replacementPresent`, `reReadConfirmed`, and `implicitOperations` (always empty for this path). A mismatch is never reported as Applied. Rollback occurs only with `rollbackOnFailure: true` and a valid snapshot, and reports its own save/verification hashes. Pass the prior read's `versionToken` as `baseVersion` to reject concurrent edits. No Specify, Generate, Build, Rebuild, compilation, reorganization, execution, or tests are invoked by a patch write.\n\n" +
                "## Disambiguation\n" +
                "If `name` matches multiple objects, the error includes `suggestion` and `availableTypes`. Pass `type=<ObjectType>` or use `parentPath` to disambiguate.\n\n" +
                "## Examples (source code)\n" +
                "- `{ name: 'InvoiceProc', part: 'Source', mode: 'patch', operation: 'Replace', context: '<old block>', content: '<new block>', dryRun: true }`\n" +
                "- `{ name: 'OrderTrn', part: 'Rules', mode: 'full', content: '<rules text>' }`\n\n" +
                "## Editing WorkWithPlus pattern parts (PatternInstance / PatternVirtual)\n" +
                "Pattern XML is the IDE's structural model — containers, controls, actions, grids, orders, filters all live there. **Both `mode: full` and `mode: patch` work**; the MCP handles the SDK quirks transparently.\n\n" +
                "### Auto-reconcile `childrenOrderedList`\n" +
                "WorkWithPlus stores IDE rendering order in a per-parent `childrenOrderedList` attribute. **You don't need to manage it.** On every pattern write the MCP rebuilds (and creates if missing) every list from the actual child order in your XML, dropping orphans and adding new entries. The response includes a `childrenOrderedListReconciliation` block listing what changed and why — read it back to confirm your changes will render.\n\n" +
                "### Element kinds (XML node → IDE control)\n" +
                "- `<textBlock controlName=\"...\" caption=\"...\" themeClass=\"BigTitle|LinkText|...\" format=\"HTML\" />`\n" +
                "- `<errorViewer defaultThemeClass=\"ErrorViewer\" />`\n" +
                "- `<attribute attribute=\"<guid>-<FieldName>\" themeClass=\"Attribute\" isRequired=\"True\" NoAccept=\"True\" />`\n" +
                "- `<gridAttribute>` / `<filterAttribute>` / `<descriptionAttribute>`\n" +
                "- `<standardAction name=\"Trn_Enter|Trn_Cancel|Trn_Delete|Insert|Update|Delete|Export|...\" caption=\"...\" buttonClass=\"btn ButtonGreen\" />` — only these registered names; **the SDK rejects unknown standardAction names**.\n" +
                "- `<userAction name=\"AnyName\" caption=\"...\" buttonClass=\"btn ButtonBlue\" confirm=\"False\" />` — use this for custom buttons like Duplicate, Audit, Export, etc.\n" +
                "- `<table name=\"...\" isGroup=\"True\" title=\"Section title\" groupThemeClass=\"GroupTela|GroupTelaResp|GroupFiltro\">...</table>` — groups (named sections).\n" +
                "- `<order name=\"...\"><attribute attribute=\"<guid>-Field\" /></order>` inside `<orders>` (Selection view).\n" +
                "- `<rule Name=\"...\" Rule=\"<SDK rule text>\" />` inside `<rules>`.\n" +
                "- `<eventBlock BlockName=\"...\" />` inside `<events>`.\n\n" +
                "### Transaction vs Selection views (XPath split)\n" +
                "- Transaction (form view, `/instance/transaction/...`): TableMain → TableContent (attributes) → TableActions (Trn_Enter/Cancel/Delete buttons).\n" +
                "- Selection (list view, `/instance/level/selection/...`): TableSearch (filters) → `<orders>` → TableGridHeader → `<grid>` (gridAttributes).\n" +
                "- Edit one without touching the other.\n\n" +
                "### Theme classes\n" +
                "Run `genexus_list_objects --typeFilter ThemeClass --nameFilter <Button|TextBlock|Title|...>` to discover the actual class names in this KB (they vary per design system). Common patterns: `themeClass=\"BigTitle\"`, `themeClass=\"LinkText\"`, `groupThemeClass=\"GroupTelaResp\"`, `cellThemeClass=\"TableTitleCell\"`. Buttons use `buttonClass=\"btn <ColorClass>\"` (e.g. `btn ButtonGreen`, `btn ButtonRed`).\n\n" +
                "### \"Apply this pattern on save\" override\n" +
                "When that checkbox is on (the default), WorkWithPlus recomputes some attributes after every save — notably `title` on top-level groups. Toggle it via `genexus_properties --action set --name WorkWithPlus<Object> --propertyName SDPlus_Editor_Apply_On_Save --value False` to keep hard overrides.\n\n" +
                "### Pattern examples\n" +
                "- Add a custom button: `{ name: 'WorkWithPlusAcao', part: 'PatternInstance', mode: 'patch', operation: 'Insert_After', context: '<existing Trn_Delete standardAction line>', content: '<userAction caption=\"Auditar\" name=\"Auditar\" buttonClass=\"btn ButtonCinza\" confirm=\"False\" />' }`\n" +
                "- Wrap attributes in a styled group (full rewrite): `{ name: 'WorkWithPlusAcao', part: 'PatternInstance', mode: 'full', content: '<full <instance> XML with <table isGroup=\"True\" title=\"Identificação\" groupThemeClass=\"GroupTelaResp\">...>' }`\n" +
                "- Add a Selection ordering: insert `<order name=\"Por código\"><attribute attribute=\"<guid>-FieldName\" /></order>` inside `<orders>`; childrenOrderedList is auto-updated.\n",

            ["genexus_analyze"] =
                "# genexus_analyze\n\n" +
                "Semantic analysis across one or more objects.\n\n" +
                "## Modes\n" +
                "- `impact` — callers, callees, blast radius, risk level, affected entry points.\n" +
                "- `dependencies` — typed dependency graph.\n" +
                "- `complexity` — line/cyclomatic counts.\n" +
                "- `naming` — naming-convention audit.\n" +
                "- `summary` — LLM-oriented summary of the object.\n" +
                "- `explain` — natural-language explanation of a slice of source.\n\n" +
                "## When to use what\n" +
                "- Raw source: `genexus_read`.\n" +
                "- Single-object metadata: `genexus_inspect`.\n" +
                "- Cross-object reasoning: `genexus_analyze`.\n\n" +
                "## Notes\n" +
                "- `impact` waits up to 30s for the index to be ready unless `waitForIndex: false`.\n" +
                "- Returns `callersTruncated: true` and `_meta.partial` when the graph is incomplete.\n\n" +
                "## Examples\n" +
                "- `{ mode: 'impact', target: 'InvoiceProc' }`\n" +
                "- `{ mode: 'summary', target: 'OrderTrn' }`\n",

            ["genexus_variable"] =
                "# genexus_variable\n\n" +
                "Add, delete, or modify variables in an object's Variables part.\n\n" +
                "## Required\n" +
                "- `action` — `add`, `delete`, or `modify`\n" +
                "- `name` — object that owns the variable\n" +
                "- `varName` — variable name, including `&` when that is how the KB stores it\n" +
                "- `typeName` — required for `add` and `modify`\n\n" +
                "## Optional\n" +
                "- `basedOn` — domain name for compatible typed variables\n" +
                "- `async: true` returns immediately with `operationId` / `job_id`; poll `genexus_lifecycle action=status|result target=op:<id>` for completion.\n\n" +
                "## Notes\n" +
                "- GAM / WWP+ framework-managed variables are protected and return a refusal instead of mutating them.\n" +
                "- `modify` preserves the variable name and description while changing the type atomically.\n\n" +
                "## Examples\n" +
                "- `{ action: 'add', name: 'InvoiceProc', varName: '&Total', typeName: 'Numeric(10.2)' }`\n" +
                "- `{ action: 'modify', name: 'InvoiceProc', varName: '&State', typeName: 'Character(20)', async: true }`\n" +
                "- `{ action: 'delete', name: 'InvoiceProc', varName: '&ScratchFlag' }`\n",

            ["genexus_read"] =
                "# genexus_read\n\n" +
                "Read source or metadata parts of one or more GeneXus objects.\n\n" +
                "## Required\n" +
                "- Either `name` (single) **or** `targets` (array). Never both.\n" +
                "- `parts`: array of part names. Common: `Source`, `Variables`, `Rules`, `Events`, `Structure`, `Layout`. Omitting `parts` returns the canonical default set for the object type.\n\n" +
                "## Data Selectors\n" +
                "- Use `type: 'DataSelector'` with `parameters`, `conditions`, `orders`, `definedBy`, `baseTransaction`, `baseTable`, or `structure`. The SDK order and complete expressions are preserved.\n" +
                "- `projection` and resolved `joins` are not exposed by the GeneXus 18 U16 public SDK. Requests return a technical reason in `unsupportedParts`, never a misleading empty value.\n" +
                "- Base table and Transaction are returned only when attribute coverage resolves them unambiguously. Declared indexes may be listed, but this read never runs Specify to claim which index is used.\n\n" +
                "- `structure.expressionKind: 'semanticProjection'` means the complete view was composed from typed SDK elements; U16's raw structure `ToString()` is not returned because it leaks internal collection type names.\n\n" +
                "## Pagination\n" +
                "- `offset` and `limit` apply to the **source** part for large objects.\n" +
                "- `_meta.partial: true` and `_meta.nextOffset` signal more content available.\n\n" +
                "## Examples\n" +
                "- `{ name: 'InvoiceProc', parts: ['Source', 'Variables'] }`\n" +
                "- `{ name: 'OrderTrn', parts: ['Rules'], offset: 0, limit: 200 }`\n" +
                "- `{ name: 'OrderFilter', type: 'DataSelector', parts: ['parameters', 'conditions', 'orders', 'definedBy', 'baseTable'] }`\n" +
                "- `{ targets: [{ name: 'A' }, { name: 'B' }], parts: ['Source'] }`\n",

            ["genexus_apply_pattern"] =
                "# genexus_apply_pattern\n\n" +
                "Apply a GeneXus pattern to a KBObject — equivalent to the IDE's `Right-click → Apply Pattern` menu. " +
                "Currently registered: `WorkWithPlus` (alias `WWP`).\n\n" +
                "## When to use this — and when NOT to\n" +
                "**Use this** any time the user asks for a WorkWithPlus / Work With Plus / WWP screen on a new or existing object. " +
                "`apply_pattern` is the *only* path that creates a real `PatternInstance` — once that exists, `genexus_edit part=PatternInstance` can shape columns, actions, filters, orders, grids, themes, etc.\n\n" +
                "**Do NOT** try to recreate a WWP screen by editing `WebForm` / `Layout` directly. The HTML generator will compile fine, but the result is a hand-built page that lacks WWP's grid/filter/action infrastructure. " +
                "If a target object already has `PatternInstance`, edit *that* part instead of `WebForm` — see `EditingWebFormUnderPattern` warning surfaced by `genexus_edit`.\n\n" +
                "## Two real target shapes — both work\n\n" +
                "### A) Transaction target — generates the WW family\n" +
                "Classic CRUD-around-an-entity flow. The engine emits:\n\n" +
                "- `WorkWithPlus<Trn>` — pattern host (edit `PatternInstance` here to shape the screen)\n" +
                "- `WW<Trn>` — Selection WebPanel (list view)\n" +
                "- `View<Trn>` — detail WebPanel\n" +
                "- `ExportWW<Trn>` / `ExportReportWW<Trn>` — export procedures\n\n" +
                "```jsonc\n" +
                "{ \"name\": \"Invoice\", \"pattern\": \"WorkWithPlus\" }\n" +
                "// Generates: WorkWithPlusInvoice + WWInvoice + ViewInvoice + ExportWWInvoice + ExportReportWWInvoice\n" +
                "```\n\n" +
                "### B) WebPanel, WebComponent or SDPanel target — in-place attach + auto-project (SOTA path)\n" +
                "For custom WWP screens that aren't pure CRUD (queries, dashboards, hybrid lists and reusable components). The MCP attaches a `WorkWithPlus<ObjectName>` host bound to the original object via the SDK's `PatternInstancePackageInterface.CreatePatternInstanceWithTemplate`, then immediately runs `IPatternBuildProcess.UpdateParentObject` so its WebForm reflects the pattern projection. The original object keeps its type and name.\n\n" +
                "Required: `settings.template` matching a `WorkWithPlus for Web Template` object in your KB. Common names: `MatIsoTemplate`, `TransactionResp2`, `PopoverEmpty`, `TransactionPopUp`. The MCP auto-discovers one if you omit, but explicit is better.\n\n" +
                "```jsonc\n" +
                "{ \"name\": \"InvoiceQueryPanel\",\n" +
                "  \"pattern\": \"WorkWithPlus\",\n" +
                "  \"settings\": { \"template\": \"MatIsoTemplate\" } }\n" +
                "// → status: Success, directAttach: true, template: \"MatIsoTemplate\"\n" +
                "// → patternHost: \"WorkWithPlusInvoiceQueryPanel\" (host with editable PatternInstance)\n" +
                "// → InvoiceQueryPanel.WebForm now contains the template-derived layout\n" +
                "```\n\n" +
                "**Auto-project on edit:** subsequent `genexus_edit name=WorkWithPlus<X> part=PatternInstance` calls automatically run UpdateParentObject too — every PatternInstance edit lands on the parent object's WebForm in the same call. The response's `projection.status` field reports the outcome.\n\n" +
                "## Response\n" +
                "- `{ status: \"Success\", wasFirstApply: true|false, generatedObjects: [...] }` on the happy path.\n" +
                "- `{ status: \"pattern_unavailable\", message: ... }` if `Artech.Packages.Patterns.dll` / license is missing — the call is **non-fatal**, treat as \"feature unavailable on this install\" and surface the message.\n" +
                "- `{ status: \"Error\", error: ... }` on SDK exceptions.\n\n" +
                "## Re-apply\n" +
                "Pass `reapply: true` to regenerate over an existing instance — equivalent to `Right-click → Apply Pattern` on an already-patterned object. If no instance exists, it falls back to first-time apply automatically.\n\n" +
                "## Settings\n" +
                "The `settings` JObject is **best-effort-projected** onto the SDK's `ApplySettings` instance on re-apply (case-insensitive property match, recursive on nested objects). Mismatched keys are silently dropped and logged — they don't fail the call.\n\n" +
                "Caveats: (a) projection only fires on `reapply: true`; first-time apply uses the SDK's void overload which has no settings slot. (b) the canonical names of `ApplySettings` properties are pattern-internal; if your projection doesn't take effect, fall back to shaping the result via `genexus_edit part=PatternInstance` after apply.\n",

            ["genexus_create"] =
                "# genexus_create\n\n" +
                "Create a new empty GeneXus object in the active KB (`action: object`, the default). The tool covers every KBObject the IDE can create — both objects with a typed wrapper (Transaction, Procedure, WebPanel, SDT, DataProvider, DataSelector, Domain, Attribute, Table, Index, ExternalObject, Theme, Image, Menu, Menubar, Stencil, UserControl, WorkPanel, Report, API, URLRewrite, MiniApp, SuperApp, DesignSystem, ColorPalette, OfflineDatabase, DataView, Group, Language) and Guid-only types (SDPanel, Dashboard, Query, QueryDashboard, WorkflowDiagram, ConversationalFlows, TestSuite, ThemeClass, ThemeColor, ThemeTransformation, DesignSystemClass, WorkWithDevices, WorkWithWeb, WikiPageKBObject, TranslationMessage, DataStoreCategory, GeneratorCategory, DeploymentUnitCategory).\n\n" +
                "Aliases accepted: `StructuredDataType`→SDT, `BusinessProcessDiagram`/`BPD`→WorkflowDiagram, `PanelForSD`→SDPanel.\n\n" +
                "## Defaults that get seeded\n" +
                "- `Transaction` — gets a default `<Name>Id : Numeric(8,0) [Key]` attribute so the SDK accepts the empty save.\n" +
                "- `SDT` — gets a default `Item1 : VARCHAR(40)` item.\n" +
                "- `Procedure` / `DataProvider` — empty source with a `// Procedure: <Name>` header.\n" +
                "- `Domain` — defaults to `Character(20)` when no `dataType` is supplied.\n\n" +
                "When the response carries `_meta.seeded`, the caller knows what's already there and can decide whether to overwrite (`genexus_edit part=Structure mode=full`).\n\n" +
                "## Domain (new)\n" +
                "Pass either a primitive shape (`dataType` + `length`/`decimals`/`signed`) or `basedOn=<existingDomain>`. For an **enumerated domain**, add `enumValues=[{name,value}...]`.\n" +
                "- The `value` for a Character/VarChar domain must be a quoted literal — e.g. `\"\\\"A\\\"\"` (string `\"A\"`). For Numeric, pass the literal number as a string (`\"1\"`).\n" +
                "- Response `_meta` echoes back `dataType`, `length`, `enumValues` (and `enumError`/`typeError` if any step failed best-effort).\n" +
                "- Replace attributes by the new domain via `genexus_edit name=<Attr> part=Structure` setting `DomainBasedOn=<DomainName>`, then `genexus_delete_object` for the now-redundant attributes.\n\n" +
                "Example — exactly the Edgar `UserStatus` case:\n" +
                "```json\n" +
                "{\n  \"type\": \"Domain\",\n  \"name\": \"UserStatus\",\n  \"dataType\": \"Character\",\n  \"length\": 10,\n  \"enumValues\": [\n    {\"name\":\"Active\",   \"value\":\"\\\"A\\\"\", \"description\":\"Cuenta Normal\"},\n    {\"name\":\"Inactive\", \"value\":\"\\\"I\\\"\", \"description\":\"Cuenta inactiva\"},\n    {\"name\":\"Blocked\",  \"value\":\"\\\"B\\\"\", \"description\":\"Bloqueada por exceso de intentos\"}\n  ]\n}\n```\n\n" +
                "## WebPanel / SDPanel hint\n" +
                "An empty WebPanel is just a blank page — it has **no WorkWithPlus pattern** by default. " +
                "If the goal is a WWP-style screen (list with filters, actions, grid), the next call should be `genexus_apply_pattern name=<X> pattern=WorkWithPlus`, then shape via `genexus_edit part=PatternInstance`. " +
                "The response surfaces this in `_meta.patternHint` so the agent doesn't drift into editing `WebForm` by hand.\n\n" +
                "For popup-style WebPanels with structured inputs/buttons, prefer `genexus_create_popup` — it emits a fully-wired popup in one call.\n\n" +
                "## More examples\n" +
                "- `{ type: \"Transaction\", name: \"Invoice\" }`\n" +
                "- `{ type: \"WebPanel\", name: \"InvoiceList\" }` — then call `genexus_apply_pattern` if WWP is wanted.\n" +
                "- `{ type: \"Procedure\", name: \"BillingCalc\" }`\n" +
                "- `{ type: \"Domain\", name: \"Email\", dataType: \"VarChar\", length: 100 }`\n" +
                "- `{ type: \"Domain\", name: \"Age\", basedOn: \"PositiveInt\" }`\n" +
                "- `{ type: \"WorkflowDiagram\", name: \"ApprovalFlow\" }`\n" +
                "- `{ type: \"Dashboard\", name: \"SalesKpis\" }`\n",

            ["genexus_edit_and_build"] =
                "# genexus_edit_and_build\n\n" +
                "Edit an object and rebuild its callers in one call.\n\n" +
                "## Required\n" +
                "- `name` — object to edit\n" +
                "- `part` — which part (e.g., `Source`, `Rules`)\n" +
                "- `content` — full text or unified diff\n\n" +
                "## Optional\n" +
                "- `mode` — `patch` (default) or `full`\n" +
                "- `type` — disambiguates when name matches multiple objects\n" +
                "- `dryRun` — preview without persisting (default `false`)\n" +
                "- `buildIncludeCallees` — `none` | `direct` (default) | `transitive`\n" +
                "- `buildPlanCap` — max build-plan size (default 200)\n\n" +
                "## Response\n" +
                "Returns a composite envelope with three blocks:\n" +
                "- `edit` — the diff from genexus_edit\n" +
                "- `impact` — output of genexus_analyze mode=impact (callers, risk, etc.)\n" +
                "- `build` — `{ taskId|TaskId, status: 'Accepted'|'Running', pollTarget }` for async caller rebuild, or `{ status: 'Skipped' }` when no callers\n\n" +
                "Poll the build via `genexus_lifecycle action=status target=<pollTarget>`.\n\n" +
                "## Errors\n" +
                "If `name` matches multiple objects, the edit phase aborts and the envelope returns `status=Error` with an `alternatives` array — retry with one of the (`name`, `type`) pairs.\n\n" +
                "## Example\n" +
                "`{ name: 'InvoiceProc', part: 'Source', mode: 'patch', content: '<diff>', buildIncludeCallees: 'direct' }`\n",

            ["genexus_db"] =
                "# genexus_db\n\n" +
                "Umbrella tool for datastore/index/DDL/sample-data actions. This entry covers the static index-advisor actions (`optimize_analyze|optimize_suggest|optimize_report`) — walks every Procedure / WebPanel / DataProvider Source + Events part, regex-parses `For each` blocks, derives (Transaction × where-signature × sort) access patterns, then surfaces concrete optimization opportunities.\n\n" +
                "## Actions\n" +
                "- `optimize_analyze [target=<Tx>]` — KB-wide pattern scan. Returns `{transactions:[{name, accessPatterns:[{whereSignature, callerCount, sortAttributes, samples:[...]}]}]}` sorted by callerCount desc. `target` is an optional filter.\n" +
                "- `optimize_suggest target=<Tx>` — for one Transaction, proposes covering indexes for the top where-signatures that are NOT covered by an existing index. Returns `{existingIndexes:[...], suggestedIndexes:[{columns, rationale, coveredQueries, estimatedBenefit, confidence, ddl}], redundantIndexes:[{name, reason}]}`. DDL is paste-ready (`CREATE INDEX IX_Tx_A_B ON Tx (A, B);`).\n" +
                "- `optimize_report [format=markdown|json]` — top-10 unindexed hot paths across the whole KB ranked by callerCount. `format=markdown` adds a paste-ready table under `report`.\n\n" +
                "## Where-signature canonicalisation\n" +
                "Two queries `Where AluCod = &c` and `Where AluCod = 1` collapse to the same signature `AluCod`. Literals and variables (&...) are stripped; only attribute references survive. Order is alphabetical so `{A,B}` and `{B,A}` collide.\n\n" +
                "## Confidence\n" +
                "Each finding carries `confidence: high|medium|low`. `low` means the For each lacked a Transaction name or a Where clause and the parse fell back to heuristic — treat those as leads, not actions.\n\n" +
                "## Index coverage\n" +
                "A multi-column index `(A, B, C)` covers any where-signature that is a strict prefix — `{A}` and `{A, B}` are covered, `{B}` is not. The advisor never suggests indexes that already exist as a prefix.\n\n" +
                "## Examples\n" +
                "- `{ action: 'optimize_analyze' }` — every transaction with at least one For each in the KB.\n" +
                "- `{ action: 'optimize_suggest', target: 'Aluno' }` — covering DDL for the hottest Where signatures on `Aluno`.\n" +
                "- `{ action: 'optimize_report', format: 'markdown' }` — paste into a code review.\n"
        };

        internal static string? Get(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;
            if (_helpTexts.TryGetValue(toolName, out var text)) return text;
            // Legacy alias → canonical: resolve and retry so old tool names still find help.
            if (McpRouter.TryRewriteLegacyTool(toolName, null, out var canonical, out _)
                && _helpTexts.TryGetValue(canonical, out var canonText))
                return canonText;
            return null;
        }

        internal static System.Collections.Generic.IReadOnlyCollection<string> KnownTools => _helpTexts.Keys;

        // Friction 2026-05-22 #62: gotcha doc resource. Every warning/lint
        // envelope carries docUrl=genexus://kb/tool-help/gotchas/<code>; the
        // agent fetches the long-form here. Returns a per-code body when
        // known, a generic stub otherwise so callers always get a payload.
        private static readonly Dictionary<string, string> _gotchaTexts = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["LintKbCharsetLossy"] =
                "# LintKbCharsetLossy\n\n" +
                "**Severity:** Warning.\n\n" +
                "Content contains characters outside the KB's WIN1252 charset. At runtime GeneXus will render those characters as `?`.\n\n" +
                "## Fix\n" +
                "- Replace with ASCII / latin-1 equivalents (`✓` → `OK`, `⧖` → `[wait]`).\n" +
                "- Or change the KB's `NLS_CHARACTERSET` to a UTF-8 variant if you need full unicode.\n",
            ["LintSpc0150ForEachAttributeWrite"] =
                "# LintSpc0150ForEachAttributeWrite\n\n" +
                "**Severity:** Warning (preflight — write succeeds, build will fail).\n\n" +
                "WebPanel Events source has an attribute assignment (no leading `&`) inside a `For each` / `endfor` block. GeneXus will fail the build with `spc0150 — Attribute cannot be assigned in this context`.\n\n" +
                "## Fix\n" +
                "Move the offending logic into a Procedure. Use the recipe:\n\n" +
                "```\ngenexus_recipe { name: 'extract_to_procedure' }\n```\n",
            ["GotchaGxButtonHtmlFormCustomEvent"] =
                "# GotchaGxButtonHtmlFormCustomEvent\n\n" +
                "`gxButton` with a custom `OnClickEvent` inside `<Form type=\"html\">` compiles but the HTML generator wires `data-gx-evt=5` (Enter) regardless. Custom events do not fire.\n\n" +
                "## Fix\n" +
                "- Use `<gxBitmap eventGX=\"'EventName'\" />` styled as a button, OR\n" +
                "- Move the control to `<Form type=\"layout\">` with `<action onClickEvent=\"'EventName'\" />`.\n",
            ["GotchaGxAttributeHtmlFormDiscreteReadOnly"] =
                "# GotchaGxAttributeHtmlFormDiscreteReadOnly\n\n" +
                "`gxAttribute` with `ControlType=\"Radio Button\"` or `\"Combo Box\"` inside `<Form type=\"html\">` renders disabled (the generator emits `disabled=\"\" class=\"gx-disabled\"`). `ReadOnly=\"False\"` and `Enabled=\"True\"` are ignored on this generator path.\n\n" +
                "## Fix\n" +
                "- Move the control to `<Form type=\"layout\">` (WWP table pattern), OR\n" +
                "- Render via a User Control, OR\n" +
                "- Emit raw `<input type=\"radio\">` inside a `gxTextBlock Format=\"HTML\"` block + JS wiring back to a hidden default-ControlType gxAttribute.\n",
            ["GotchaGxAttributeMissingDataField"] =
                "# GotchaGxAttributeMissingDataField\n\n" +
                "`gxAttribute` has neither `AttID` nor `DataField`. The control renders but binds to nothing; `FixWebFormData` silently keeps it so the missing binding masks the problem.\n\n" +
                "## Fix\n" +
                "Add `AttID=\"var:N\"` or `DataField=\"<attributeName>\"` so the control binds to a value.\n",
            ["GotchaUnknownControlType"] =
                "# GotchaUnknownControlType\n\n" +
                "`gxAttribute ControlType=\"...\"` is not a value the SDK recognizes (often a typo: `RadioButton` for `Radio Button`). The generator silently falls back to `Edit`.\n\n" +
                "## Valid ControlType values\n" +
                "Edit, Text Box, Combo Box, Radio Button, Check Box, Calendar, Image, Picture, Hyperlink, Button, Static, Description, Embedded Page, Dynamic Combo Box, List Box, Multi Selection List Box, Textarea, Password.\n",
            ["GotchaWebComponentMissingObjectCall"] =
                "# GotchaWebComponentMissingObjectCall\n\n" +
                "`gxEmbeddedPage` / `gxWebComponent` has no `ObjectCall` attribute → runtime renders an empty `<div>`.\n\n" +
                "## Fix\n" +
                "Add `ObjectCall=\"<ComponentName>.Create()\"` (or equivalent factory call).\n",
            ["GotchaHtmlFormatScriptStripped"] =
                "# GotchaHtmlFormatScriptStripped\n\n" +
                "`gxTextBlock Format=\"HTML\"` with `<script>`, `<iframe>`, or `<img onerror=...>` inside the CDATA. The GeneXus HTML generator escapes these tags so they render as literal text — your JS will NOT run.\n\n" +
                "## Fix\n" +
                "Use `<body onmousedown=\"...\">` + `addEventListener` for runtime JS injection. Inline event attributes on raw HTML elements inside `Format=\"HTML\"` blocks (e.g. `<input type=\"radio\" onclick=\"...\">`) ARE preserved — only block-level `<script>` / `<iframe>` / `img onerror` patterns are escaped.\n",
            ["GotchaCellOutsideTable"] =
                "# GotchaCellOutsideTable\n\n" +
                "`<cell>` or `<row>` with no `<table>` ancestor — the generator wraps silently or drops the element. Layout structure may be malformed at runtime.\n\n" +
                "## Fix\n" +
                "Wrap the element in a `<table>...<tbody>...</tbody></table>` hierarchy.\n",
            ["GotchaDuplicateControlName"] =
                "# GotchaDuplicateControlName\n\n" +
                "Two elements share the same `id` / `Name`. The SDK auto-renames the duplicates via `GetUniqueName` on save — any caller reference (event handler, JS, parent layout) that pointed at the renamed control breaks silently.\n\n" +
                "## Fix\n" +
                "Make each `id` unique. Suffix logically-related controls (`Btn1`, `Btn2`, ...).\n"
        };

        internal static string GetGotchaHelp(string code)
        {
            if (!string.IsNullOrWhiteSpace(code) && _gotchaTexts.TryGetValue(code, out var text))
                return text;
            // Generic stub so the agent always gets a 200. The code itself is the strongest
            // grep target; the agent can fall back to the message text on the warning.
            return $"# {code}\n\nNo long-form documentation is registered for this code yet. " +
                   "Inspect the `message` / `workaround` fields on the warning envelope — those carry the actionable guidance.\n";
        }

        internal static System.Collections.Generic.IReadOnlyCollection<string> KnownGotchaCodes => _gotchaTexts.Keys;
    }
}
