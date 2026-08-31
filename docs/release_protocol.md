# Genexus18MCP Release Protocol

Release-facing instructions moved out of `AGENTS.md` so normal implementation
tasks load a smaller instruction file. These rules remain normative whenever a
release, merge, or changelog edit is requested.

## Explicit release gate

Do not run `release.ps1`, create tags, push release branches, or publish a
GitHub Release because a change looks ready. Shipping requires the maintainer's
explicit request for that change. A prior approval never carries to a later
release. Before a release, add the exact version entry to `CHANGELOG.md`.

## Standard release execution

This project ships both the GitHub Release and the npm package `genexus-mcp`.
Use the one-shot script:

```powershell
.\release.ps1 -Version <X.Y.Z>
```

It bumps versions, builds Gateway and Worker, creates the normalized
`publish.zip` and checksum, commits/tags, and creates the GitHub release with
the zip attached. Do not run `gh release create` manually: the release workflow
requires `publish.zip` on the initial published event. The Worker needs the
local GeneXus 18 SDK, so the release artifact must be built on Windows with
GeneXus installed.

After publishing, verify both channels:

```powershell
gh run list --workflow release.yml
npm view genexus-mcp@latest version
```

Issues are closed only after the released fix is available. Comment on the
issue with the release URL first, then close it.

## Merge discipline

Two PRs that both edit `CHANGELOG.md` can conflict regardless of merge order.
Before merging, probe with `git merge-tree --write-tree` and, when needed, a
read-only `git commit-tree` simulation. For a fork PR, resolve the Unreleased
sections in a temporary worktree, preserve CRLF, commit with the canonical
GitHub merge message, and push the explicit ref. After a manual main update,
rebase the local branch onto `origin/main` and resolve the changelog by
combining sections in project order.

## npm version verification

The npm registry can show a new version before the npmjs.com rendered page
updates. Treat `npm view` and the registry endpoint as authoritative; do not
re-cut a release because the website CDN still shows an older version.

If a user is actually running an old install, check multiple binaries with
`where.exe genexus-mcp`, clear stale npm metadata only when appropriate, and
confirm the result with `genexus-mcp doctor`.

## Changelog voice

`CHANGELOG.md` is user-facing. Use `### Added`, `### Fixed`, `### Changed`, and
`### Removed` in that order, with `### Internal` last for engineer-only notes.
Each user-facing bullet should lead with the capability or behavior, use plain
English and past tense for fixes, and avoid roadmap codes, session narratives,
agent IDs, commit hashes, KB-specific names, and implementation dumps. Do not
put test counts in user-facing sections. Every merged PR's user-facing work
must include the contributor credit and PR links before release.
