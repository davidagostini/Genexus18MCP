#!/usr/bin/env python3
"""Three-way merge of src/GxMcp.Gateway/tool_definitions.json.

The file keeps one very long JSON object per tool, so Git reports a whole-line conflict whenever
two branches touch the same tool, even in unrelated fields. This merges by tool name and then by
field, and falls back to a sentence-level text merge only for a string both sides edited.

    git show :1:path > base.json; git show :2:path > ours.json; git show :3:path > theirs.json
    python scripts/merge-tool-definitions.py base.json ours.json theirs.json -o tool_definitions.json

Exit status is 1 and nothing is written when two sides changed the same value differently.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile


class Conflict(Exception):
    pass


def load_tools(path):
    with open(path, encoding="utf-8") as handle:
        tools = json.load(handle)
    if not isinstance(tools, list):
        raise ValueError(f"{path}: expected an array of tool definitions")
    by_name = {}
    order = []
    for tool in tools:
        name = tool.get("name") if isinstance(tool, dict) else None
        if not isinstance(name, str) or not name:
            raise ValueError(f"{path}: every tool must have a nonempty name")
        if name in by_name:
            raise ValueError(f"{path}: duplicate tool name {name!r}")
        by_name[name] = tool
        order.append(name)
    return by_name, order


def merge_text(base, ours, theirs, where):
    pieces = {}
    with tempfile.TemporaryDirectory() as directory:
        for label, text in (("ours", ours), ("base", base), ("theirs", theirs)):
            path = os.path.join(directory, label)
            with open(path, "w", encoding="utf-8", newline="") as handle:
                handle.write(text.replace(". ", ".\n"))
            pieces[label] = path
        result = subprocess.run(
            ["git", "merge-file", "-p", pieces["ours"], pieces["base"], pieces["theirs"]],
            capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        raise Conflict(where)
    return result.stdout.replace(".\n", ". ")


def merge_value(base, ours, theirs, where):
    if ours == theirs:
        return ours
    if ours == base:
        return theirs
    if theirs == base:
        return ours
    if isinstance(ours, dict) and isinstance(theirs, dict) and isinstance(base, (dict, type(None))):
        base = base or {}
        merged = {}
        # Keep our key order, then append keys only the other side added.
        for key in list(ours) + [key for key in theirs if key not in ours]:
            absent = object()
            b, o, t = base.get(key, absent), ours.get(key, absent), theirs.get(key, absent)
            if o is absent and t is absent:
                continue
            if o is absent:
                if b is absent:
                    merged[key] = t  # added only on their side
                    continue
                if t == b:
                    continue  # we deleted it, they left it alone
                raise Conflict(f"{where}/{key}")
            if t is absent:
                if b is absent:
                    merged[key] = o  # added only on our side
                    continue
                if o == b:
                    continue  # they deleted it, we left it alone
                raise Conflict(f"{where}/{key}")
            merged[key] = merge_value(None if b is absent else b, o, t, f"{where}/{key}")
        return merged
    if all(isinstance(value, str) for value in (base, ours, theirs)):
        return merge_text(base, ours, theirs, where)
    raise Conflict(where)


def merge(base_path, ours_path, theirs_path):
    base, _ = load_tools(base_path)
    ours, ours_order = load_tools(ours_path)
    theirs, theirs_order = load_tools(theirs_path)
    names = list(ours_order) + [name for name in theirs_order if name not in ours]
    merged = []
    for name in names:
        b, o, t = base.get(name), ours.get(name), theirs.get(name)
        if o is None:
            if b is None:
                merged.append(t)  # added only on their side
                continue
            if t == b:
                continue
            raise Conflict(name)
        if t is None:
            if b is None:
                merged.append(o)  # added only on our side
                continue
            if o == b:
                continue
            raise Conflict(name)
        merged.append(merge_value(b, o, t, name))
    return merged


def render(tools):
    lines = [json.dumps(tool, separators=(",", ":"), ensure_ascii=False) for tool in tools]
    return "[\n" + ",\n".join("  " + line for line in lines) + "\n]\n"


def write_atomic(path, text):
    directory = os.path.dirname(os.path.abspath(path))
    descriptor, temporary = tempfile.mkstemp(prefix=".tool-definitions-", suffix=".tmp", dir=directory)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("base")
    parser.add_argument("ours")
    parser.add_argument("theirs")
    parser.add_argument("-o", "--output", required=True)
    args = parser.parse_args(argv)
    try:
        text = render(merge(args.base, args.ours, args.theirs))
    except Conflict as conflict:
        print(f"conflict at {conflict}: both sides changed it differently; resolve by hand", file=sys.stderr)
        return 1
    write_atomic(args.output, text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
