---
name: properties-model-workwithweb
description: Configurable WorkWithForWeb pattern settings properties
---

Use this file to select editable properties, defaults, and valid options for the `WorkWithForWeb` pattern settings

---

# GENERAL

## Description
Human-readable label for the description attribute
- Type: `string`

---

# TEMPLATE node

## FormTemplate
Form layout template applied to generated web objects
- Type: `enum{<default>,Carmine Template,Unanimo Template,Flat Template,Fiori Template}`
- Default: `<default>`
- Options:
	* `<default>`: Default GeneXus template
	* `Carmine Template`: Carmine design template
	* `Unanimo Template`: Unanimo design template
	* `Flat Template`: Flat design template
	* `Fiori Template`: SAP Fiori-style template

## UpdateTransaction
How the pattern updates the source Transaction object when regenerating
- Type: `enum{Do not update,Only rules and events,Apply WW Style,Create default}`
- Default: `Only rules and events`
- Options:
	* `Do not update`: Leave the Transaction object unchanged
	* `Only rules and events`: Update only the rules and events sections
	* `Apply WW Style`: Apply WorkWith style settings to the Transaction
	* `Create default`: Recreate a default Transaction layout

## GenerateNoPromptRule
When `true`, generates a `NoPrompt` rule on the source `Transaction` to suppress automatic prompt navigation for all attributes
- Type: `boolean`
- Default: `False`

## SelectionIsMain
When `true`, treat Selection nodes as main objects in the navigation flow
- Type: `boolean`
- Default: `False`

## TabsForParallelTransactions
When `true`, adds a section to the detail for each parallel transaction instead of a single view
- Type: `boolean`
- Default: `False`

## CallTransactionsOnlyIfWorkWithApplied
When `true`, generate calls only to transactions that have the WorkWith pattern applied
- Type: `boolean`
- Default: `True`

## UseTransactionContext
When `true`, use the navigation context as insert parameters in transaction calls
- Type: `boolean`
- Default: `True`

## AfterInsert
Navigation target after a successful insert
- Type: `enum{Return to Caller,Go to View,Go to Selection}`
- Default: `Return to Caller`
- Options:
	* `Return to Caller`: Navigate back to the calling object
	* `Go to View`: Navigate to the View panel of the inserted record
	* `Go to Selection`: Navigate to the Selection panel

## AfterUpdate
Navigation target after a successful update
- Type: `enum{Return to Caller,Go to View,Go to Selection}`
- Default: `Return to Caller`
- Options:
	* `Return to Caller`: Navigate back to the calling object
	* `Go to View`: Navigate to the View panel of the updated record
	* `Go to Selection`: Navigate to the Selection panel

## AfterDelete
Navigation target after a successful deletion
- Type: `enum{Return to Caller,Go to Selection}`
- Default: `Return to Caller`
- Options:
	* `Return to Caller`: Navigate back to the calling object
	* `Go to Selection`: Navigate to the Selection panel

## FixVariableLoadCode
When `true`, adds missing variable assignments in the generated load code
- Type: `boolean`
- Default: `True`

---

# OBJECTS node

## View
Naming pattern for generated View webpanel objects; default `View<name>`, where `<name>` is the Transaction name
- Type: `string`

## Selection
Naming pattern for generated Selection webpanel objects; use `<Object>` as a placeholder for the transaction name
- Type: `string`
- Default: `WW<Object>`

## Tabular
Naming pattern for generated Tabular (general tab) webpanel objects; default `'<name>General'`, where <name> is the Transaction name
- Type: `string`

## Export
Naming pattern for generated Export procedure objects; default `'Export<name>'`, where <name> is the Transaction name
- Type: `string`

---

# THEME node

## Style
Name of a `DesignSystem` object used for styling
- Type: `string`

## SetObjectTheme
When `true`, set the selected design system style as the theme property on each generated object
- Type: `boolean`
- Default: `False`

## Button
Web theme class applied to button controls in generated objects; template-dependent if omitted
- Type: `string`

## Image
Image displayed on action button or when selected
- Type: `string`

## Subtitle
Web theme class applied to subtitle text in generated objects; template-dependent if omitted
- Type: `string`

## TextToLink
Web theme class applied to text-to-link controls in generated objects; template-dependent if omitted
- Type: `string`

## PlainText
Web theme class applied to plain text elements in generated objects; template-dependent if omitted
- Type: `string`

## Label
Web theme class applied to attribute labels in generated objects
- Type: `string`
- Default: `Label`

## ViewTable
Web theme class applied to the outer table in the View panel; template-dependent if omitted
- Type: `string`

## Grid
Web theme class applied to grids in generated objects; template-dependent if omitted
- Type: `string`

## Table100
Web theme class applied to 100%-width tables in generated objects
- Type: `string`
- Default: `Table100x100`

## TableGridContainer
Web theme class applied to the grid container table in generated objects; template-dependent if omitted
- Type: `string`

## Separator
Web theme class applied to separator elements in generated objects
- Type: `string`
- Default: `Separator`

## ReadOnlyAttribute
Web theme class applied to read-only attribute controls in generated objects
- Type: `string`
- Default: `ReadonlyAttribute`

## ReadOnlyGridAttribute
Web theme class applied to read-only attribute controls inside grids; template-dependent if omitted
- Type: `string`

## ReadOnlyBlobAttribute
Web theme class applied to read-only blob attribute controls
- Type: `string`
- Default: `ReadonlyAttribute`

## ReadOnlyVideoAttribute
Web theme class applied to read-only video attribute controls
- Type: `string`
- Default: `ReadonlyVideoAttribute`

## ReadOnlyAudioAttribute
Web theme class applied to read-only audio attribute controls
- Type: `string`
- Default: `ReadonlyAudioAttribute`

## ReadOnlyDownloadAttribute
Web theme class applied to read-only downloadable attribute controls
- Type: `string`
- Default: `ReadonlyDownloadAttribute`

## ReadOnlyGridBlobAttribute
Web theme class applied to read-only blob attribute controls inside grids; template-dependent if omitted
- Type: `string`

## OptionalColumn
Web theme class applied to optional grid columns; template-dependent if omitted
- Type: `string`

## GridAction
Web theme class applied to action controls rendered inside grids
- Type: `string`
- Default: ``

---

# LABELS node

## GeneralTab
Caption for the general tab in the detail view
- Type: `string`
- Default: `General`

## WorkWithTitle
Title format for the Work With selection screen; use `<Object>` as a placeholder for the transaction name
- Type: `string`
- Default: `Work With <Object>`

## PluralizeObjectName
When `true`, attempt automatic pluralization of the object name in titles
- Type: `boolean`
- Default: `False`

## ViewDescription
Format string for the View panel description; default `'<name> Information'`, where `<name>` is the Transaction name
- Type: `string`

## OrderedBy
Label for the ordered-by indicator shown in the selection grid
- Type: `string`
- Default: `Ordered By`

## AllInCombo
Caption for the 'All' option in filter combo boxes
- Type: `string`
- Default: `GX_AllItems`

## PreviousTab
Tooltip text for the previous tab navigation button
- Type: `string`
- Default: `Previous Tab`

## NextTab
Tooltip text for the next tab navigation button
- Type: `string`
- Default: `Next Tab`

## RecordNotFound
Text displayed in the View panel when the requested record does not exist
- Type: `string`
- Default: `Record not found`

---

# GRID node

## BackColorStyle
Row background coloring style applied to generated selection grids
- Type: `enum{None,Uniform,Header,Report}`
- Default: `Report`
- Options:
	* `None`: No alternating row colors
	* `Uniform`: Uniform row color throughout the grid
	* `Header`: Apply color to header rows only
	* `Report`: Alternating row colors in report style

## CellSpacing
Cell spacing in pixels for generated grids
- Type: `string`
- Default: `2`

## CellPadding
Cell padding in pixels for generated grids
- Type: `string`
- Default: `4`

## Page
Number of rows per page in generated grids; use `'0'` to disable paging
- Type: `string`
- Default: `10`

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

## SaveGridState
When `true`, preserve the grid page, active filters, and sort order between successive visits
- Type: `boolean`
- Default: `True`

## CustomRender
Name of the user control used to render the grid; omit to use the default grid renderer
- Type: `string`

---

# MASTERS node

## Selection
Master page object assigned to generated Selection webpanels
- Type: `string`
- Default: `AppMasterPage`

## Transaction
Master page object assigned to generated Transaction webforms
- Type: `string`
- Default: `AppMasterPage`

## View
Master page object assigned to generated View webpanels
- Type: `string`
- Default: `AppMasterPage`

---

# ACTION node

## Caption
Human-readable label for various UI elements
- Type: `string`

## Tooltip
Tooltip text shown when the mouse hovers over the action
- Type: `string`

## EnabledByDefault
Include this action in generated grids and forms by default
- Type: `boolean`
- Default: `True`

## Image
Image displayed on action button or when selected
- Type: `string`

## DisabledImage
Image resource shown when the action is disabled; omit if not applicable
- Type: `string`

## DisabledClass
Web theme class applied to the action when it is disabled; leave empty if not set
- Type: `string`
- Default: ``

## ButtonClass
Web theme class applied to the action button
- Type: `string`

## InGridClass
Web theme class applied to the action when rendered as a column inside the grid
- Type: `string`

## BaseLocation
Base file system path used for generated export files; omit to use the server default
- Type: `string`
- Applies when: `Node = Export`

## FileExtension
File extension for the generated export file
- Type: `enum{xls,xlsx,*}`
- Default: `xlsx`
- Options:
	* `xls`: Legacy Excel 97-2003 format
	* `xlsx`: Modern Excel Open XML format
	* `*`: Any extension; determined at runtime
- Applies when: `Node = Export`

## Template
Path to the Excel template file used as the base for the export; omit if not applicable
- Type: `string`

## StartRow
Row number (1-based) in the Excel template where data export begins
- Type: `string`
- Default: `1`
- Applies when: `Node = Export`

## StartColumn
Column number (1-based) in the Excel template where data export begins
- Type: `string`
- Default: `1`
- Applies when: `Node = Export`

## OnlyVisible
Export only visible attributes (`True`) or all attributes (`False`)
- Type: `boolean`
- Default: `True`
- Applies when: `Node = Export`

## EnumeratedDomains
How enumerated domain values are exported
- Type: `enum{Value,Description}`
- Default: `Description`
- Options:
	* `Value`: Export the underlying numeric or string value
	* `Description`: Export the human-readable description
- Applies when: `Node = Export`

---

# CONTEXT node

## Name
Unique name
- Type: `string`

## Type
GeneXus data type of the context variable (e.g., `Numeric`, `Character`, an SDT name)
- Type: `string`

## LoadProcedure
Name of the Procedure object used to load the context variable value from the current context; omit if not applicable
- Type: `string`

## UseInitialValue
When `true`, initialize the context variable with its defined initial value
- Type: `boolean`
- Default: `True`

---

# SECURITY node

## Enabled
When `true`, generate authorization check calls in each produced web panel
- Type: `boolean`
- Default: `True`

## Check
Name of the Procedure object invoked to verify whether the current user is authorized; omit to skip authorization checks
- Type: `string`

## NotAuthorized
Name of the WebPanel object displayed when the authorization check returns false
- Type: `string`

## Name
Unique name
- Type: `string`
