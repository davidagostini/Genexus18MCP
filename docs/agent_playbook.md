# Genexus18MCP Agent Playbook

Detailed, task-specific guidance for agents working on this repository. The
short project rules and navigation pointers live in `AGENTS.md`; read the
relevant section here when a task touches the corresponding behavior.

## Engineering safeguards

### Adding a mutating tool

Any new tool that mutates KB state must be registered in
`Program.IsMutatingTool` (`src/GxMcp.Gateway/Program.ToolPayload.cs`). Otherwise
the semantic cache can replay stale reads until the gateway restarts.

The cache stores the first successful read per `(kbAlias, tool, args)` and a
mutation clears the whole cache. Name-substring verbs cover edit/create/refactor/
variable tools; action-gated tools (`gxserver`, `transfer`, `structure`, `db`,
`lifecycle`, `properties`) need explicit action checks against
`src/GxMcp.Gateway/tool_definitions.json`. Extend
`SemanticCacheInvalidationTests` with the new mutating tool and its read-only
counterparts.

### Proving SDK fixes against a real KB

Unit tests are not enough for SDK behavior. For a controlled Streamable HTTP
validation cycle:

1. Build Gateway and Worker to `bin/Debug`.
2. Write a temporary config with an unused HTTP port, `McpStdio: false`, the
   debug Worker executable, and a scratch KB path.
3. Launch `GxMcp.Gateway.exe` with `GX_CONFIG_PATH` set. A detached launcher can
   time out while the child continues to serve; verify the port separately.
4. POST `initialize` to `/mcp` with
   `Accept: application/json, text/event-stream`; reuse `MCP-Session-Id`.
   Tool text is JSON-in-JSON in `result.content[0].text`.
5. Exercise the actual flow. For persistence claims, kill and relaunch the
   gateway before reopening the KB so the semantic cache cannot mask a failure.
6. Delete scratch objects, close the KB, stop only the scratch gateway, and
   remove temporary files. Never kill a user's running npm/stdio gateway.

### Windows shell and process gotchas

- In Bash-on-Windows, put the whole PowerShell command in single quotes so
  `$env:VAR` and `$_` reach PowerShell unchanged.
- Native Windows Python cannot read `/tmp/...`; convert paths with `cygpath -w`
  or use the real Windows temporary path.
- Git Bash passes `taskkill` switches as `//PID` and `//F`.
- Long-lived children can keep the shell pipe open after a successful spawn;
  treat the timeout as expected and verify the process/port separately.
- `git merge-tree` and `git commit-tree` can simulate merges without touching
  the working tree.

### Worker reload and known flakes

After editing Worker code, reload without restarting the MCP client with
`genexus_worker_reload mode=hard sourceDir=C:\Projetos\Genexus18MCP\src\GxMcp.Worker\bin\Debug`.
Use `genexus_worker_reload mode=soft force=true` only when the Worker is wedged.
The gateway pipe can become stale after reload; reconnect `/mcp` once if the
next call reports a crashed or exited Worker.

The following are known flaky in parallel runs and should be reproduced in
isolation before being treated as regressions:

- `EdgeCaseRegressionTests.Dispatcher_PatchApply_ValidateOnly_MapsToDryRun_ViaConvention`
- `PatternApplyServiceTests.*`

## Live tool playbook

The authoritative schemas remain in `src/GxMcp.Gateway/tool_definitions.json`.
These notes explain when an agent should reach for the newer capabilities.

### Lifecycle and authoring helpers

- **`genexus_lifecycle action=status wait=<sec> since=<baseline>`** — waits for a
  state transition instead of polling.
- **`genexus_history action=restore discard=true target=<obj>`** — restores the
  latest `EditSnapshotStore` part snapshot without a VCS round-trip.
- **`genexus_preview action=run`** — launches the KB startup object (`StartupObject`
  then `DefaultObject`) through the headless bridge.
- **`genexus_analyze mode=parent_context target=<webpanel>`** — distinguishes a
  popup from a standalone panel; `genexus_create_popup` returns the same hint.
- **`genexus_tutorial step=N`** — returns the six-step orient/list/inspect/read/
  edit/build walkthrough.
- **`genexus_watch_event target=<obj> event=<name>`** — filters in-memory
  `OperationTracker` runs; it is not a persistent breakpoint.
- **`genexus_learning action=report`** — aggregates `.gx/friction.jsonl`; pair
  with `genexus_friction_log action=tail` for raw entries.
- **`genexus_sd_panel action=inspect|create|edit name=<sdpanel>`** — type-locked
  SDPanel operations tagged `kind="SDPanel"`.
- **`genexus_multi_agent_lock action=acquire|release|status target=<obj> ownerId=<id>`**
  — advisory `.gx/locks` coordination with a default five-minute TTL.
- **`genexus_what_if change={kind,target,attribute,oldType,newType}`** — read-only
  impact preview; Numeric/Character family changes are flagged as breaks.
- **`genexus_voice transcript="..."`** — maps a natural-language transcript to
  a suggested call but does not dispatch it.
- **`genexus_ai_complete context=<prompt>`** — uses the configured
  OpenAI-compatible completion endpoint, or returns `AiEndpointNotConfigured`.

### History, testing, patterns, and cross-KB work

- **`genexus_time_travel name=<obj> at=<ISO-or-sha>`** — recovers part bytes from
  a historical Git commit; returns `KbNotInGit` when appropriate.
- **`genexus_auto_test action=generate_from_prod_log path=<jsonl>`** — emits
  GXtest stubs from deduplicated production log lines without writing the KB.
- **`genexus_reverse_pattern action=infer source=[X,Y,…]`** — intersects at
  least two objects to identify common variables, events, and parm signatures;
  it does not create a pattern.
- **`genexus_cross_browser target=<webpanel>`** — renders the resolved URL in
  available Chrome and Firefox/WebKit drivers and reports per-browser results.
- **`genexus_rename_across_kb from=<name> to=<name> type=Attribute?`** — routes
  indexed call sites through the SDK refactor service.
- **`genexus_kb_diff kbA=<alias-or-path> kbB=<alias-or-path>`** — compares KB
  object folders on disk without opening the SDK.
- **`genexus_worker_pool action=warm_spares spareCount=N`** — pre-spawns up to
  five workers for declared KBs; non-positive values disable it.
- **`genexus_sandbox action=create|remove from=<alias> name=<id>`** — clones a
  KB under the config sandbox directory for throwaway edits.
- **`genexus_github action=create_pr title=<t> body=<b>`** — invokes `gh` from
  the KB/working directory and returns a URL or structured CLI error.
- **`genexus_kb_import from=<alias> name=<obj> type=<TypeName>`** — copies one
  object's files without dependency resolution; run
  `genexus_lifecycle action=index force=true` afterward.

### SDK endpoint expansion

- **`genexus_transfer action=export|inspect|import`** — dependency-aware XPZ
  transfer through `IKnowledgeManagerService`; import is destructive and needs
  `dryRun=false confirm=true`.
- **`genexus_deploy action=list_targets|deploy`** — lists deployment targets;
  deployment needs `confirm=true`.
- **`genexus_security action=scan_native`** — runs the SDK Security Scanner,
  distinct from regex secret scanning and GAM auditing.
- **`genexus_analyze mode=kb_stats`** — reports last object/table changes,
  reorganization state, and optional per-type operation history.
- **`genexus_analyze mode=table_relations name=<Transaction>`** — reports the
  associated table, related transactions, and redundant attributes.
- **`genexus_db action=reorg_impact`** — uses a cheap timestamp heuristic unless
  `deep=true`, which runs the build-heavy database impact service.
- **`genexus_gxserver action=pipeline_list|pipeline_runs|pipeline_output|pipeline_run|pipeline_abort`**
  — manages CI pipelines on a GXserver-linked KB; run/abort need confirmation.
- **`genexus_gxserver action=ignored`** — reads the IDE's ignored-object marker
  (`ModelEntityOutput` type 505); see `docs/teamdev_commit_ignore_505.md`.
- **`genexus_layout action=list_controls`** — lists built-in and user control
  definitions.
- **`genexus_layout action=design_system [name=<DSO>]`** — inspects DSO tokens,
  themes, images, and references.
- **`genexus_create action=curl_procedure name=<Proc> curl="curl …"`** — creates
  a REST-consumer Procedure from cURL through the SDK service.

## Authoring constraints

### API routing grammar

`genexus_create type=API` accepts one top-level HTTP verb block per API object:

```text
Verb { <route> => <Object>; <route2> => <Object2>; }
```

Routes are bare identifiers, mappings use `=>`, and rules end in `;`. Mixing
top-level verb blocks or adding `@Get`/`@Post` decorators is rejected by the
GeneXus 18 grammar. Expose multiple verbs with per-Procedure REST instead.

### Folder and module placement

- Move an existing object with
  `genexus_properties action=move name=<obj> destination=<Folder-or-Module>`.
  Use `targetModule` or `destKind=Folder|Module` only when needed.
- Use `dryRun=true` to preview the destination. For writes, pass the prior
  `versionToken` as `baseVersion`; the service snapshots parts and authored
  properties, verifies the committed parent/content, and rolls back on failure
  by default.
- Move performs no Specify, Generate, Build, Rebuild, reorganization,
  execution, or tests.
- Create directly in a container with `folder=<name>` or `module=<name>`;
  placement is reported in the response.
- Placement properties passed to `action=set` are routed to the move service.
- After a move, `list`/`inspect` invalidate hierarchy caches immediately.

The containers themselves are created with `genexus_create type=Folder|Module`.
The facade's empty `KBObject.Parent`/`Module` setters are misleading; runtime
persistence goes through `Artech.Udm.Framework.EntityManager.UpdateParent` in
`src/GxMcp.Worker/Helpers/ObjectMover.cs`, with full snapshot verification.

### Control-bound events

Write the layout or PatternInstance before the Events part. A control-bound
event references a control that must already exist, otherwise the SDK returns
`src0233`/`src0216`. A `userAction name="Foo"` creates an empty `DoFoo` stub;
fill that stub rather than adding a second event (`src0208`).

### SDPanel projections

SDPanels are WorkWithDevices projections, not self-contained ordinary parts.
`SDEvents` and `SDRules` are readable virtual source parts; `part=Source` and
`part=Events` resolve to `SDEvents`. `SDLayout`, `SDVariables`, and
`SDConditions` are non-source projections and may serialize as empty properties;
an empty result does not mean the panel is empty. Layout and variables are
authored in the GeneXus IDE.
