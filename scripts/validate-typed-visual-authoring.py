#!/usr/bin/env python3
"""Validate the design-only acceptance corpus for Plan 108."""

from __future__ import annotations

import json
import pathlib
import sys
from typing import Any


EXPECTED_IDS = [f"VA{i:02d}" for i in range(1, 11)]
REQUIRED_STAGES = {
    "authoritative-surface",
    "typed-intent",
    "sdk-mutation",
    "schema-and-gotchas",
    "generate-build",
    "browser-preview",
    "persist-readback",
}


def validate(document: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if document.get("schemaVersion") != "visual-authoring-acceptance/1":
        errors.append("schemaVersion must be visual-authoring-acceptance/1")
    if document.get("status") != "DESIGN_NOT_EXECUTED":
        errors.append("status must be DESIGN_NOT_EXECUTED")

    execution = document.get("executionContract", {})
    if execution.get("requiresLiveSdk") is not True:
        errors.append("executionContract.requiresLiveSdk must be true")
    if execution.get("missingPrerequisite") != "SKIPPED":
        errors.append("executionContract.missingPrerequisite must be SKIPPED")

    contracts = document.get("typedContracts", {})
    request = contracts.get("request", {})
    receipt = contracts.get("receipt", {})
    evidence = contracts.get("previewEvidence", {})
    if request.get("contractVersion") != "visual-authoring/1":
        errors.append("request contractVersion must be visual-authoring/1")
    if "rollbackOnFailure" not in request.get("required", []):
        errors.append("request required fields must include rollbackOnFailure")
    if "raw-xml" in receipt.get("mutationRoutes", []):
        errors.append("receipt mutationRoutes must not include raw-xml")
    if set(receipt.get("persistenceBooleans", [])) != {
        "attempted",
        "committed",
        "verified",
        "rolledBack",
    }:
        errors.append("receipt persistenceBooleans must be explicit")
    if "sha256" not in evidence.get("artifactHashes", []):
        errors.append("previewEvidence artifactHashes must include sha256")

    scenarios = document.get("scenarios", [])
    ids = [scenario.get("id") for scenario in scenarios]
    for scenario_id in ids:
        if ids.count(scenario_id) > 1:
            errors.append(f"duplicate scenario id: {scenario_id}")
            break
    if ids != EXPECTED_IDS:
        errors.append("scenario ids must be VA01..VA10")
    for scenario in scenarios:
        missing = set(scenario.get("requiredStages", [])) - REQUIRED_STAGES
        if missing:
            errors.append(
                f"{scenario.get('id')} has unknown validation stages: {sorted(missing)}"
            )
        if not scenario.get("oracle"):
            errors.append(f"{scenario.get('id')} must have an oracle")
    return errors


def main() -> int:
    corpus_path = pathlib.Path(__file__).resolve().parents[1] / "plans" / "108-typed-visual-authoring.acceptance.json"
    try:
        document = json.loads(corpus_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"invalid corpus: {exc}", file=sys.stderr)
        return 2
    errors = validate(document)
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print(f"valid: {corpus_path} ({len(document['scenarios'])} scenarios)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
