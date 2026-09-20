---
name: properties-object-work-with-web
description: Configurable WorkWithForWeb pattern instance properties
---

Use this file to select editable properties, defaults, and valid options for a `WorkWithForWeb` pattern instance and its nodes

---

# GENERAL
Include [General](./properties-common.md) properties

## MasterPage
The master page applied to the generated screen.
- Type: `string`
- Default: `<default>`

## CustomStartEventCode
GeneXus code injected at the beginning of the generated object's `Start` event
- Type: `string`

## RowsPerPage
Number of rows displayed per page; use `<default>` to inherit from pattern settings, or `<unlimited>` for no paging
- Type: `string`
- Default: `<default>`

---

# INSTANCE NODE

## UpdateTransaction
How the pattern updates the source Transaction object when regenerating
- Type: `enum{Do not update,Only rules and events,Apply WW Style,Create default}`
- Default: `Only rules and events`
- Options:
	* `Do not update`: Leave the Transaction object unchanged
	* `Only rules and events`: Update only the rules and events sections
	* `Apply WW Style`: Apply WorkWithForWeb style settings to the Transaction
	* `Create default`: Recreate a default Transaction layout

---

# TRANSACTION NODE

## GenerateNoPromptRule
When `true`, generates a `NoPrompt` rule on the source `Transaction` to suppress automatic prompt navigation for all attributes
- Type: `boolean`
- Default: `False`

---

# LEVEL NODE

## Name
Existing Transaction level name
- Type: `string`

## Description
Human-readable label for the description attribute
- Type: `string`

---

# DESCRIPTOR NODE

## Attribute
Name used as record description, sort key, and source attribute
- Type: `string`

## Description
Human-readable label for the description attribute
- Type: `string`

---

# SELECTION NODE

## MasterPage
The master page applied to the generated screen.
- Type: `string`
- Default: `<default>`

## CustomStartEventCode
GeneXus code injected at the beginning of the generated object's `Start` event
- Type: `string`

## RowsPerPage
Number of rows displayed per page; use `<default>` to inherit from pattern settings, or `<unlimited>` for no paging
- Type: `string`
- Default: `<default>`

## Description
Human-readable label for the description attribute
- Type: `string`

## PagingMode
Paging strategy applied to the selection grid
- Type: `enum{One page at a time,Infinite scrolling}`
- Default: `One page at a time`
- Options:
	* `One page at a time`
	* `Infinite scrolling`

## ShowCurrentPage
Show the current page indicator in the selection grid paging controls
- Type: `boolean`
- Default: `False`

## IsMain
Marks this `Selection` as the main entry point of the level; controls which WW screen is the root
- Type: `boolean`
- Default: `True`

---

# MODES NODE

## Insert
Supports insertion in interface
- Type: `boolean`
- Default: `True`

## Update
Supports updating records in the interface
- Type: `boolean`
- Default: `True`

## Delete
Enables deletion in the interface
- Type: `boolean`
- Default: `True`

## Display
Supports read-only view in selection screen
- Type: `boolean`
- Default: `True`

## Export
Enable the Export action in the selection screen
- Type: `boolean`
- Default: `True`

## InsertCondition
GeneXus expression that must evaluate to `True` for the Insert action to be available
- Type: `string`

## UpdateCondition
GeneXus expression that must evaluate to `True` for the Update action to be available
- Type: `string`

## DeleteCondition
GeneXus expression that must evaluate to `True` for the Delete action to be available
- Type: `string`

## DisplayCondition
GeneXus expression that must evaluate to `True` for the Display action to be available
- Type: `string`

## ExportCondition
GeneXus expression that must evaluate to `True` for the Export action to be available
- Type: `string`

---

# ATTRIBUTE NODE

## Attribute
Name used as record description, sort key, and source attribute
- Type: `string`

## Description
Human-readable label for the description attribute
- Type: `string`

## Autolink
Automatically navigate to `View<transaction>` when the cell value is clicked
- Type: `boolean`
- Default: `False`

## Visible
Show or hide the column in the grid
- Type: `boolean`
- Default: `True`

---

# ORDER NODE

## Attribute
Name used as record description, sort key, and source attribute
- Type: `string`

## Description
Human-readable label for the description attribute
- Type: `string`

## Ascending
Sort direction for the attribute
- Type: `boolean`
- Default: `True`
- Options:
	* `True`: Ascending order
	* `False`: Descending order

---

# FILTER NODE

## Name
Existing Attribute or Variable name
- Type: `string`

## Description
Human-readable label for the description attribute
- Type: `string`

## Default
Default value used when no input value is provided
- Type: `string`

---

# CONDITION NODE

## Value
GeneXus condition expression applied as a filter; may reference filter variables with `&` prefix
- Type: `string`

---

# VIEW NODE

## MasterPage
The master page applied to the generated screen.
- Type: `string`
- Default: `<default>`

## CustomStartEventCode
GeneXus code injected at the beginning of the generated object's `Start` event
- Type: `string`

## Caption
Human-readable label for various UI elements
- Type: `string`

## Description
Human-readable label for the description attribute
- Type: `string`

## BackToSelection
Show a back link to the `WW<transaction>` selection screen
- Type: `boolean`
- Default: `True`

---

# PARAMETER NODE

## Domain
Domain name of the parameter attribute; used for type validation
- Type: `string`

## NullValue
Accept null values for this parameter
- Type: `boolean`
- Default: `False`

---

# TAB NODE

## MasterPage
The master page applied to the generated screen.
- Type: `string`
- Default: `<default>`

## CustomStartEventCode
GeneXus code injected at the beginning of the generated object's `Start` event
- Type: `string`

## RowsPerPage
Number of rows displayed per page; use `<default>` to inherit from pattern settings, or `<unlimited>` for no paging
- Type: `string`
- Default: `<default>`

## Caption
Human-readable label for various UI elements
- Type: `string`

## Code
Single-word code identifying the tab; used for tab switching and URL routing; must be unique within the instance
- Type: `string`

## Description
Human-readable label for the description attribute
- Type: `string`

## Type
Rendering type for the tab content
- Type: `enum{Tabular,Grid,UserDefined}`
- Options:
	* `Tabular`
	* `Grid`
	* `UserDefined`

## ComponentName
A `WebComponent` object generated for this tab
- Type: `string`
- Require: `Type = Tabular`

## Condition
Expression to control tab visibility and action enablement
- Type: `string`

---

# ACTION NODE

## Name
Unique name
- Type: `string`

## Caption
Human-readable label for various UI elements
- Type: `string`

## Gxobject
GeneXus object invoked when the action is executed; required for non-standard actions
- Type: `string`

## Image
Image displayed on action button or when selected
- Type: `string`

## Tooltip
Tooltip text shown when the mouse hovers over the action
- Type: `string`

## Condition
Expression to control tab visibility and action enablement
- Type: `string`

## ButtonClass
Web theme class applied to the action button
- Type: `string`

## InGridClass
Web theme class applied to the action when rendered as a column inside the grid
- Type: `string`

## CallType
How the invoked object is called when the action is triggered
- Type: `enum{Auto,Call,Link}`
- Default: `Auto`
- Options:
	* `Auto`: uses `Link` for all objects except non-main Procedures
	* `Call`: generates an event that calls the object
	* `Link`: uses the web Link method in the grid load logic
