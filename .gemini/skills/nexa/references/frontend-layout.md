---
name: frontend-layout
description: Index for layout structure definition
---

Layout structure reference

---

# RUNTIMES
- `Web`: Classic HTML/CSS/JS web stack
- `Native`: Android, iOS, Angular platforms

---

# TYPE SEMANTICS

## Primitives
- `boolean`: Flag value
- `integer`: Numeric value without decimals
- `decimal`: Numeric value with optional decimals
- `string`: Text value
- `measure`: Numeric value with required unit
	* `%` relative
	* `px` absolute for `Web`
	* `dip` absolute for `Native`
- `color`: Color value
	* Known name; e.g. `Red`. `Green`, `Blue`, `Transparent`, etc
	* Hex representation value as `#rrggbb(aa)?` or `#rgb(a)?`
	* Token reference by `#color.<token-name>` from `DesignSystem` object

## Constructors
- `class(…)`: Style class name for the given control type, or any if omitted
- `enum{…}`: Closed list of literal values
- `code(…)`: GeneXus snippet returning type; void if unspecified
- `keys(…)`: Space-separated sequence of given type
- `list(…)`: Comma-separated sequence of given type
- `pack(…)`: Semicolon-separated sequence of given type
- `quad(…)`: Space-separated values of types
- `set(…)`: One or more of the listed literals, in any order
- `ref(…)`: References an existing object of provided types
- `sel(…)`: Resolves name from query path within file scope
- `…[n]`: Exactly n space-separated values of type

---

# REFERENCES
Explore layout elements, supported attributes, and parent/child rules
- Run `python scripts/genexus-catalog.py -h` for full usage
- Relevant catalogs: `elements`, `attributes`, `methods`

Notes:
- All attributes are design-time by default in layout definition
- The `Access` field indicates when attribute can be used in event event code:
	* `init`: DesignTime init; default when field is omitted
	* `set`: RunTime set value
	* `get`: RunTime get value
- All runtime attributes referenced in event code must use PascalCase

---

# CONSTRAINTS
- Indent using tabs (`\t`); whitespaces forbidden
- Indent attributes one tab deeper than the element
- Expand element as multiline when more than two attributes
- Place element name alone on opening line
- Place one attribute per line
