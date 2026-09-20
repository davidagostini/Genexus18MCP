---
name: properties-object-user-control
description: Configurable user control properties
---

Use this file to select editable properties, defaults, and valid options for this target

---

# GENERAL
Include [General](./properties-common.md) properties

## Is Control Type
Use as selectable ControlType for Attributes and Variables elements
- Type: `boolean`

## Data Type Filter
Comma separated supported data types: `character`, `varchar`, `longvarchar`, `numeric`, `date`, `datetime`, `image`, `video`, or any `SDT` object name
- Type: `string`
- When: `IsControlType = true`

## References
Semicolon-separated JS/CSS references with relative/absolute paths supported by this control
- Type: `string`

## Base Control Type
Specify the standard GeneXus control behavior extended by this control
- Type: `enum{None;Button;ComboBox;CheckBox;Edit;ErrorViewer;Grid;Group;HorizontalRule;HyperLink;Image;ListBox;RadioButton;Section;Tab;Table;TextBlock}`
- Options:
	* `None`: No base control type
	* `Button`: Clickable action control
	* `ComboBox`: Dropdown selection control
	* `CheckBox`: Boolean selection control
	* `Edit`: Text input control
	* `ErrorViewer`: Error display control
	* `Grid`: Tabular data control
	* `Group`: Container grouping control
	* `HorizontalRule`: Horizontal separator line
	* `HyperLink`: Navigable link control
	* `Image`: Image display control
	* `ListBox`: List selection control
	* `RadioButton`: Exclusive selection control
	* `Section`: Section container control
	* `Tab`: Tab page control
	* `Table`: Table layout control
	* `TextBlock`: Read-only text display control

## Base Style
Base Style for this `User Control` object
- Type: `string`