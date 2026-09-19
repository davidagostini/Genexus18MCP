# Official Nexa Skill Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Import the downloaded official Nexa skill into the repository and expose its Markdown knowledge through the Gateway MCP as safe, read-only resources, while preserving the existing broader MCP tool surface.

**Architecture:** Treat the downloaded ZIP as an upstream reference pack, validate and synchronize its `nexa/` content into `.gemini/skills/nexa`, and embed only `SKILL.md` plus Markdown references in the Gateway assembly. Add `genexus://kb/skills/nexa` to the existing catalog and a `genexus://kb/skills/nexa/references/{name}` resource template. Resolve reference names through a fixed embedded-resource map so `resources/read` cannot access arbitrary files or execute the package scripts.

**Tech Stack:** C#/.NET 10 Gateway, Newtonsoft.Json, xUnit, MSBuild embedded resources, PowerShell archive validation, Markdown skill content.

---

### Task 1: Validate and synchronize the official skill package

**Files:**
- Modify: `.gemini/skills/nexa/**` from `C:\Users\swluc\Downloads\NexaSkill.zip`
- Reference: `.gemini/skills/NOTICE.md`

- [x] Validate the ZIP has only the expected `nexa/` Markdown, catalog JSON, and Python script paths, with no traversal or absolute entries.
- [x] Extract to an ignored `scratchpad/` staging directory and compare the staged package with the tracked copy.
- [x] Synchronize the official `SKILL.md` and references into `.gemini/skills/nexa` (with two trailing-whitespace-only cleanups), preserving the existing upstream attribution and license files.
- [x] Confirm the synchronized package is tracked and no unrelated working-tree changes were altered.

### Task 2: Embed Nexa knowledge in the Gateway MCP

**Files:**
- Create: `src/GxMcp.Gateway/NexaSkillPack.cs`
- Modify: `src/GxMcp.Gateway/GxMcp.Gateway.csproj`
- Modify: `src/GxMcp.Gateway/SkillCatalog.cs`
- Modify: `src/GxMcp.Gateway/McpRouter.cs`
- Modify: `src/GxMcp.Gateway/Program.Whoami.cs` only if Nexa needs a dedicated read hint

- [x] Add deterministic embedded-resource entries for the synchronized `SKILL.md` and `references/*.md` files.
- [x] Implement a read-only `NexaSkillPack` lookup that accepts only `SKILL.md` and a single safe Markdown reference filename.
- [x] Add the official Nexa entry to `SkillCatalog`, including a concise description and a “read before” hint for object modeling, properties, commands, build, or `gxnext` workflows.
- [x] Extend `resources/read` to serve `genexus://kb/skills/nexa/references/{name}` while rejecting unknown names and traversal-like paths.
- [x] Add the Nexa reference URI template to `resources/templates/list`; keep scripts and JSON catalogs local-only and non-executable through MCP.

### Task 3: Add regression coverage and synchronize contracts

**Files:**
- Modify: `src/GxMcp.Gateway.Tests/SkillCatalogTests.cs`
- Modify: `src/GxMcp.Gateway.Tests/McpRouterTests.cs`
- Modify: `src/GxMcp.Gateway.Tests/McpHandshakeContractTests.cs` only for explicit Nexa assertions
- Modify: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/resources-list.response.json`
- Modify: `CHANGELOG.md`

- [x] Test that the official Nexa root and a representative reference are embedded, non-empty, and cite GeneXus documentation.
- [x] Test `resources/list`, `resources/read`, the reference template, and path rejection.
- [x] Regenerate only the intentional discovery golden fixture change.
- [x] Add the required Unreleased changelog entry.

### Task 4: Validate and report the comparison

- [x] Run the narrow Gateway tests first, then the repository-required build/test checks that are practical in the installed SDK environment.
- [x] Review the final diff and status, preserving the pre-existing untracked `pnpm-lock.yaml`.
- [x] Report the official server findings: installed `gxnext`/MCP versions and 14-tool surface versus the repository’s 50-tool surface, plus any remaining candidate improvements that require a separate design decision.
