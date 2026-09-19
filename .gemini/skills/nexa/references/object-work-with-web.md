---
name: object-work-with-web
description: Work With pattern instance for web UIs generated from a Transaction definition
---

Defines a `WorkWith` pattern instance that generates `WebPanel` and `WebComponent` objects for list and detail navigation from a `Transaction` definition

---

# DEFINITION
A `WorkWithForWeb` object (or `WWWeb`) is a pattern that produces a `Selection` web panel, a `View` web panel, and one `WebComponent` per `Tabular` tab from a source `Transaction` object

Generated object names per level:
- `WW<transaction>`: Selection web panel
- `View<transaction>`: View web panel
- `<component>`: Generated `WebComponent` per `Tabular` tab

---

# SYNTAX
~~~
WorkWithForWeb <name>
{
	<instance>

	#Properties
		<properties>
	#End
}
~~~

Where:
- `<name>`: Instance identifier; by convention `WorkWithWeb<transaction>`
- `<instance>`: See [INSTANCE](#instance) section
- `<properties>`: See [WorkWithWeb](./properties-object-work-with-web.md)

---

# INSTANCE
Declares pattern-level configuration applied to all generated objects

Syntax:
~~~
WorkWithPatternInstance
[
	<properties>
]
{
	<transaction>
	<levels>
}
~~~

Where:
- `<transaction>`: See [TRANSACTION](#transaction) section
- `<levels>`: See [LEVEL](#level) section
- `<properties>`: See [INSTANCE node](./properties-object-work-with-web.md#instance-node)

---

# TRANSACTION
Declares the source `Transaction` for this pattern instance

Syntax:
~~~
Transaction
[
	Transaction = '<transaction>',
	<properties>
]
~~~

Where:
- `<transaction>`: Source `Transaction` name
- `<properties>`: See [TRANSACTION node](./properties-object-work-with-web.md#transaction-node)

---

# LEVEL
Maps one or more `Transaction Level`; contains `Selection` and `View` definitions

Syntax:
~~~
Level
[
	<properties>
]
{
	<descriptor>
	<selection>
	<view>
}
…
~~~

Where:
- `<descriptor>`: See [DESCRIPTOR](#descriptor) section
- `<selection>`: See [SELECTION](#selection) section
- `<view>`: See [VIEW](#view) section
- `<properties>`: See [LEVEL node](./properties-object-work-with-web.md#level-node)

---

# DESCRIPTOR
Main descriptive attribute displayed in `Selection` and `View` definitions

Syntax:
~~~
DescriptionAttribute [ <properties> ]
~~~

Where:
- `<properties>`: See [DESCRIPTOR node](./properties-object-work-with-web.md#descriptor-node)

---

# SELECTION
Configures the list screen; maps to generated `WW<transaction>` object

Syntax:
~~~
Selection
[
	<properties>
]
{
	<modes>
	<attributes>
	<orders>
	<filter>
}
~~~

Where:
- `<modes>`: See [MODES](#modes) section
- `<attributes>`: See [ATTRIBUTES](#attributes) section
- `<orders>`: See [ORDERS](#orders) section
- `<filter>`: See [FILTER](#filter) section
- `<properties>`: See [SELECTION node](./properties-object-work-with-web.md#selection-node)

---

# MODES
CRUD mode flags and conditions for the `Selection` grid

Syntax:
~~~
Modes [ <properties> ]
~~~

Where:
- `<properties>`: See [MODES node](./properties-object-work-with-web.md#modes-node)

---

# VIEW
Configures the detail screen; maps to `View<transaction>` object

Syntax:
~~~
View
[
	<properties>
]
{
	<parameters>
	<fixed-data>
	<tabs>
}
~~~

Where:
- `<parameters>`: See [PARAMETERS](#parameters) section
- `<fixed-data>`: See [FIXEDDATA](#fixeddata) section
- `<tabs>`: See [TABS](#tabs) section
- `<properties>`: See [VIEW node](./properties-object-work-with-web.md#view-node)

---

# PARAMETERS
PK attributes received by the `View` screen

Syntax:
~~~
Parameters
{
	Parameter
	[
		Name = '<attribute>',
		<properties>
	]
	…
}
~~~

Where:
- `<attribute>`: PK attribute name received by the `View` section
- `<properties>`: See [PARAMETER node](./properties-object-work-with-web.md#parameter-node)

---

# FIXEDDATA
Attributes always visible in the `View` outside tabs

Syntax:
~~~
FixedData
{
	<attributes>
}
~~~

Where:
- `<attributes>`: See [ATTRIBUTES](#attributes) section

---

# ATTRIBUTES
Reusable column list for `Selection`, `FixedData`, and `Tab` nodes

Syntax:
~~~
Attributes
{
	Attribute [ <properties> ]
	…
}
~~~

Where:
- `<properties>`: See [ATTRIBUTE node](./properties-object-work-with-web.md#attribute-node)

---

# ORDERS
Available sort criteria for the `Grid` selection

Syntax:
~~~
Orders
{
	Order [ Name = '<order>' ]
	{
		Attribute
		[
			<properties>
		]
		…
	}
	…
}
~~~

Where:
- `<order>`: Name for the `Order` element; drives the orders combo in `WW<transaction>` object
- `<properties>`: See [ORDER node](./properties-object-work-with-web.md#order-node)

---

# FILTER
Structured search configuration for the `Grid` selection

Syntax:
~~~
Filter
{
	<attributes>
	<conditions>
}
~~~

Where:
- `<attributes>`: See [FILTERATTRIBUTES](#filterattributes) section
- `<conditions>`: See [CONDITIONS](#conditions) section

---

# FILTERATTRIBUTES
Declares filter input variables inside the `Filter` block

Syntax:
~~~
Attributes
{
	FilterAttribute
	[
		<properties>
	]
	…
}
~~~

Where:
- `<properties>`: See [FILTER node](./properties-object-work-with-web.md#filter-node)

---

# CONDITIONS
Sequence of conditions for the `Filter` node combined with an implicit `AND` operator

Syntax:
~~~
Conditions
{
	Condition [ <properties> ]
	…
}
~~~

Where:
- `<properties>`: See [CONDITION node](./properties-object-work-with-web.md#condition-node)

Rules:
- Must only reference attributes declared in the same `Filter > Attributes` block

---

# TABS
Container block inside `View` holding one or more `Tab` entries

Syntax:
~~~
Tabs
{
	<tab>
	…
}
~~~

Where:
- `<tab>`: See [TAB](#tab) section

---

# TAB
Single tab inside `Tabs`; generates a `WebComponent` object when `Type = 'Tabular'`

Syntax:
~~~
Tab
[
	<properties>
]
{
	<attributes>
	<actions>
}
~~~

Where:
- `<attributes>`: See [ATTRIBUTES](#attributes) section
- `<actions>`: See [ACTIONS](#actions) section
- `<properties>`: See [TAB node](./properties-object-work-with-web.md#tab-node)

Tab type behavior:
- `Tabular`: Renders scalar attributes; generates a dedicated `WebComponent` named by `ComponentName`
- `Grid`: Renders a related sub-level collection; requires a `Transaction` property on the tab
- `UserDefined`: Delegates layout to an existing `WebComponent` object; requires a `WebComponent` with the same parameter signature as the `View`

---

# ACTIONS
Actions exposed in a `Tab` control

Syntax:
~~~
Actions
{
	Action [ <properties> ]
	…
}
~~~

Where:
- `<properties>`: See [ACTION node](./properties-object-work-with-web.md#action-node)

---

# OUTPUT
See [global-output](./global-output.md)

Workflow:
- Create or update the `WorkWithForWeb` pattern instance artifact
- Import into the Knowledge Base; stop on failure
- Verify generated objects are present in `src/<transaction>/`; expected:
	* `WW<transaction>.gx`
	* `View<transaction>.gx`
	* One `.gx` per `Tabular` tab
- Run `Build` to regenerate web panels and components from the updated instance
- Confirm `WW<transaction>` and `View<transaction>` objects reflect the changes

---

# CONVENTIONS
- Name the instance `WorkWithWeb<transaction>` to match GeneXus convention
- Define `Selection > Description` as a human-readable screen title
- Define `DescriptionAttribute` as the main descriptive attribute of the level
- Keep `Selection > Attributes` to identifying and summary columns only
- Define `Autolink = 'True'` on description attributes to enable automatic navigation to the `View`
- Define at least one `Order` per `Level` for user-friendly sorting
- Use `Filter > Attributes` to declare structured search variables
- Define `FilterAttribute` entries before referencing them in `Filter > Conditions`
- Define `View > FixedData` as the description attribute so the record title is always visible outside tabs
- Use `Tab > Type = Tabular` for scalar attributes
- Keep `Code` and `ComponentName` unique per tab
- Use `Tab > Type = Grid` for related sub-level collections
- Define `View > BackToSelection = 'True'` to show a back link to `WW<transaction>`

---

# CONSTRAINTS
- See [global-constraints](./global-constraints.md)
- Apply the pattern only to an existing `Transaction`
- Never manually edit auto-generated `WW*`, `View*`, or tab component objects
- Apply all changes to the pattern instance artifact
- Keep `Tab > ComponentName` unique within the instance
- Use `Filter > Conditions` only with attributes declared in the same `Filter > Attributes` block
- Define more than one `Order` to enable the orders combo in `WW<transaction>`
- Ensure `Tab > Type = UserDefined` has a matching `WebComponent` with the same parameter signature as the `View`

---

# EXAMPLES

## Example 1
Single-level `Country` transaction: list screen with one sort order, a text filter, a single `Tabular` tab with Update/Delete actions, and a `View` showing the country name as a fixed header above the tabs; `Modes` restricts Insert to administrators via a condition expression
~~~
WorkWithForWeb WorkWithWebCountry
{
	WorkWithPatternInstance
	[
		// UpdateTransaction controls whether applying the pattern also resynchronizes
		// the source Transaction layout. 'Apply WW Style' is the standard value
		UpdateTransaction = 'Apply WW Style'
	]
	{
		Transaction
		[
			Transaction = 'Country',
			// GenerateNoPromptRule = 'True' suppresses the default confirmation prompt
			// that GeneXus generates before INSERT/UPDATE/DELETE on the Transaction
			GenerateNoPromptRule = 'False'
		]

		// Level maps one Transaction level. Id is autogenerated by GeneXus and must
		// NOT be changed manually; Name must match the Transaction level name exactly
		Level
		[
			Name = 'Country'
		]
		{
			// DescriptionAttribute is used as the record title in the View header and
			// as the default Autolink column in Selection grids
			DescriptionAttribute
			[
				Attribute = 'CountryName',
				Description = 'Name'
			]

			Selection
			[
				Description = 'Countries', // title shown in WWCountry web panel
				RowsPerPage = '<default>', // inherits the pattern-level default (typically 10)
				PagingMode = 'Paging', // standard page-by-page navigation
				ShowCurrentPage = 'True' // shows "Page X of Y" indicator in the grid footer
			]
			{
				// Modes controls which CRUD operations are available in the list
				// Omit a mode to hide that action from all users
				// Use *Condition to apply a conditional expression evaluated at runtime
				Modes
				[
					Insert = 'Yes',
					// InsertCondition restricts Insert to users with role 'Admin'
					InsertCondition = '&GXUserRole = ''Admin''',
					Update = 'Yes',
					Delete = 'Yes',
					Display = 'Yes',
					Export = 'No'
				]

				Attributes
				{
					// CountryId is shown but not the primary navigation target;
					// CountryName carries Autolink = 'True' to enable click-through to the View
					Attribute
					[
						Attribute = 'CountryId',
						Description = 'ID',
						Autolink = 'False',
						Visible = 'True'
					]
					Attribute
					[
						Attribute = 'CountryName',
						Description = 'Country',
						Autolink = 'True', // clicking navigates to ViewCountry
						Visible = 'True'
					]
				}

				Orders
				{
					// Providing more than one Order enables the sort combo in the list screen
					Order
					[
						Name = 'Name A→Z'
					]
					{
						Attribute
						[
							Attribute = 'CountryName',
							Description = 'Country Name',
							Ascending = 'True'
						]
					}
					Order
					[
						Name = 'Name Z→A'
					]
					{
						Attribute
						[
							Attribute = 'CountryName',
							Description = 'Country Name',
							Ascending = 'False'
						]
					}
				}

				Filter
				{
					// Declare filter variables here; reference them in Conditions below
					// The generated &CountryName variable is a free-text search box in the list toolbar
					Attributes
					{
						FilterAttribute
						[
							Name = 'CountryName',
							Description = 'Search by name',
							Default = ''
						]
					}

					Conditions
					{
						// Case-insensitive partial match; condition is skipped when the filter is empty
						Condition
						[
							Value = 'CountryName.ToLower() LIKE &CountryName.ToLower() WHEN NOT &CountryName.IsEmpty()'
						]
					}
				}
			}

			View
			[
				// Caption is a GeneXus expression evaluated per record; used as the page/browser title
				Caption = 'CountryName.ToString()',
				Description = 'Country Information',
				BackToSelection = 'True', // renders a "Back to Countries" link at the top
				MasterPage = '<default>' // inherits the KB-level MasterPage
			]
			{
				Parameters
				{
					// The View receives the PK of the record being displayed
					// NullValue = 'True' allows navigating to an empty View for insert flows
					Parameter
					[
						Name = 'CountryId',
						NullValue = 'True'
					]
				}

				// FixedData attributes are always visible above the tab strip regardless of
				// which tab is selected. Use it for the primary descriptive attribute
				FixedData
				{
					Attributes
					{
						Attribute
						[
							Attribute = 'CountryName',
							Description = 'Name',
							Autolink = 'False',
							Visible = 'True'
						]
					}
				}

				Tabs
				{
					// Type = 'Tabular' generates a dedicated WebComponent named by ComponentName
					// Code is the URL fragment used for deep-linking to this tab
					Tab
					[
						Caption = 'General',
						Code = 'General',
						Description = 'Main country attributes',
						Type = 'Tabular',
						ComponentName = 'CountryGeneral'
					]
					{
						Attributes
						{
							Attribute
							[
								Attribute = 'CountryName',
								Description = 'Country Name',
								Autolink = 'False',
								Visible = 'True'
							]
						}

						Actions
						{
							// Standard actions map to the Transaction CRUD operations
							// Caption overrides the default button label; Tooltip adds hover text
							Action
							[
								Name = 'Update',
								Caption = 'Edit',
								Tooltip = 'Edit this country'
							]
							Action
							[
								Name = 'Delete',
								Caption = 'Delete',
								Tooltip = 'Remove this country'
							]
						}
					}
				}
			}
		}
	}

	#Properties
		IsGeneratedObject = "False"
	#End
}
~~~

Calling the generated screens from any `WebPanel` event:
~~~
// Opens the Countries list (WWCountry web panel)
Event 'OpenCountries'
	WWCountry()
EndEvent

// Opens the detail View for a specific country; second parameter is the null sentinel
Event 'ViewCountryDetail'
	ViewCountry(CountryId, '')
EndEvent
~~~

---

## Example 2
Header–detail `Invoice / InvoiceLine` transaction: root level with both `Selection` and `View`; subordinate level with `View` only shown as a `Grid` tab inside the Invoice View; demonstrates multi-level `Level` blocks, `Tab Type = 'Grid'`, a custom non-standard action with `Gxobject`, and `Export = 'Yes'` on the root Selection
~~~
WorkWithForWeb WorkWithWebInvoice
{
	WorkWithPatternInstance
	[
		UpdateTransaction = 'Apply WW Style'
	]
	{
		Transaction
		[
			Transaction = 'Invoice',
			GenerateNoPromptRule = 'False'
		]

		// Root level — has both Selection (list screen) and View (detail screen)
		Level
		[
			Name = 'Invoice'
		]
		{
			DescriptionAttribute
			[
				Attribute = 'InvoiceDate',
				Description = 'Date'
			]

			Selection
			[
				Description = 'Invoices',
				RowsPerPage = '20',
				PagingMode = 'Paging',
				ShowCurrentPage = 'True'
			]
			{
				Modes
				[
					Insert = 'Yes',
					Update = 'Yes',
					Delete = 'Yes',
					Display = 'Yes',
					Export = 'Yes' // enables the Export button; generates an Excel/CSV export action
				]

				Attributes
				{
					Attribute
					[
						Attribute = 'InvoiceId',
						Description = 'Invoice #',
						Autolink = 'True',
						Visible = 'True'
					]
					Attribute
					[
						Attribute = 'InvoiceDate',
						Description = 'Date',
						Autolink = 'False',
						Visible = 'True'
					]
					Attribute
					[
						Attribute = 'CustomerName',
						Description = 'Customer',
						Autolink = 'False',
						Visible = 'True'
					]
					Attribute
					[
						Attribute = 'InvoiceTotal',
						Description = 'Total',
						Autolink = 'False',
						Visible = 'True'
					]
				}

				Orders
				{
					Order [ Name = 'Date (newest first)' ]
					{
						Attribute [ Attribute = 'InvoiceDate', Ascending = 'False' ]
					}
					Order [ Name = 'Customer A→Z' ]
					{
						Attribute [ Attribute = 'CustomerName', Ascending = 'True' ]
					}
				}

				Filter
				{
					Attributes
					{
						FilterAttribute [ Name = 'CustomerName', Description = 'Customer', Default = '' ]
						FilterAttribute [ Name = 'InvoiceDate', Description = 'From date', Default = '' ]
					}
					Conditions
					{
						Condition [ Value = 'CustomerName like &CustomerName when not &CustomerName.IsEmpty()' ]
						Condition [ Value = 'InvoiceDate >= &InvoiceDate when not &InvoiceDate.IsEmpty()' ]
					}
				}
			}

			View
			[
				Caption = '"Invoice #" + InvoiceId.ToString()',
				Description = 'Invoice Detail',
				BackToSelection = 'True',
				MasterPage = '<default>'
			]
			{
				Parameters
				{
					Parameter [ Name = 'InvoiceId', NullValue = 'False' ]
				}

				FixedData
				{
					Attributes
					{
						Attribute [ Attribute = 'InvoiceDate', Description = 'Date', Visible = 'True' ]
						Attribute [ Attribute = 'CustomerName', Description = 'Customer', Visible = 'True' ]
						Attribute [ Attribute = 'InvoiceTotal', Description = 'Total', Visible = 'True' ]
					}
				}

				Tabs
				{
					// Tabular tab — scalar header attributes for editing
					Tab
					[
						Caption = 'Header',
						Code = 'Header',
						Description = 'Invoice header fields',
						Type = 'Tabular',
						ComponentName = 'InvoiceHeader'
					]
					{
						Attributes
						{
							Attribute [ Attribute = 'InvoiceDate', Description = 'Date', Visible = 'True' ]
							Attribute [ Attribute = 'CustomerId', Description = 'Customer', Visible = 'True' ]
							Attribute [ Attribute = 'InvoiceNotes', Description = 'Notes', Visible = 'True' ]
						}
						Actions
						{
							Action [ Name = 'Update', Caption = 'Edit', Tooltip = 'Edit this invoice' ]
							Action [ Name = 'Delete', Caption = 'Delete', Tooltip = 'Delete this invoice' ]
							// Non-standard action: Print invokes the PrintInvoice procedure
							// CallType = 'Link' opens it in-page rather than a full navigation
							Action
							[
								Name = 'Print',
								Caption = 'Print',
								Tooltip = 'Generate PDF',
								Gxobject = 'PrintInvoice',
								CallType = 'Link'
							]
						}
					}

					// Grid tab — renders the InvoiceLine sub-level as an embedded grid
					// The tab's Transaction-level data comes from the subordinate Level block below
					Tab
					[
						Caption = 'Lines',
						Code = 'Lines',
						Description = 'Invoice line items',
						Type = 'Grid',
						ComponentName = 'InvoiceLines'
					]
					{
						Attributes
						{
							Attribute [ Attribute = 'InvoiceLineId',          Description = '#',        Visible = 'True' ]
							Attribute [ Attribute = 'InvoiceLineDescription', Description = 'Item',     Visible = 'True' ]
							Attribute [ Attribute = 'InvoiceLineQty',         Description = 'Qty',      Visible = 'True' ]
							Attribute [ Attribute = 'InvoiceLineUnitPrice',   Description = 'Price',    Visible = 'True' ]
							Attribute [ Attribute = 'InvoiceLineTotal',       Description = 'Subtotal', Visible = 'True' ]
						}
						Actions
						{
							Action [ Name = 'Insert', Caption = 'Add line' ]
							Action [ Name = 'Update', Caption = 'Edit' ]
							Action [ Name = 'Delete', Caption = 'Remove' ]
						}
					}
				}
			}
		}

		// Subordinate level — only has View; List is generated by the Grid tab above
		Level
		[
			Name = 'InvoiceLine'
		]
		{
			DescriptionAttribute
			[
				Attribute = 'InvoiceLineDescription',
				Description = 'Description'
			]

			View
			[
				Caption = 'InvoiceLineDescription.ToString()',
				Description = 'Line Item Detail',
				BackToSelection = 'True',
				MasterPage = '<default>'
			]
			{
				Parameters
				{
					Parameter [ Name = 'InvoiceId', NullValue = 'False' ]
					Parameter [ Name = 'InvoiceLineId', NullValue = 'False' ]
				}

				FixedData
				{
					Attributes
					{
						Attribute [ Attribute = 'InvoiceLineDescription', Description = 'Item', Visible = 'True' ]
					}
				}

				Tabs
				{
					Tab
					[
						Caption = 'Line Detail',
						Code = 'LineDetail',
						Description = 'Line item attributes',
						Type = 'Tabular',
						ComponentName = 'InvoiceLineDetail'
					]
					{
						Attributes
						{
							Attribute [ Attribute = 'InvoiceLineDescription', Description = 'Description', Visible = 'True' ]
							Attribute [ Attribute = 'InvoiceLineQty',         Description = 'Quantity',    Visible = 'True' ]
							Attribute [ Attribute = 'InvoiceLineUnitPrice',   Description = 'Unit Price',  Visible = 'True' ]
							Attribute [ Attribute = 'InvoiceLineTotal',       Description = 'Subtotal',    Visible = 'True' ]
						}
						Actions
						{
							Action [ Name = 'Update', Caption = 'Edit line' ]
							Action [ Name = 'Delete', Caption = 'Remove line' ]
						}
					}
				}
			}
		}
	}

	#Properties
		IsGeneratedObject = "False"
	#End
}
~~~

Calling the generated Invoice screens from any `WebPanel` event:
~~~
// Opens the Invoices list
Event 'GoToInvoices'
	WWInvoice()
EndEvent

// Opens the Invoice detail View for a specific record
Event 'GoToInvoiceDetail'
	ViewInvoice(InvoiceId, '')
EndEvent

// Opens the InvoiceLine detail for a specific line (both PK values required)
Event 'GoToLineDetail'
	ViewInvoiceLine(InvoiceId, InvoiceLineId, '')
EndEvent
~~~
