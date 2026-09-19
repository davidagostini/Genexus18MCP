---
name: frontend-styles
description: Index for control styling definition
---

GeneXus-exclusive style properties

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
	* `px` absolute for `Web` runtime
	* `dip` absolute for `Native` runtime
- `color`: Color value
	* Known name; e.g. `Red`. `Green`, `Blue`, `Transparent`, etc
	* Hex representation value as `#rrggbb(aa)?` or `#rgb(a)?`
	* Token reference by `#color.<token-name>` from `DesignSystem` object

## Constructors
- `class(…)`: Style class name for the given control type, or any if omitted
- `enum{…}`: Closed list of literal values
- `ref(…)`: References an existing object of provided type

---

# REFERENCES
Explore styling properties for any supported control
- Run `python scripts/genexus-catalog.py -h` for full usage
- Relevant catalogs: `styles`, `transform`

---

# CONSTRAINTS
- Use `gx-image(<object-name>)` function for `Image` object references
- Use `gx-file(<object-name>)` function for `File` object references to font files
- Set `Style` property at `Panel` object with target `DesignSystem` object
- Map `Style` classes from `Panel` object layout controls
- Use `@font-face` with these properties:
	* Required: `src`, `font-family`
	* Optional: `font-weight`, `font-style`, `font-display`