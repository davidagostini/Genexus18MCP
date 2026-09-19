# Release Process

This file is a navigation pointer. The normative release, merge, npm, issue,
resume, and changelog rules live in [`release_protocol.md`](release_protocol.md).
Read that document before any release operation.

## Fast path

From a clean `main` checkout, after explicit maintainer approval:

```powershell
pwsh -NoProfile -File .\release.ps1 -Version <X.Y.Z>
```

`release.ps1` is the only publication entrypoint. It commits the exact source
state before building, runs the canonical preflight, creates the manifest and
checksum, packages `publish.zip` and the Nexus VSIX, creates or resumes the
GitHub Release, and leaves npm publication to `.github/workflows/release.yml`.

The preflight performs cheap fail-fast checks first, then runs independent CLI,
PowerShell, Nexus, and solution-test phases in parallel. The MSBuild
warning-baseline and live-artifact phases remain outside that wave: live also
invokes `dotnet test` against Gateway test binaries, so sequencing both phases
prevents races in shared `bin/obj` and testhost outputs. Its JSON summary records
`sourceCommit`, `artifactFingerprint`, phase timings, and any reused phase.

If a preflight phase fails after the source commit and artifacts were produced,
rerun the same version. The entrypoint resumes only when the version, source
commit, SDK path, live inputs, and artifact fingerprint all match; otherwise it
rebuilds and runs the full gate. Do not manually skip individual phases.

## Verification

Wait for the release workflow and verify the exact package version through the
registry, not the npm website CDN:

```powershell
gh run list --workflow release.yml
npm view genexus-mcp@<version> version
npm view genexus-mcp@<version> dist
```

The workflow keeps npm verification synchronous and exact. It reports publish
acceptance, registry visibility, probe count, and propagation seconds in the
GitHub Step Summary. A package becoming visible in the registry can still take
about 90 seconds; that external delay is not removed by local parallelism.

## Recovery

- Build or preflight failure: keep the same untagged source commit, fix the
  cause, and rerun the same version.
- Existing release without assets: rerun the same entrypoint; it uploads the
  missing assets instead of creating a duplicate.
- Workflow failure after publication: inspect the exact workflow run and npm
  registry state before retrying. The workflow is idempotent for an already
  published exact version.

For issue collection, live SDK/KB evidence, warning baselines, detached status
files, and merge discipline, use `release_protocol.md` and its linked guides.
Contributors should open a PR against `main`; only the maintainer publishes.
