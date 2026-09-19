#!/usr/bin/env python3
"""Synchronize release-facing metadata from package.json and gx-versions.json."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys
from typing import Any


SEMVER = re.compile(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\Z")
MARKER = "gx-compatibility"


def fail(message: str) -> int:
    print(f"release-sync: invalid: {message}", file=sys.stderr)
    return 2


def read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot read {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def catalog_data(root: Path) -> tuple[str, list[dict[str, Any]], list[dict[str, Any]], Path]:
    path = root / "config" / "gx-versions.json"
    document = read_json(path)
    primary = document.get("primaryMajor")
    entries = document.get("supportedMajors")
    if not isinstance(primary, str) or not primary.strip():
        raise ValueError(f"{path} has no primaryMajor")
    if not isinstance(entries, list) or not entries:
        raise ValueError(f"{path} has no supportedMajors")

    normalized: list[dict[str, Any]] = []
    seen: set[str] = set()
    for entry in entries:
        if not isinstance(entry, dict) or not str(entry.get("major", "")).strip():
            raise ValueError(f"{path} contains an entry without major")
        major = str(entry["major"]).strip()
        if major in seen:
            raise ValueError(f"{path} contains duplicate major {major}")
        seen.add(major)
        normalized.append(entry)
    if primary not in seen:
        raise ValueError(f"{path} primaryMajor {primary!r} is not supported")

    legacy_entries = document.get("legacyMajors", [])
    if legacy_entries is None:
        legacy_entries = []
    if not isinstance(legacy_entries, list):
        raise ValueError(f"{path} legacyMajors must be a list")

    legacy_normalized: list[dict[str, Any]] = []
    for entry in legacy_entries:
        if not isinstance(entry, dict) or not str(entry.get("major", "")).strip():
            raise ValueError(f"{path} contains a legacy entry without major")
        major = str(entry["major"]).strip()
        if major in seen:
            raise ValueError(f"{path} contains duplicate major {major}")
        seen.add(major)
        legacy_normalized.append(entry)

    return primary, normalized, legacy_normalized, path


def primary_entry(primary: str, entries: list[dict[str, Any]], path: Path) -> dict[str, Any]:
    for entry in entries:
        if str(entry["major"]) == primary:
            if not str(entry.get("defaultInstallPath", "")).strip():
                raise ValueError(f"{path} has no defaultInstallPath for primary major {primary}")
            return entry
    raise ValueError(f"{path} has no entry for primary major {primary}")


def display_names(entries: list[dict[str, Any]]) -> list[str]:
    return [str(entry.get("displayName") or f"GeneXus {entry['major']}") for entry in entries]


def join_values(values: list[str]) -> str:
    if len(values) < 2:
        return values[0] if values else ""
    if len(values) == 2:
        return f"{values[0]} and {values[1]}"
    return ", ".join(values[:-1]) + f", and {values[-1]}"


def generated_block(
    primary: str,
    entries: list[dict[str, Any]],
    legacy_entries: list[dict[str, Any]],
) -> str:
    names = display_names(entries)
    supported = ", ".join(names)
    legacy_names = ", ".join(display_names(legacy_entries)) or "none"
    legacy_drivers = join_values(
        [
            f"`{driver}`"
            for driver in sorted(
                {
                    str(entry.get("driver")).strip()
                    for entry in legacy_entries
                    if str(entry.get("driver", "")).strip()
                }
            )
        ]
    ) or "legacy adapters"
    return "\n".join(
        [
            f"<!-- BEGIN GENERATED: {MARKER} -->",
            f"Supported SDK majors: **{supported}** (native SDK).",
            f"Basic legacy compatibility: **{legacy_names}** via {legacy_drivers} (not the native SDK build).",
            f"Primary SDK: **GeneXus {primary}**.",
            "Source of truth: `config/gx-versions.json`.",
            f"<!-- END GENERATED: {MARKER} -->",
        ]
    )


def replace_marked_block(text: str, replacement: str, label: str) -> str:
    pattern = re.compile(
        rf"<!-- BEGIN GENERATED: {re.escape(MARKER)} -->.*?"
        rf"<!-- END GENERATED: {re.escape(MARKER)} -->",
        re.DOTALL,
    )
    matches = list(pattern.finditer(text))
    if len(matches) != 1:
        raise ValueError(
            f"{label} must contain exactly one generated {MARKER} block; found {len(matches)}"
        )
    return pattern.sub(lambda _: replacement, text, count=1)


def render_generated_doc(
    version: str,
    primary: str,
    entries: list[dict[str, Any]],
    legacy_entries: list[dict[str, Any]],
) -> str:
    rows = [
        "# Supported GeneXus versions",
        "",
        "<!-- This file is generated by scripts/sync-release-metadata.py. -->",
        "",
        f"Package version: `{version}`",
        f"Primary SDK major: `{primary}`",
        "",
        "## Native SDK support",
        "",
        "| Major | Display name | Default install path |",
        "|---|---|---|",
    ]
    for entry in entries:
        path = str(entry.get("defaultInstallPath", ""))
        display = str(entry.get("displayName") or f"GeneXus {entry['major']}")
        rows.append(f"| {entry['major']} | {display} | `{path}` |")
    rows.extend(
        [
            "",
            "The catalog describes recognized installations; capability evidence and persistence parity are reported separately by `genexus_sdk_probe mode=capabilities`.",
            "",
        ]
    )
    if legacy_entries:
        rows.extend(
            [
                "## Basic legacy compatibility",
                "",
                "These entries are not native-SDK support; they use the driver shown below and degrade unsupported modern tools explicitly.",
                "",
                "| Major | Display name | Driver | Default install path |",
                "|---|---|---|---|",
            ]
        )
        for entry in legacy_entries:
            path = str(entry.get("defaultInstallPath", ""))
            display = str(entry.get("displayName") or f"GeneXus {entry['major']}")
            driver = str(entry.get("driver") or "legacy-adapter")
            rows.append(f"| {entry['major']} | {display} | `{driver}` | `{path}` |")
        rows.append("")
    return "\n".join(rows)


def expected_files(root: Path, version: str) -> dict[Path, str]:
    primary, entries, legacy_entries, catalog_path = catalog_data(root)
    primary_path = str(primary_entry(primary, entries, catalog_path)["defaultInstallPath"])
    names = display_names(entries)
    supported = ", ".join(names)
    legacy_names = ", ".join(display_names(legacy_entries)) or "catalogued legacy versions"
    legacy_drivers = join_values(
        sorted(
            {
                str(entry.get("driver")).strip()
                for entry in legacy_entries
                if str(entry.get("driver", "")).strip()
            }
        )
    ) or "legacy adapters"
    description = (
        f"Read, edit, and analyze {supported} KB objects through the native SDK, "
        f"with basic legacy compatibility for {legacy_names} via {legacy_drivers} drivers, "
        "from Claude, Cursor, and AI agents."
    )
    package = read_json(root / "package.json")
    package["description"] = (
        f"{supported} MCP server with native SDK support; basic legacy compatibility for "
        f"{legacy_names} via {legacy_drivers} drivers — read, edit, and analyze Knowledge Base objects directly "
        "from Claude, Cursor, and other AI agents over the Model Context Protocol."
    )
    server = read_json(root / "server.json")
    server["version"] = version
    if not isinstance(server.get("packages"), list) or not server["packages"]:
        raise ValueError("server.json has no packages entry")
    server["packages"][0]["version"] = version
    server["description"] = description

    sample = read_json(root / "config.sample.json")
    sample.setdefault("GeneXus", {})["InstallationPath"] = primary_path

    return {
        root / "package.json": json.dumps(package, indent=2, ensure_ascii=False) + "\n",
        root / "server.json": json.dumps(server, indent=2, ensure_ascii=False) + "\n",
        root / "config.sample.json": json.dumps(sample, indent=2, ensure_ascii=False) + "\n",
        root / "README.md": replace_marked_block(
            (root / "README.md").read_text(encoding="utf-8"),
            generated_block(primary, entries, legacy_entries),
            "README.md",
        ),
        root / "AGENTS.md": replace_marked_block(
            (root / "AGENTS.md").read_text(encoding="utf-8"),
            generated_block(primary, entries, legacy_entries),
            "AGENTS.md",
        ),
        root / "docs" / "generated" / "supported-versions.md": render_generated_doc(
            version, primary, entries, legacy_entries
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).parents[1])
    parser.add_argument("--version", required=True)
    parser.add_argument("--write", action="store_true", help="write synchronized files")
    parser.add_argument("--check", action="store_true", help="fail when files need synchronization")
    args = parser.parse_args()
    if args.write == args.check:
        return fail("choose exactly one of --write or --check")

    version = args.version.lstrip("v")
    if not SEMVER.fullmatch(version):
        return fail(f"requested version is not semver: {args.version}")

    root = args.root.resolve()
    try:
        expected = expected_files(root, version)
    except (OSError, ValueError) as exc:
        return fail(str(exc))

    changed: list[Path] = []
    for path, content in expected.items():
        actual = path.read_text(encoding="utf-8") if path.exists() else None
        if actual == content:
            continue
        changed.append(path)
        if args.write:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8", newline="\n")

    if changed and args.check:
        print("release-sync: drift detected:", file=sys.stderr)
        for path in changed:
            print(f"  - {path.relative_to(root)}", file=sys.stderr)
        return 1

    action = "updated" if args.write else "valid"
    print(f"release-sync: {action} files={len(expected)} changed={len(changed)} version={version}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
