# Nexus IDE — recon map (2026-07-23)

Read-only orientation of `src/nexus-ide` — the VS Code-family extension "Nexus IDE for
GeneXus," an MCP client to this repo's gateway. Produced by the `improve` skill's
direction pass so the maintainer can decide where to take it. **Not a plan** — a map +
honest gap assessment. Evidence is `file:line` under `src/nexus-ide/src`. `node_modules/`
excluded. ~12.8k LOC. No secrets found (config holds local paths only).

> Two of the most consequential claims below were spot-verified against the source:
> `SyncManager` is imported (`extension.ts:16`) but never instantiated (dead code), and
> `gxActionsProvider.ts:56` binds the "Explain Code with AI" item to `copyMcpConfig`.

## Architecture

- **Transport** — `infra/GxGatewayClient.ts` (raw HTTP JSON-RPC to `GxMcp.Gateway.exe`,
  MCP `2025-11-25`; session init, retry on ECONNRESET/expired session, busy indicator).
  Everything funnels through this one client.
- **Managers** (`src/managers/`) — `BackendManager` (spawn gateway dev/packaged/publish,
  file-lease PID mutex, health monitor + restart prompt), `CommandManager` (~30 commands,
  thin wrappers over MCP tools), `ContextManager` (status bar / `genexus.activePart`),
  `GxCacheManager` (in-memory caches), `McpDiscoveryManager` (lists tools/resources/prompts,
  can bridge into `vscode.lm.registerTool`, can patch Claude Desktop config), `ProviderManager`
  (registers all language providers), `ShadowManager` (`**/*.gx` watcher → `syncToKB`),
  `SyncManager` (**dead code — see below**).
- **Filesystem/mirror** — editing happens through real `.gx_mirror` files (the "shadow"),
  not the `gxkb18:` VFS. `gxShadowService.ts` materializes the KB as placeholder `.gx`
  files, lazily hydrates on open, delta-syncs back (line-diff patch → full-content
  fallback), maintains `.gx_index.json` + `.gx_containers.json`. `gxFileSystem.ts`
  (`gxkb18:` VFS) is a secondary/legacy path; structural mutation ops all `throw
  NoPermissions`. `gxTreeProvider.ts` reads the mirror off disk with a "Main Programs" /
  "Root Module" grouping layer.
- **Data flow** — open `file://…/.gx_mirror/…` → save/watch → gateway (delta patch) →
  tree/VFS read from mirror or cached query results.

## Feature completeness

| Surface | State | Evidence |
|---|---|---|
| Diagnostics | Real | `diagnosticProvider.ts` — `genexus_analyze` linter, debounced on-type |
| Completion | Real (substantial) | `completionProvider.ts` — part-aware, `&var.` member resolution, for-each attrs |
| Hover | Real | `hoverProvider.ts` — functions/vars/attributes, 30s cache |
| Signature help | Real | `signatureHelpProvider.ts` — native fns + `genexus_inspect` |
| Code lens | Real (small) | `codeLensProvider.ts` — top-of-file "N references" |
| Workspace symbol | Real (thin) | `workspaceSymbolProvider.ts` — `genexus_query` mapped |
| Format | Real (delegates) | `formatProvider.ts` — `genexus_format`, silent `[]` on timeout |
| Definition | **Partial** | `definitionProvider.ts:53` — remote resolves only to object at position (0,0) |
| References | **Partial** | `referenceProvider.ts:29` — every hit is (0,0); no variable search (`:19`) |
| Rename | **Partial** | `renameProvider.ts:74` — server rename runs but returns an EMPTY `WorkspaceEdit` (UI shows nothing) |
| Document symbol | **Regex-only** | `symbolProvider.ts` — no semantic model |
| Inline completion | **Stub** | `inlineCompletionProvider.ts` — 4 hardcoded string branches, no model/backend |
| Code action | **Stub** | `codeActionProvider.ts:20-21` — one hardcoded "Create Variable", admitted incomplete |
| StructureView | Real (most complete) | `webviews/StructureView.ts` — editable, saves via `genexus_structure` |
| PropertiesView | Real | `webviews/PropertiesView.ts` — get/set via `genexus_properties` |
| HistoryView | Real | `webviews/HistoryView.ts` — revision list + `vscode.diff` |
| IndexView | Real (read) | `webviews/IndexView.ts` — no save path |
| LayoutView | **Thin passthrough** | `webviews/LayoutView.ts` — injects MCP HTML, no editing/messages |
| DiagramView | Real, **CDN dep** | `webviews/DiagramView.ts:61` — mermaid from jsdelivr CDN, no CSP/localResourceRoots |

## Maturity signals

- **No TODO/FIXME markers**, but inline prose admits incompleteness: `codeActionProvider.ts:20-21`,
  `renameProvider.ts:55-56`, `referenceProvider.ts:19`.
- **Dead code**: `SyncManager` (143 lines, SSE live-sync) imported at `extension.ts:16`,
  never instantiated/registered — a half-shipped bidirectional-sync feature sitting inert. *(verified)*
- **Mis-wired command**: `gxActionsProvider.ts:56` — "Explain Code with AI" bound to
  `nexus-ide.copyMcpConfig` (copies config to clipboard), not any AI flow. *(verified)*
- **11 bare `catch {}`** (some deliberate best-effort, some swallow real read/parse failures, e.g. `gxFileSystem.ts:139`).
- **49 `console.log`** across 9 files (heaviest `extension.ts`, `BackendManager.ts`) — the only logging, no level gating.
- **Dev-tree assumptions leak into shipped code**: `gxShadowService.ts:76-79` walks up for
  `.git`/`Genexus18MCP.sln`; `BackendManager.ts:270-296` hardcodes dev bin paths.
- **Webview supply chain**: `DiagramView.ts:61` external CDN in a scripts-enabled webview, no CSP.
- No prompt-injection content found.

## Test coverage

One spec — `src/test/suite/extension.test.ts` (378 lines, 9 real tests) via
`@vscode/test-electron`: activation, command registration, `GxUriParser` multi-part,
`browseObjects` structural resolution, `hasUsableSearchIndex`, shadow materialization
dedup, tree grouping, legacy-mirror detection. Genuinely useful on the trickiest pieces,
but **zero coverage** of `BackendManager`, `GxGatewayClient`, any language provider, any
webview, `CommandManager`, or `SyncManager`. No coverage gate / CI found in this scan.

## Biggest gaps (unfinished / "looks like it works but doesn't")

1. **Rename / references / definition precision** — rename UI shows nothing
   (`renameProvider.ts:74` empty edit); refs & defs collapse to object-at-(0,0). Most
   user-visible "half-working" gap.
2. **Inline completion is a placeholder**, not real IntelliSense/AI, despite the extension's MCP/AI positioning.
3. **Code actions ignore the rich diagnostics** the linter already produces — one hardcoded fix.
4. **SyncManager fully built but disconnected** — wire it or delete it.
5. **LayoutView is a raw HTML passthrough**, not an editor (unlike Structure/Properties/Index).

## Adjacent-possible (cheap given the architecture)

1. **Richer chat/agent tool bridge** — `McpDiscoveryManager` already lists MCP
   tools/resources/prompts and can register LM tools + patch Claude config; exposing
   GeneXus refactor/analyze/build as first-class chat tools is a small step.
2. **Real code actions from existing diagnostics** — `GxDiagnosticProvider` already carries
   `code`/`severity`/`snippet`; driving `codeActionProvider` off the diagnostic at cursor is mostly plumbing.
3. **Turn SyncManager into real live bidirectional sync** — the SSE listener + cache
   invalidation + re-hydration already exist; needs `.register()` + dirty-doc conflict handling.

## Note on scope

The Worker cannot build on GitHub-hosted CI (local GeneXus SDK), and this extension's
tests use `@vscode/test-electron` — so any Nexus IDE test/CI work is local/self-hosted,
same constraint as the rest of the repo. If the extension becomes a first-class product
surface, it warrants its own CHANGELOG/versioning decision separate from the MCP server.
