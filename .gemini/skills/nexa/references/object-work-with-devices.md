---
name: object-work-with-devices
description: Work With pattern for Mobile and Angular UIs based on a Transaction definition
---

Defines a `WorkWith` pattern instance that generates mobile `Panel` objects for list and detail navigation from a `Transaction` definition

---

# DEFINITION
A `WorkWith` object (or `WWD`) is a pattern that produces a hierarchy of `Panel` objects targeting native environments from a source `Transaction` object

---

# SYNTAX
~~~
WorkWith <name>
{
	<workwith>

	#Properties
		<properties>
	#End
}
~~~

Where:
- `<name>`: Auto-generated name as `WorkWith<transaction>`
- `<workwith>`: see [WORKWITH](#workwith) section
- `<properties>`: see [WorkWith](./properties-object-work-with-devices.md)

---

# WORKWITH
Root block binding the pattern to a source `Transaction` used as a Business Component

Syntax:
~~~
WorkWith [ BusinessComponent = '<transaction>' ]
{
	<levels>
}
~~~

Where:
- `<transaction>`: Name of the `Transaction` object; see [common-business-component](./common-business-component.md)
- `<levels>`: see [LEVEL](#level) section

---

# LEVEL
Maps one `Transaction` level; contains `List` and `Detail` definitions

Syntax:
~~~
Level
[
	<properties>
]
{
	<list>
	<detail>
}
…
~~~

Where:
- `<list>`: see [LIST](#list) section
- `<detail>`: see [DETAIL](#detail) section
- `<properties>`: see [LEVEL node](./properties-object-work-with-devices.md#level-node)

Rules:
- Root level contains both `List` and `Detail`; subordinate levels contain `Detail` only
- Use one or more `Level` blocks per `WorkWith` instance

---

# LIST
Drives generation of the browsing Panel for the root level

Syntax:
~~~
List
[
	<properties>
]
{
	<layout>
	<rowset>
}
~~~

Where:
- `<layout>`: see [LAYOUT](#layout) section
- `<rowset>`: see [GRIDDATA](#griddata) section
- `<properties>`: see [LIST node](./properties-object-work-with-devices.md#list-node)

---

# LAYOUT
Tree-based screen layout schema used in `List`, `Detail`, and `Section` nodes

Syntax:
~~~
Layout [ Type = '<type>' ]
{
	<control> [ <properties> ]
	{
		// child nodes; only valid when parent is a container control
		<control> [ <properties> ]
		{
			…
		}
		…
	}
	…
}
~~~

Where:
- `<type>`: Layout mode; one of:
	* `'View'`: Display-only layout; default for `List`
	* `'Edit'`: Update/insert layout
	* `'Any Mode'`: Single layout for any mode
- `<control>`: Any valid control name
- `<properties>`: Optional node properties with comma-separated `Key = 'Value'` syntax

Scopes:
- Define hierarchical structure and control composition per layout mode
- Define visual styling in `DesignSystem` object classes

Rules:
- See [GeneXus Layout](./frontend-layout.md) for available controls (elements) and properties (attributes)
- Define node and property names in PascalCase; e.g. `flexGrid` (✘) → `FlexGrid` (✓)

---

# GRIDDATA
Configures `Grid` data retrieval behavior

Syntax:
~~~
GridData [ Name = '<grid>', <properties> ]
{
	<orders>
	<breakby>
	<search>
}
~~~

Where:
- `<grid>`: Name of the grid control as defined in the `Layout` block
- `<orders>`: see [ORDERS](#orders) section
- `<breakby>`: Optional; see [BREAKBY](#breakby) section
- `<search>`: Optional; see [SEARCH](#search) section
- `<properties>`: see [GRIDDATA node](./properties-object-work-with-devices.md#griddata-node)

---

# ORDERS
Defines sort criteria available in the generated list selector

Syntax:
~~~
Orders
{
	<order>
	…
}
~~~

Where:
- `<order>`: see [ORDER](#order) section

---

# ORDER
Single sort entry inside `Orders`

Syntax:
~~~
Order [ <properties> ]
{
	Attribute
	[
		Attribute = '<attribute>',
		Description = '<description>',
		Ascending = '<ascending>'
	]
	…
}
~~~

Where:
- `<attribute>`: Attribute name from the source `Transaction`
- `<description>`: Human-readable column or sort label
- `<ascending>`: Sort direction; `True` for ascending, `False` for descending
- `<properties>`: see [ORDER node](./properties-object-work-with-devices.md#order-node)

---

# BREAKBY
Defines a global control-break grouping applied across all `Order` entries

Syntax:
~~~
BreakBy [ <properties> ]
{
	Attribute [ Attribute = '<attribute>' ]
	…
}
~~~

Where:
- `<attribute>`: Attribute that defines the control-break grouping boundary
- `<properties>`: see [BREAKBY node](./properties-object-work-with-devices.md#breakby-node)

Rules:
- Use when the same grouping must apply across all orders, not just a single one
- Distinct from the `BreakBy` property on an `Order` element, which enables grouping only for that specific order

---

# SEARCH
Full-text and structured search configuration for the `Grid`

Syntax:
~~~
Search [ <properties> ]
{
	Attribute
	[
		Attribute = '<attribute>',
		Description = '<description>'
	]
	…
}
~~~

Where:
- `<attribute>`: Attribute name searched by the search bar
- `<description>`: Human-readable label shown in the search field
- `<properties>`: see [SEARCH node](./properties-object-work-with-devices.md#search-node)

---

# DETAIL
Drives generation of the record-detail Panel per level

Syntax:
~~~
Detail
[
	<properties>
]
{
	<layout>
	<sections>
}
~~~

Where:
- `<layout>`: see [LAYOUT](#layout) section
- `<sections>`: see [SECTIONS](#sections) section
- `<properties>`: see [DETAIL node](./properties-object-work-with-devices.md#detail-node)

---

# SECTIONS
Container block inside `Detail` holding one or more `Section` entries

Syntax:
~~~
Sections
{
	<section>
	…
}
~~~

Where:
- `<section>`: see [SECTION](#section) section

---

# SECTION
Groups attributes into a logical display unit inside `Detail`

Syntax:
~~~
Section
[
	<properties>
]
{
	<layout>
}
~~~

Where:
- `<layout>`: see [LAYOUT](#layout) section
- `<properties>`: see [SECTION node](./properties-object-work-with-devices.md#section-node)

---

# NAVIGATION
Navigation call syntax from any `Panel` or `WorkWith` event:
~~~
WorkWith<name>.<level>.List([<param>, …])
WorkWith<name>.<level>.Detail([<param>, …])
WorkWith<name>.<level>.Detail.Insert([&<bc-var>])
WorkWith<name>.<level>.Detail.Update(<pk-params>)
WorkWith<name>.<level>.Detail.Delete(<pk-params>)
~~~

Where:
- `<name>`: Name of the `WorkWith` object; same as the source `Transaction`
- `<level>`: Level name inside the object
- `<bc-var>`: Business Component variable matching the transaction structure
- `<pk-params>`: Primary key attribute values

---

# EVENTS
See [common-events](./common-events.md)

---

# RULES
See [common-rules](./common-rules.md)

---

# OUTPUT
See [global-output](./global-output.md)

Workflow:
- Create or update the `WorkWith` pattern instance artifact
- Import into the Knowledge Base; stop on failure
- Run `Build` to regenerate screens from the updated instance

---

# CONVENTIONS
- Name `Section` elements semantically: `General`, `Address`, `Orders`, `Photos`
- Keep `List` layout minimal: show only key identifying attributes
- Reserve `Detail > Sections > Section [ Name = 'General' ]` for primary attributes
- Use additional sections for related data
- Define `Orders` inside `GridData` when multiple sort options are needed
- Define `Search` attributes inside `GridData` for full-text search support
- Define `AdvancedSearch` filters inside `GridData` for structured search by domain-relevant fields
- Use `ApplicationBar` for primary actions: `Insert` on `List`; `Save` and `Delete` on `Detail`
- Define `Display` on `Detail` as `Inline`, `Link`, `Tabs`, or `Platform Default` when using multiple sections
- Keep event logic minimal in `List`, `Detail`, and `Section` blocks
- Define business logic in `Procedure` objects, not in event handlers

---

# CONSTRAINTS
- See [global-constraints](./global-constraints.md)
- Apply the pattern only to an existing `Transaction`; no standalone creation
- Keep one `List` element per root `Level`; subordinate `Level` elements have `Detail` only
- Define `Variables`, `Events`, and `Rules` as element properties in `[…]` on `List`, `Detail`, and `Section` blocks
- Never place `#Events`, `#Rules`, or `#Variables` named sections inside a block body `{ }`
- Ensure the `Insert()` call has a Business Component variable matching the transaction structure
- Define `ApplicationBar` inside the `Layout` block of each node
- Forbid more than one `ApplicationBar` per layout node

---

# EXAMPLES

## Example 1
Single-level `Customer` transaction: searchable sortable list and a detail screen with two sections; list has Insert in its ApplicationBar; detail has Save and Delete; `Search` uses `FilterOperator = 'Contains'` for substring match
~~~
WorkWith WorkWithCustomer
{
	WorkWith
	{
		Level
		[
			Name = 'Customer',
			Description = 'Customers'
		]
		{
			List
			[
				Caption = 'Customers',
				// Lapse = '0' disables local cache; the grid always fetches fresh data from the server
				Lapse = '0'
			]
			{
				// Layout declares the visual structure of the generated Panel
				// ApplicationBar holds primary actions; here Insert creates a new Customer record
				Layout [ Type = 'View' ]
				{
					ApplicationBar
					{
						// Insert navigates to Detail in insert mode using a Business Component variable
						Action [ Name = 'Insert', Caption = 'New Customer' ]
					}

					// The grid control named 'CustomerGrid' is bound to the GridData block below
					Grid [ Name = 'CustomerGrid' ]
					{
						// Attribute controls inside the grid map to columns shown in the list row
						Attribute [ Name = 'CustomerName' ]
						Attribute [ Name = 'CustomerEmail' ]
						Attribute [ Name = 'CustomerPhone' ]
					}
				}

				GridData [ Name = 'CustomerGrid' ]
				{
					Orders
					{
						// Two sort orders enable a sort selector in the list toolbar
						Order [ Name = 'Name A→Z' ]
						{
							Attribute [ Attribute = 'CustomerName', Ascending = 'True' ]
						}
						Order [ Name = 'Name Z→A' ]
						{
							Attribute [ Attribute = 'CustomerName', Ascending = 'False' ]
						}
					}

					Search
					[
						// FilterOperator = 'Contains' performs a substring search (like %term%)
						// AlwaysVisible = 'True' keeps the search bar expanded without requiring a tap
						FilterOperator = 'Contains',
						AlwaysVisible = 'True',
						CaseSensitive = 'False'
					]
					{
						// Attributes listed here are the fields searched by the search bar
						Attribute [ Attribute = 'CustomerName', Description = 'Name' ]
						Attribute [ Attribute = 'CustomerEmail', Description = 'Email' ]
					}
				}
			}

			Detail
			[
				Caption = 'Customer',
				// Rules passes the PK to the detail Panel; required for Update and Delete flows
				Rules = 'parm(CustomerId);'
			]
			{
				Layout [ Type = 'Any Mode' ]
				{
					ApplicationBar
					{
							// Save commits the BC; Delete removes the record. Both are standard actions
							Action [ Name = 'Save', Caption = 'Save' ]
							Action [ Name = 'Delete', Caption = 'Delete' ]
					}
				}

				Sections
				{
					// 'General' section holds primary identifying attributes
					Section
					[
						Name = 'General',
						Caption = 'General Info'
					]
					{
						Layout [ Type = 'Any Mode' ]
						{
							Attribute [ Name = 'CustomerName' ]
							Attribute [ Name = 'CustomerEmail' ]
							Attribute [ Name = 'CustomerPhone' ]
						}
					}

					// 'Address' section groups location fields separately for clarity
					Section
					[
						Name = 'Address',
						Caption = 'Address'
					]
					{
						Layout [ Type = 'Any Mode' ]
						{
							Attribute [ Name = 'CustomerAddress' ]
							Attribute [ Name = 'CustomerCity' ]
							Attribute [ Name = 'CustomerCountry' ]
						}
					}
				}
			}
		}
	}

	#Properties
	#End
}
~~~

Calling `WorkWithCustomer` from any other Panel:
~~~
// Navigate to the Customer list
Event 'OpenCustomerList'
	WorkWithCustomer.Customer.List()
EndEvent

// Navigate directly to the Customer detail in display mode
Event 'OpenCustomerDetail'
	WorkWithCustomer.Customer.Detail.Update(CustomerId)
EndEvent

// Navigate to the Customer detail in insert mode using a Business Component variable
Event 'AddNewCustomer'
	&CustomerBC = new()
	WorkWithCustomer.Customer.Detail.Insert(&CustomerBC)
EndEvent
~~~

---

## Example 2
Header–detail `Order / OrderLine` transaction: root level with both `List` and `Detail`; subordinate level with `Detail` only navigated from an action inside Order Detail; demonstrates multi-level `Level` blocks, `BreakBy` on an `Order` element to group list rows by date, `EnableAlphaIndexer` for alphabetical navigation, and a custom non-standard action calling a `Procedure`
~~~
WorkWith WorkWithOrder
{
	WorkWith
	{
		// Root level — has both List and Detail
		Level
		[
			Name = 'Order',
			Description = 'Orders'
		]
		{
			List
			[
				Caption = 'Orders',
				Lapse = '0'
			]
			{
				Layout [ Type = 'View' ]
				{
					ApplicationBar
					{
						Action [ Name = 'Insert', Caption = 'New Order' ]
					}

					Grid [ Name = 'OrderGrid' ]
					{
						Attribute [ Name = 'OrderDate' ]
						Attribute [ Name = 'CustomerName' ]
						Attribute [ Name = 'OrderStatusName' ]
						Attribute [ Name = 'OrderTotal' ]
					}
				}

				GridData [ Name = 'OrderGrid' ]
				{
					Orders
					{
						// BreakBy groups list rows by month, inserting section headers
						// EnableAlphaIndexer adds an A-Z side index for fast scrolling
						Order
						[
							Name = 'Date (newest first)',
							BreakBy = 'OrderMonth',
							EnableAlphaIndexer = 'False'
						]
						{
							Attribute [ Attribute = 'OrderDate', Ascending = 'False' ]
						}
						Order
						[
							Name = 'Customer A→Z',
							EnableAlphaIndexer = 'True',
							DescriptionAttribute = 'CustomerName'
						]
						{
							Attribute [ Attribute = 'CustomerName', Ascending = 'True' ]
						}
					}

					Search
					[
						FilterOperator = 'Begins with',
						AlwaysVisible = 'False', // search bar is collapsed; user taps to expand
						CaseSensitive = 'False'
					]
					{
						Attribute [ Attribute = 'CustomerName', Description = 'Customer' ]
						Attribute [ Attribute = 'OrderDate', Description = 'Date' ]
					}
				}
			}

			Detail
			[
				Caption = 'Order',
				Rules = 'parm(OrderId);'
			]
			{
				Layout [ Type = 'Any Mode' ]
				{
					ApplicationBar
					{
						Action [ Name = 'Save', Caption = 'Save' ]
						Action [ Name = 'Delete', Caption = 'Delete' ]
						// Non-standard action: calls the ConfirmOrder procedure directly from the toolbar
						Action [ Name = 'Confirm', Caption = 'Confirm' ]
					}
				}

				Sections
				{
					Section
					[
						Name = 'General',
						Caption = 'Order Info'
					]
					{
						Layout [ Type = 'Any Mode' ]
						{
							Attribute [ Name = 'OrderDate' ]
							Attribute [ Name = 'CustomerId' ]
							Attribute [ Name = 'OrderStatusId' ]
							Attribute [ Name = 'OrderNotes' ]
						}
					}

					// 'Lines' section navigates to the OrderLine sub-level list
					// It is rendered as a link or embedded list depending on Display mode
					Section
					[
						Name = 'Lines',
						Caption = 'Line Items'
					]
					{
						Layout [ Type = 'View' ]
						{
							Grid [ Name = 'OrderLineGrid' ]
							{
								Attribute [ Name = 'OrderLineDescription' ]
								Attribute [ Name = 'OrderLineQty'	]
								Attribute [ Name = 'OrderLineUnitPrice' ]
								Attribute [ Name = 'OrderLineTotal' ]
							}
						}
					}
				}
			}
		}

		// Subordinate level — Detail only; navigated from the Lines section above
		// Subordinate levels do NOT have a List block
		Level
		[
			Name = 'OrderLine',
			Description = 'Line Items'
		]
		{
			Detail
			[
				Caption = 'Line Item',
				Rules = 'parm(OrderId, OrderLineId);'
			]
			{
				Layout [ Type = 'Any Mode' ]
				{
					ApplicationBar
					{
						Action [ Name = 'Save', Caption = 'Save' ]
						Action [ Name = 'Delete', Caption = 'Delete' ]
					}
				}

				Sections
				{
					Section
					[
						Name = 'General',
						Caption = 'Line Detail'
					]
					{
						Layout [ Type = 'Any Mode' ]
						{
							Attribute [ Name = 'OrderLineDescription' ]
							Attribute [ Name = 'OrderLineQty' ]
							Attribute [ Name = 'OrderLineUnitPrice' ]
							Attribute [ Name = 'OrderLineTotal' ]
						}
					}
				}
			}
		}
	}

	#Properties
	#End
}
~~~

Calling `WorkWithOrder` from any other Panel:
~~~
// Navigate to the Orders list
Event 'OpenOrders'
	WorkWithOrder.Order.List()
EndEvent

// Navigate to the Order detail in display/edit mode
Event 'OpenOrderDetail'
	WorkWithOrder.Order.Detail.Update(OrderId)
EndEvent

// Navigate to an OrderLine detail inside an Order
Event 'OpenOrderLineDetail'
	WorkWithOrder.OrderLine.Detail.Update(OrderId, OrderLineId)
EndEvent

// Navigate to the Order detail in insert mode
Event 'CreateOrder'
	&OrderBC = new()
	WorkWithOrder.Order.Detail.Insert(&OrderBC)
EndEvent
~~~
