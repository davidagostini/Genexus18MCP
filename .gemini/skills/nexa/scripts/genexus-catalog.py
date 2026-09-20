#!/usr/bin/env python3
"""
genexus-catalog.py — Search knowledge base catalog definitions.

Usage:
	genexus-catalog.py list
	genexus-catalog.py list <catalog>
	genexus-catalog.py list <catalog> --where FIELD=VALUE [--where FIELD=VALUE ...]
	genexus-catalog.py list <catalog> --where FIELD:KEY=VALUE
	genexus-catalog.py list <catalog> --values FIELD
	genexus-catalog.py read <catalog> --entry <name>

Commands:
	list: Show available catalogs with descriptions
	list <catalog>: List all entries and available fields with sample values
	list <catalog> --where <expr>: Filter entries; repeatable with AND logic
	list <catalog> --values FIELD: List all distinct values for a field
	read <catalog> --entry <name>: Show full detail of one target entry given the name

Filter syntax (--where):
	FIELD=VALUE			scalar: substring | array: membership | dict: key exists
	FIELD:KEY=VALUE		dict: entry[FIELD][KEY] contains VALUE
"""

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Iterable

THIS_FILE = Path(__file__)
HERE_PATH = THIS_FILE.parent
DATA_PATH = HERE_PATH / THIS_FILE.stem

CATALOGS = {
	"elements": {
		"path": "ui-elements.json",
		"desc": "Layout elements for control definition with valid children, supported attributes, and parent/child rules",
	},
	"attributes": {
		"path": "ui-attributes.json",
		"desc": "Layout attributes for control properties with type, default, dependency conditions",
	},
	"methods": {
		"path": "ui-methods.json",
		"desc": "Runtime methods available on layout controls with syntax, scope, and platform restrictions",
	},
	"transform": {
		"path": "ui-transform.json",
		"desc": "Styling transform functions with syntax specification",
	},
	"styles": {
		"path": "ui-styles.json",
		"desc": "Styling properties per control type: type, default, applies-on",
	},
}

ENUM_PREFIX = "enum"

FIELD_SAMPLE_SIZE = 5
NON_FILTERABLE_FIELDS = {
	"description",
	"constraints"
}


def load(catalog: str) -> dict[str, Any]:
	"""Load and return the JSON data for the given catalog name."""
	path = DATA_PATH / CATALOGS[catalog]["path"]
	if not path.exists():
		return {}
	with open(path) as f:
		return json.load(f)


def field_index(data: dict[str, dict[str, Any]]) -> dict[str, list[str]]:
	"""Return dict of field -> sorted list of all distinct values."""
	fields = {}
	for entry in data.values():
		for field, value in entry.items():
			if field.lower() in NON_FILTERABLE_FIELDS:
				continue
			if field not in fields:
				fields[field] = set()
			if isinstance(value, list):
				fields[field].update(str(v) for v in value if v)
			elif isinstance(value, dict):
				fields[field].update(value.keys())
			elif value is not None and value != "":
				fields[field].add(str(value))
	return {
		field: sorted(values)
		for field, values in fields.items()
	}


def parse_where(expr: str) -> tuple[str, str | None, str]:
	"""Parse a --where expression into (field, key, value); key is None for scalar filters."""
	if "=" not in expr:
		sys.exit(f"--where requires FIELD=VALUE or FIELD:KEY=VALUE, got: {expr!r}")
	left, _, value = expr.partition("=")
	field, _, key = left.partition(":")
	return field.strip(), key.strip() or None, value.strip()


def match_where(name: str, entry: dict[str, Any], field: str, key: str | None, value: str) -> bool:
	"""Return True if the entry matches the given field/key/value filter."""
	needle = value.lower()
	if field.lower() == "name":
		return needle in name.lower()
	if field.lower() in NON_FILTERABLE_FIELDS:
		sys.exit(f"Field '{field}' is not filterable.")
	matched_field = next((k for k in entry if k.lower() == field.lower()), None)
	if matched_field is None:
		return False
	cell = entry[matched_field]
	if isinstance(cell, dict):
		if key:
			match = next((k for k in cell if k.lower() == key.lower()), None)
			return match is not None and needle in str(cell[match]).lower()
		return any(needle == k.lower() for k in cell)
	if isinstance(cell, list):
		return any(needle == str(v).lower() for v in cell)
	if needle.startswith(ENUM_PREFIX):
		source = str(cell).lower().strip()
		if not source.startswith(ENUM_PREFIX):
			return False
		source = source[len(ENUM_PREFIX):]
		if not source.startswith("{") or not source.endswith("}"):
			return False
		needle = needle[len(ENUM_PREFIX):]
		if not needle:
			return True
		pattern = needle[1:-1]
		options = source[1:-1].split(";")
		return any(pattern in option for option in options)
	return needle in str(cell).lower()


def resolve_entry(data: dict[str, Any], name: str) -> str:
	"""Return the matched key or exit with suggestions."""
	if name in data:
		return name
	match = next((k for k in data if k.lower() == name.lower()), None)
	if match:
		return match
	suggestions = [k for k in data if name.lower() in k.lower()]
	msg = f"Target entry '{name}' not found."
	if suggestions:
		msg += f"\n* Did you mean: {' | '.join(suggestions[:5])}"
	sys.exit(msg)


BULLETS = ['*', '-']


def print_value(label: str | None, value: Any, depth: int) -> None:
	"""Recursively print a labeled value with depth-based indentation and alternating bullet markers."""
	indent = '  ' * depth
	bullet = BULLETS[depth % 2]
	if isinstance(value, dict):
		if label:
			print(f"{indent}{bullet} {label}")
		for k, v in value.items():
			print_value(k, v, depth + 1)
	elif isinstance(value, list):
		if label:
			print(f"{indent}{bullet} {label}:")
		for item in value:
			print_value(None, item, depth + 1)
	elif not label:
		print(f"{indent}{bullet} {value}")
	elif not value:
		print(f"{indent}{bullet} {label}")
	else:
		print(f"{indent}{bullet} {label}: {value}")


def print_fields(entry: dict[str, Any]) -> None:
	"""Print all nfields of an entry using recursive bullet formatting."""
	for field, value in entry.items():
		print_value(field, value, 0)


def singular_noun(word: str) -> str:
	"""Return the singular form of a noun by stripping common English plural suffixes."""
	word = word.strip()
	label = word.lower()
	if label.endswith("ies"):
		label = word[:-3] + "y"
	elif label.endswith(("ses", "xes", "zes", "ches", "shes")):
		label = word[:-2]
	elif label.endswith("s") and not label.endswith("ss"):
		label = word[:-1]
	return label.capitalize()


def format_field_hint(catalog: str, fname: str, values: list[str]) -> str:
	"""Return a hint string showing sample values and overflow info."""
	display = sorted(set("enum" if v.startswith("enum{") else v for v in values))
	sample = display[:FIELD_SAMPLE_SIZE]
	hint = " | ".join(h for h in sample if h)
	remaining = len(display) - len(sample)
	if remaining > 0:
		hint += f" | (+{remaining} more, use --values {fname})"
	return hint


def column_width(items: Iterable[str], minimum: int = 0) -> int:
	return max(minimum, *(len(item) for item in items))


def cmd_list(args: argparse.Namespace) -> None:
	"""List catalog entries, applying optional filters or showing field value summaries."""
	if args.catalog is None:
		print("Catalogs:")
		width = column_width(CATALOGS)
		for name, meta in CATALOGS.items():
			status = "✓" if (DATA_PATH / meta["path"]).exists() else "✗"
			print(f"* {name:<{width}} [{status}]  {meta['desc']}")
		return

	data = load(args.catalog)
	if not data:
		print(f"[{args.catalog}] No data available (file missing or empty).")
		return

	index = field_index(data)

	if args.values:
		matched_field = next((f for f in index if f.lower() == args.values.lower()), None)
		if matched_field is None:
			sys.exit(f"Field '{args.values}' not found. Available: {' | '.join(index)}")
		values = index[matched_field]
		print(f"{matched_field} values ({len(values)} total):")
		for value in values:
			print(f"* {value}")
		return

	filters = [parse_where(w) for w in args.where] if args.where else []
	entries = sorted(
		(name, entry)
		for name, entry in data.items()
		if all(
			match_where(name, entry, f, k, v)
			for f, k, v in filters
		)
	)

	if filters:
		names = {v.lower() for f, k, v in filters if f.lower() == "name" and k is None}
		exact = [(n, e) for n, e in entries if n.lower() in names]
		if exact:
			entries = exact

	header = args.catalog.capitalize()
	if filters:
		header += f" matching {' & '.join(args.where)}"

	if filters and len(entries) == 1: # single match
		label = singular_noun(args.catalog)
		name, entry = entries[0]
		print(f"{label}: {name}")
		print_fields(entry)
	elif entries:
		print(f"{header} ({len(entries)} entries):")
		width = column_width((name for name, _ in entries))
		for name, entry in entries:
			desc = entry.get("Description") or entry.get("description", "")
			print(f"* {name:<{width}} {desc}")
		if filters:
			print("\nWhich one did you mean?")
	else:
		print("No matches, relax criteria." if filters else "No entries, empty catalog.")


	if not filters:
		print()
		print("Fields:")
		width = column_width(index, len("Name"))
		print(f"* {'Name':<{width}} → target name")
		for fname, values in index.items():
			hint = format_field_hint(args.catalog, fname, values)
			print(f"* {fname:<{width}} → {hint}")


def cmd_read(args: argparse.Namespace):
	"""Print the full detail of a single catalog entry by name."""
	data = load(args.catalog)
	if not data:
		sys.exit(f"[{args.catalog}] No data available (file missing or empty).")

	name = resolve_entry(data, args.entry)
	label = args.catalog.rstrip("s").capitalize()
	print(f"{label}: {name}")
	print_fields(data[name])


catalog_lines = '\n'.join(
	f"\t{name:<12} {meta['desc']}"
	for name, meta in CATALOGS.items()
)

EPILOG = f"""
{__doc__}

Catalog:
{catalog_lines}
"""


def main():
	"""Parse CLI arguments and dispatch to the appropriate command handler."""
	parser = argparse.ArgumentParser(
		prog="genexus-catalog.py",
		description="Query knowledge base definition catalogs.",
		formatter_class=argparse.RawDescriptionHelpFormatter,
		epilog=EPILOG,
	)
	sub = parser.add_subparsers(dest="command", required=True)

	lp = sub.add_parser("list", help="List catalog entries")
	lp.add_argument("catalog", help="Catalog name (omit to see all catalogs)", nargs="?", choices=list(CATALOGS))
	lp.add_argument("--where", "-w", help="Filter entries by field value; use FIELD:KEY=VALUE for dict fields (repeatable, AND logic)", metavar="FIELD[=VALUE|:KEY=VALUE]", action="append")
	lp.add_argument("--values", "-v", help="List all distinct values for a field", metavar="FIELD")

	rp = sub.add_parser("read", help="Read a catalog entry in full")
	rp.add_argument("catalog", choices=list(CATALOGS), metavar="CATALOG", help="Catalog name (choices: " + ", ".join(CATALOGS) + ")")
	rp.add_argument("--entry", "-e", help="Target entry name (case-insensitive)", required=True, metavar="NAME")

	args = parser.parse_args()
	match args.command:
		case "list": cmd_list(args)
		case "read": cmd_read(args)


if __name__ == "__main__":
	main()
