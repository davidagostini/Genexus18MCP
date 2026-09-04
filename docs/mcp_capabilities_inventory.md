# GeneXus MCP Capabilities Inventory

This document records the MCP-facing surface that is currently exposed by the repository.

Agent usage reference:
- [`docs/llm_cli_mcp_playbook.md`](llm_cli_mcp_playbook.md)

Status values:
- `active`: implemented and reachable through the current gateway-worker path
- `partial`: implemented but still limited in scope or ergonomics

## Transport

| Capability | Status | Notes |
| --- | --- | --- |
| stdio MCP loop | active | Main local transport for agent clients |
| `/mcp` HTTP endpoint | active | Supports POST, GET (SSE), and DELETE with MCP session headers |
| local bind default | active | Defaults to `127.0.0.1` through config |
| origin validation | partial | Loopback safe by default, configurable allowlist supported |
| session expiration | active | Idle sessions are removed automatically |

## Tools

Source of truth:
- `src/GxMcp.Gateway/tool_definitions.json`

Query notes:
- `genexus_query` supports both the legacy `parent:"FolderName"` filter and the hierarchical `parentPath:"Module/Folder"` filter.
- Prefer `parentPath` whenever the KB contains duplicate folder names under different modules.

Tool response notes (`tools/call` text payload):
- Gateway now enriches worker payloads with AXI-like metadata under `_meta` (underscore-prefixed per MCP convention for non-standard fields).
- `_meta.schemaVersion` currently uses `mcp-axi/2` (bumped in v2.0.0).
- `_meta.tool` identifies the normalized tool name.
- For collection responses, gateway may add `returned`, `total`, `empty`, `hasMore`, and `nextOffset` when enough context is available.
- For truncated responses, gateway sets `_meta.truncated=true` and appends an actionable `help` hint.
- For idempotent success (`status=Success` + `details=No change`), gateway adds `noChange=true`.
- v2.0.0 added: `_meta.idempotent=true` on idempotency-cache hits, `_meta.batched=true` when `targets[]` plural form is used, `_meta.dryRun=true` on preview responses, `_meta.removedTools` on `initialize` for proactive agent detection of removed tools.
- For worker timeout budget events, gateway returns a structured payload with `result.isError=true`, `status=Running`, `operationId`, `correlationId`, and `help` follow-up guidance.
- These enrichments are additive and keep existing response fields for backward compatibility.

Optional response-shaping arguments for list-heavy tools:
- `genexus_query` and `genexus_list_objects` accept optional `fields` (array or comma-separated string) for a custom field subset.
- `axiCompact` defaults to **`true`** for `genexus_query` and `genexus_list_objects`. The compact projection returns only `name`, `type`, and `path` (plus `parentPath` for `genexus_list_objects`). Pass `axiCompact: false` to receive the full payload (description, parent, metadata, etc.).
- `meta.fields` is returned when field projection is active.
- `meta.totalByType` may be emitted when result rows expose a `type` field.

| Tool | Status | Worker path |
| --- | --- | --- |
| `genexus_query` | active | `Search -> Query` |
| `genexus_list_objects` | active | `List -> Objects` |
| `genexus_read` | active | `Read -> ExtractSource`; `targets[]` plural form routes to `Batch -> BatchRead` |
| `genexus_edit` | active | `Write`, `SemanticOps -> Apply` (mode=ops), `JsonPatch -> Apply` (mode=patch + array), or legacy `Patch -> Apply` (mode=patch + string); `targets[]` plural form routes to `Batch -> MultiEdit` |
| `genexus_inspect` | active | `Analyze -> GetConversionContext` |
| `genexus_analyze` | active | `Analyze`, `Linter`, or `UI` depending on mode |
| `genexus_lifecycle` | active | `Build`, `KB`, or `Validation` depending on action (specify, compile_check, build, rebuild, index, status, result, reorg, validate) |
| `genexus_create` | active | Object creation umbrella: Transaction, Procedure, WebPanel, SDT, API, Domain, Popup, SDPanel, SaveAs, Template, `object_atomic` |
| `genexus_structure` | active | `Structure -> GetVisualStructure | UpdateVisualStructure | GetVisualIndexes | GetLogicStructure | CheckSubtypes`; supports `type` disambiguation, `remove_attribute`, `move_attribute` |
| `genexus_refactor` | active | `Refactor -> RenameObject | RenameAttribute | RenameVariable | ExtractProcedure | ExtractSubroutine | WWPSetCondition` |
| `genexus_format` | active | `Formatting -> Format` |
| `genexus_properties` | active | `Property -> Get | Set | Move` |
| `genexus_versioning` | active | Versioning umbrella: `History -> List | Get_Source | Save | Restore`, `Undo`, `TimeTravel`, `Blame`, `Diff` |
| `genexus_io` | active | IO umbrella: `Asset -> Find | Read | Write`, `Object -> ExportText | ImportText`, `Export -> Unified`, `ScreenshotPublish` |
| `genexus_db` | active | Database umbrella: `DbDrift`, `DbOptimize`, `Analyze -> GetSQL / GetSqlForNavigation / GenerateSampleData`, `Types`, `ReorgImpact` |
| `genexus_layout` | active | WebForm control tree, layout properties, printblock management |
| `genexus_edit_form` | active | Semantic WebForm element manipulation |
| `genexus_apply_pattern` | active | Pattern application and WorkWithPlus action group configuration |
| `genexus_wwp` | active | WorkWithPlus grid actions and action groups |
| `genexus_security` | active | `Security -> audit_gam | scan_secrets | scan_native` (native SDK scanner) |
| `genexus_kb` | active | Multi-KB pool management, startup object, and environment switching |
| `genexus_kb_version` | active | SDK `KBVersionHelper` model version tree and branch management |
| `genexus_gam` | active | SDK `IIntegratedSecurityService` GAM provisioning and deploy |
| `genexus_transfer` | active | Native XPZ export and import |
| `genexus_deploy` | active | Application deployment targets and execution |
| `genexus_doc` | active | `Wiki`, `Visualizer`, or `Health` depending on action |
| `genexus_recipe` | active | Named playbooks, macro suggestion, and crystallization |
| `genexus_generator_reference` | active | Native typed .NET generator references |
| `genexus_data_view` | active | Native typed Transaction + Data View authoring |
| `genexus_whoami` | active | KB context, version, health, and playbook/skills discovery |

## Resources

| Resource or template | Status | Notes |
| --- | --- | --- |
| `genexus://kb/index-status` | active | KB indexing status |
| `genexus://kb/health` | active | Gateway and worker health report |
| `genexus://kb/agent-playbook` | active | Agent-native operating playbook for MCP, verification, and Git-friendly change control |
| `genexus://kb/llm-playbook` | active | Protocol-first guide for LLM usage across CLI AXI and MCP tool flows |
| `genexus://objects` | active | Browsable index of objects |
| `genexus://attributes` | active | Browsable attribute listing |
| `genexus://objects/{name}/part/{part}` | active | Part-specific object reading |
| `genexus://objects/{name}/variables` | active | Object variable declarations |
| `genexus://objects/{name}/navigation` | active | Navigation analysis |
| `genexus://objects/{name}/hierarchy` | active | Dependency hierarchy |
| `genexus://objects/{name}/data-context` | active | Data context bundle |
| `genexus://objects/{name}/ui-context` | active | UI context bundle |
| `genexus://objects/{name}/conversion-context` | active | Conversion-oriented context |
| `genexus://objects/{name}/pattern-metadata` | active | Pattern metadata |
| `genexus://objects/{name}/summary` | active | LLM-oriented summary |
| `genexus://objects/{name}/indexes` | active | Visual indexes for Transaction/Table objects |
| `genexus://objects/{name}/logic-structure` | active | Logical structure for Transaction/Table objects |
| `genexus://attributes/{name}` | active | Attribute metadata |
| resource subscriptions | partial | Subscription capability is advertised and notifications are emitted through the SSE session stream |

## Prompts

| Prompt | Status | Notes |
| --- | --- | --- |
| `gx_explain_object` | active | Grounded explanation workflow using source, variables, navigation, and summary |
| `gx_bootstrap_llm` | active | Session bootstrap workflow for protocol-first usage (`tools/list`, `resources/list`, `prompts/list`, `genexus://kb/llm-playbook`) with optional `goal` argument |
| `gx_convert_object` | active | Conversion workflow with review gates and target-language argument |
| `gx_review_transaction` | active | Transaction review workflow focused on structure, rules, and risks |
| `gx_refactor_procedure` | active | Procedure refactor workflow focused on preserving behavior |
| `gx_generate_tests` | active | Test-plan generation workflow |
| `gx_trace_dependencies` | active | Dependency tracing workflow with impact analysis |
| `gx_agent_ship_change` | active | Controlled-change workflow for agents with explicit verification and reporting |
| `gx_agent_visual_change` | active | Visual metadata workflow that forces authoritative-surface resolution before editing |

## Completion

| Capability | Status | Notes |
| --- | --- | --- |
| `completion/complete` | active | Supports structured completions for object parts, include fields, and target languages |

## Notifications

| Capability | Status | Notes |
| --- | --- | --- |
| `notifications/initialized` | active | Handled as a no-op |
| operation progress notification | active | Emitted through SSE as `notifications/message` with `operationId` and `correlationId` for long-running tools |
| tools list changed notification | active | Emitted through the HTTP SSE session stream |
| resources list changed notification | active | Emitted through the HTTP SSE session stream |
| resource updated notification | active | Emitted through the HTTP SSE session stream |

Operational notes:
- `genexus_lifecycle(action='status'|'result', target='op:<operationId>')` resolves gateway-tracked MCP operations.
- `genexus_lifecycle(action='status', target='gateway:metrics')` returns per-tool p50/p95 and error/timeout/no-change counters.

## Extension integration

| Capability | Status | Notes |
| --- | --- | --- |
| local discovery file `.mcp_config.json` | active | Points to `/mcp` |
| default extension HTTP client | active | Extension runtime speaks MCP directly for discovery, VFS, providers, shadow sync, commands, and webviews |
| dynamic tool discovery in extension | active | Runtime discovery now loads tools, resources, and prompts from `/mcp` and caches the snapshot locally |
| MCP discovery commands in extension | active | Command Palette can inspect discovery, open resources, and run prompts from the cached snapshot |
| global Claude registration | active | Uses HTTP wrapper against `/mcp` |

## Known gaps

- Resource surface is still too small for rich object exploration.
- Prompt catalog is still minimal.
- Completions are currently static and schema-oriented; object-name completion is still pending.
- Prompt workflows now validate required and enumerated arguments in the gateway, but object-name-aware completion is still pending.
- Extension flows already migrated to MCP include discovery, prompts, resources, SQL, tests, build/rebuild, indexing, object creation, attribute rename, procedure extraction, properties, history, and structure/indexes views.
- `genexus_forge` is reachable now, but code generation quality is still early-stage.
