# Seven Pending MCP Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement and validate the seven remaining MCP improvements identified from the official GeneXus for Agents comparison and the repository changelog: batch Object Text workflows, real dry-run broken-reference reporting, indexed source search, complete async/cancellation behavior, Theme/StyleSheet editing, WebForm validation diagnostics, and fast-incremental/warm-reload completion.

**Architecture:** Preserve the existing Gateway → Worker → GeneXus SDK boundary. Extend the existing `genexus_io`/Object dispatch and lifecycle/edit contracts instead of creating parallel protocols. Keep selector expansion, validation, indexing, cancellation, and warm-state restoration behind Worker services so Gateway remains a transport/router. Every externally visible contract change is reflected in the tool schema, help catalog, discovery fixture, tests, and changelog.

**Tech Stack:** C#/.NET 10 Gateway, .NET Framework 4.8 STA Worker, GeneXus 18 SDK, MCP JSON-RPC/Streamable HTTP, Node.js CLI, xUnit, PowerShell build/test scripts.

---

## Task 1 — Establish contracts and regression coverage

- [x] Read the current router, dispatcher, schema, help catalog, existing service tests, and generated discovery fixture for the seven affected paths.
- [x] Record the exact current behavior for each gap with focused tests before changing implementation; preserve existing aliases and backward-compatible envelopes.
- [x] Keep the batch Object Text API under `genexus_io` with explicit actions for export, import, validate, and delete, and define deterministic selection, dry-run, overwrite, and per-item result semantics.

## Task 2 — Implement batch Object Text workflows

- [x] Add a Worker Object Text service that expands the repository’s existing object selectors, resolves objects through the SDK/index, and returns stable per-object results without silently crossing KB boundaries.
- [x] Implement export, import, validation, and delete execution with bounded file/path checks, overwrite handling, validation-only behavior, and partial-failure reporting.
- [x] Route the actions through Gateway/Worker, update schema/help/classification/mutation invalidation, and regenerate the discovery golden fixture.
- [x] Add focused Worker and Gateway contract tests covering empty selections, mixed success, invalid files, dry-run deletion, and alias propagation.

## Task 3 — Make dry-run broken references real

- [x] Wire dry-run planning to the existing validation/impact service on every write path that currently emits `brokenRefs: []` or an unavailable-analysis warning.
- [x] Report deterministic reference diagnostics with source object, target object, and reason while retaining the current plan envelope and avoiding writes during dry-run.
- [x] Add regression tests for a changed object that introduces a broken reference and for a clean edit; verify the actual edit response and not only the planner in isolation.

## Task 4 — Add an index-backed source-search path

- [x] Inspect the existing index persistence/revision/invalidation mechanisms and add a compact, revision-aware source-token index only where it can be populated from the native SDK source already used by the Worker.
- [x] Use the index for ordinary token/phrase searches, retain the current cancellation and time-budgeted scan as a correctness fallback, and invalidate or rebuild entries when object source changes.
- [x] Add correctness, stale-index, cancellation, and performance-regression tests; expose whether the result used the index only through existing diagnostic metadata if the contract already supports it.

## Task 5 — Complete async and cancellation propagation

- [x] Trace Gateway background jobs, Worker dispatch, cancellation registry, and all affected edit/semantic-operation handlers to identify operations that return an async id but ignore cancellation.
- [x] Make cancellation cooperative at service boundaries and return one consistent terminal job state for cancelled, failed, and completed operations without breaking synchronous callers.
- [x] Add tests for cancellation before start, during a long operation, repeated cancel, unknown job, and worker restart cleanup.

## Task 6 — Support Theme and StyleSheet editing

- [x] Confirm the native SDK read/write path and part names for Theme and StyleSheet using existing SDK probes and test doubles.
- [x] Extend the existing generic object/part editing path with the smallest native-compatible implementation, including type validation, dry-run, validation mode, and cache invalidation.
- [x] Add Worker tests and a Gateway contract test for read, edit, invalid part/type, and validation failure behavior.

## Task 7 — Surface WebForm validation diagnostics

- [x] Integrate `WebFormPreSaveValidator` into the edit response and `validate=only` path for WebForm-compatible objects, preserving warnings/errors and object/field locations.
- [x] Add the documented force-write escape hatch only if the current edit contract can carry it without changing unrelated writes; ensure force-write is explicit and never implicit.
- [x] Add regression tests for blocking validation errors, warnings, validation-only no-write, and explicit forced writes.

## Task 8 — Finish fast incremental and warm reload

- [x] Trace `FastIncrementalDecision` through the real build execution path and implement only safe skip behavior backed by the decision object, with an explicit fallback reason when the SDK cannot honor it.
- [x] Complete warm snapshot metadata and boot-time restore/revalidation, rejecting incompatible or stale snapshots and falling back to the existing index bootstrap path.
- [x] Add tests for eligible/ineligible fast incremental builds, stale snapshots, compatible restore, corruption, and fallback; preserve the experimental safety gates.

## Task 9 — Validate, document, and hand off

- [x] Add the required `CHANGELOG.md` entry under `## Unreleased` describing the observable improvements and any intentionally deferred SDK limitations.
- [x] Run the narrowest tests after each task, then the required solution/Worker/Gateway/CLI checks and build/package validation.
- [x] Review the final diff for scope, compatibility, security, mutation invalidation, generated fixture synchronization, and the pre-existing unrelated `pnpm-lock.yaml` change.
