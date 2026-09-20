---
name: model-work-with-web
description: Work With pattern setting for Web defining template defaults, objects naming, style, labels, grid, master pages, actions, context variables, and security
---

Generates or interprets the `WorkWith` for web configuration file

---

# DEFINITION
The `WorkWith` pattern setting for web configures global defaults for the [WorkWithWeb object](./object-work-with-web.md): template behavior, object naming conventions, style classes, label defaults, grid settings, master pages, standard CRUD action appearance, context variables, and security authorization objects

---

# SYNTAX
~~~
PatternSettings WorkWithForWeb
{
	WwConfiguration
	{
		<template>
		<objects>
		<theme>
		<labels>
		<grid>
		<masters>
		<actions>
		<context>
		<security>
	}

	#Properties
		<properties>
	#End
}
~~~

Where:
- `<template>`: Template and navigation defaults; see [TEMPLATE](#template) section
- `<objects>`: Naming conventions for generated objects; see [OBJECTS](#objects) section
- `<theme>`: Style class overrides; see [THEME](#theme) section
- `<labels>`: Default label text values; see [LABELS](#labels) section
- `<grid>`: Grid rendering and paging settings; see [GRID](#grid) section
- `<masters>`: Master page assignments per object type; see [MASTERPAGES](#masterpages) section
- `<actions>`: Standard action blocks; see [ACTIONS](#actions) section section
- `<context>`: Context variable definitions; see [CONTEXT](#context) section
- `<security>`: Authorization attributes; see [SECURITY](#security) section
- `<properties>`: Optional object properties in TOML syntax; see [properties](./properties-model-work-with-web.md)

---

# TEMPLATE
Configures default behavior for generated web objects and navigation flow

Syntax:
~~~
Template
[
	<properties>
]
~~~

Where:
- `<properties>`: See [TEMPLATE node](./properties-model-work-with-web.md#template-node) properties

---

# OBJECTS
Configures naming conventions for generated web objects

Syntax:
~~~
Objects
[
	<properties>
]
~~~

Where:
- `<properties>`: See [OBJECTS node](./properties-model-work-with-web.md#objects-node) properties

---

# THEME
Configures the style class overrides applied to generated web objects

Syntax:
~~~
Theme
[
	<properties>
]
~~~

Where:
- `<properties>`: See [THEME node](./properties-model-work-with-web.md#theme-node) properties

---

# LABELS
Configures default label text used across generated web objects

Syntax:
~~~
Labels
[
	<properties>
]
~~~

Where:
- `<properties>`: See [LABELS node](./properties-model-work-with-web.md#labels-node) properties

---

# GRID
Configures grid rendering and paging behavior for generated web objects

Syntax:
~~~
Grid
[
	<properties>
]
~~~

Where:
- `<properties>`: See [GRID node](./properties-model-work-with-web.md#grid-node) properties

---

# MASTERPAGES
Assigns master page objects to each generated web object type

Syntax:
~~~
MasterPages
[
	<properties>
]
~~~

Where:
- `<properties>`: See [MASTERS node](./properties-model-work-with-web.md#masters-node) properties

---

# ACTIONS
Container for the six standard action blocks

Syntax:
~~~
StandardActions
{
	<action>
	…
}
~~~

Where:
- `<action>`: See [ACTION](#action) section

Rules:
- All six actions must be present: `Insert`, `Update`, `Delete`, `Display`, `Export`, `Search`

---

# ACTION
Declares default appearance and behavior shared by all standard CRUD action blocks in the web `Work With` pattern

Syntax:
~~~
<name>
[
	<properties>
]
~~~

Where:
- `<name>`: One of `Insert`, `Update`, `Delete`, `Display`, `Export`, `Search` modes
- `<properties>`: See [ACTION node](./properties-model-work-with-web.md#action-node) properties

---

# CONTEXT
Container for zero or more `ContextVariable` element blocks

Syntax:
~~~
Context
{
	<variable>
	…
}
~~~

Where:
- `<variable>`: One `ContextVariable` element block; see [VARIABLE](#variable)

Rules:
- Must contain zero or more `ContextVariable` entries
- Omit `Context` entirely when no context variables are defined

---

# VARIABLE
Declares a single context variable passed into generated web objects

Syntax:
~~~
ContextVariable
[
	Name = '<var-name>',
	Type = '<type>',
	LoadProcedure = '<load-procedure>',
	UseInitialValue = '<use-initial-value>'
]
~~~

Where:
- `<properties>`: See [CONTEXT node](./properties-model-work-with-web.md#context-node) properties

---

# SECURITY
Configures the authorization check and redirect for generated web objects

Syntax:
~~~
Security
[
	<properties>
]
{
	// Optional: Omit Parameters when no additional parameters are needed
	Parameters
	{
		<parameter>
		…
	}
}
~~~

Where:
- `<properties>`: See [SECURITY node](./properties-model-work-with-web.md#security-node) properties
- `<parameter>`: One `Parameter` element block; see [SECURITYPARAMETER](#securityparameter)

Rules:
- Omit `Parameters` when no additional parameters are needed
- Ensure `Check` value is an existing `Procedure` object name if set
- Ensure `NotAuthorized`value is an existing `WebPanel` object if set

---

# PARAMETER
Declares a single additional parameter passed to the authorization check procedure

Syntax:
~~~
Parameter
[
	Name = '<name>'
]
~~~

Where:
- `<name>`: Parameter name passed to the authorization check

---

# OUTPUT
Use [global-output](../../nexa/references/global-output.md) with:
- Location: `src/#patternsettings/`
- Main: `WorkWithForWeb.gx`

---

# CONSTRAINTS
- Use [global-constraints](../../nexa/references/global-constraints.md)
- Exactly one `PatternSettings WorkWithForWeb` file exists per Knowledge Base
- Never rename the artifact; the identifier must always be `WorkWithForWeb`
- Keep `#Properties` `Description` equal to `"Work With for Web"`
- Define all six standard actions: `Insert`, `Update`, `Delete`, `Display`, `Export`, `Search`
- Omit any section whose properties all retain their default values; do not write empty or default-only sections

---

# EXAMPLES

## Example 1
Default WorkWithForWeb pattern settings with standard actions and security configuration
~~~
PatternSettings WorkWithForWeb
{
	WwConfiguration
	{
		StandardActions
		{
			Insert
			[
				Caption = 'GXM_insert',
				Tooltip = 'GXM_insert',
				Image = 'ActionInsert',
				DisabledImage = 'ActionDisabled',
				InGridClass = ''
			]
			Update
			[
				Caption = 'GXM_update',
				Tooltip = 'GXM_update',
				DisabledClass = '',
				InGridClass = ''
			]
			Delete
			[
				Caption = 'GX_BtnDelete',
				Tooltip = 'GX_BtnDelete',
				DisabledClass = '',
				InGridClass = ''
			]
			Display
			[
				Caption = 'GXM_display',
				Tooltip = 'GXM_display',
				EnabledByDefault = 'False',
				DisabledClass = '',
				InGridClass = ''
			]
			Export
			[
				Caption = 'Export',
				Tooltip = 'Export to Excel',
				EnabledByDefault = 'False',
				Image = 'ActionExport',
				DisabledImage = 'ActionDisabled'
			]
			Search
			[
				Caption = 'GX_BtnSearch',
				Tooltip = 'GX_BtnSearch'
			]
		}
		Security
		[
			Check = 'IsAuthorized',
			NotAuthorized = 'NotAuthorized'
		]
	}

	#Properties
		Description = "Work With for Web"
	#End
}
~~~

Saved as:
~~~
src/#patternsettings/WorkWithForWeb.gx
~~~
