#!/usr/bin/env python3
"""Validate a conversion bundle and provide its deterministic digest oracle.

The oracle hashes normalized JSON (sorted object keys, compact UTF-8) and never
uses wall-clock time, absolute paths, or filesystem traversal order. Artifact
hashes are checked only when --artifacts-root is supplied.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
import tempfile
from pathlib import Path
from typing import Any

ORACLE_VERSION = "conversion-oracle/1"
SCHEMA_VERSION = "conversion-bundle/1.0"
SHA256_LENGTH = 64


def canonical_json(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")


def digest(value: Any) -> str:
    return hashlib.sha256(canonical_json(value)).hexdigest()


def bundle_digest(bundle: dict[str, Any]) -> str:
    """Hash the bundle with its self-referential digest field removed."""
    payload = json.loads(json.dumps(bundle))
    determinism = payload.get("determinism")
    if isinstance(determinism, dict):
        determinism.pop("canonicalSha256", None)
    return digest(payload)


def validate(bundle: Any, artifacts_root: Path | None = None) -> list[str]:
    errors: list[str] = []
    if not isinstance(bundle, dict):
        return ["root must be an object"]
    if bundle.get("schemaVersion") != SCHEMA_VERSION:
        errors.append(f"schemaVersion must be {SCHEMA_VERSION!r}")
    for key in ("source", "target", "artifacts", "diagnostics", "acceptance"):
        if key not in bundle:
            errors.append(f"missing required field: {key}")
    if not isinstance(bundle.get("artifacts"), list) or not bundle.get("artifacts"):
        errors.append("artifacts must be a non-empty array")
    else:
        for index, artifact in enumerate(bundle["artifacts"]):
            prefix = f"artifacts[{index}]"
            if not isinstance(artifact, dict):
                errors.append(f"{prefix} must be an object")
                continue
            path = artifact.get("path")
            if not isinstance(path, str) or not path or Path(path).is_absolute() or ":" in path or "\\" in path or path.startswith("/"):
                errors.append(f"{prefix}.path must be a relative POSIX path")
            sha = artifact.get("sha256")
            if not isinstance(sha, str) or len(sha) != SHA256_LENGTH or any(c not in "0123456789abcdef" for c in sha):
                errors.append(f"{prefix}.sha256 must be lowercase SHA-256")
            if artifacts_root is not None and isinstance(path, str) and path:
                candidate = (artifacts_root / Path(path)).resolve()
                root = artifacts_root.resolve()
                if root not in candidate.parents:
                    errors.append(f"{prefix}.path escapes artifact root")
                elif candidate.is_file():
                    actual = hashlib.sha256(candidate.read_bytes()).hexdigest()
                    if actual != sha:
                        errors.append(f"{prefix}.sha256 does not match {path}")
                else:
                    errors.append(f"artifact is missing: {path}")
    determinism = bundle.get("determinism")
    if not isinstance(determinism, dict) or determinism.get("oracleVersion") != ORACLE_VERSION:
        errors.append(f"determinism.oracleVersion must be {ORACLE_VERSION!r}")
    elif determinism.get("canonicalSha256") != bundle_digest(bundle):
        errors.append("determinism.canonicalSha256 does not match canonical bundle digest")
    return errors


def self_test() -> int:
    first = {"b": [2, 1], "a": "stable"}
    second = {"a": "stable", "b": [2, 1]}
    if digest(first) != digest(second):
        print("conversion-oracle: FAIL key ordering", file=sys.stderr)
        return 1
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        artifact = root / "generated.cs"
        artifact.write_text("// deterministic fixture\n", encoding="utf-8", newline="\n")
        sha = hashlib.sha256(artifact.read_bytes()).hexdigest()
        bundle = {
            "schemaVersion": SCHEMA_VERSION,
            "source": {"kind": "genexus-object", "kbAlias": "fixture", "objectName": "Invoice", "objectType": "Transaction", "snapshotSha256": "0" * 64},
            "target": {"kind": "business-component", "language": "csharp", "generatorVersion": "spike", "assumptions": ["synthetic only"]},
            "artifacts": [{"path": "generated.cs", "mediaType": "text/x-csharp", "sha256": sha, "bytes": artifact.stat().st_size}],
            "diagnostics": {"status": "blocked", "items": [{"severity": "warning", "code": "FIXTURE_REQUIRED", "message": "Synthetic oracle run; no live evidence."}]},
            "acceptance": {"humanApproval": "pending", "fixtureId": "synthetic-only", "checks": [{"name": "determinism", "status": "passed", "evidence": "oracle self-test"}]},
            "determinism": {"canonicalSha256": "0" * 64, "oracleVersion": ORACLE_VERSION},
        }
        bundle["determinism"]["canonicalSha256"] = bundle_digest(bundle)
        errors = validate(bundle, root)
        if errors:
            print("conversion-oracle: FAIL " + "; ".join(errors), file=sys.stderr)
            return 1
    print("conversion-oracle: self-test passed (canonicalization + artifact hash)")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", nargs="?", type=Path)
    parser.add_argument("--artifacts-root", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)
    if args.self_test:
        return self_test()
    if args.bundle is None:
        parser.error("bundle is required unless --self-test is used")
    try:
        bundle = json.loads(args.bundle.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"conversion-oracle: unable to read {args.bundle}: {exc}", file=sys.stderr)
        return 2
    errors = validate(bundle, args.artifacts_root)
    if errors:
        print("conversion-oracle: invalid", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 2
    print(f"conversion-oracle: valid bundle={bundle.get('bundleId', '<unidentified>')}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
