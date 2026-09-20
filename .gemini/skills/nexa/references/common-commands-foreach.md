---
name: common-commands-foreach
description: Complete For Each command syntax for declarative data processing
---

Declarative command for record selection and manipulation, abstracting database queries

---

# DEFINITION
The `For Each` command (or `FE` for short) processes stored data from `Transaction` objects declaratively, focusing on WHAT data to process rather than HOW to retrieve it

---

# SYNTAX
~~~
For each [<transaction-1>[, … [, <transaction-N>]]]
	[Skip <skip>]
	[Count <count>]
	[Order <attribute-1>, …, <attribute-N> [when <constraint>]]
	[Order none [when <constraint>]]
	[Using <data-selector>]
	[Unique <attribute-1>, …, <attribute-N>]
	[Where <condition> [when <constraint>]]
	[Blocking <size>]
	<code-main>
[When duplicate
	<code-dupl>]
[When none
	<code-none>]
EndFor
~~~

Where:
- `<transaction-i>`: Base transaction; use `.<level-j>` only for sublevel targeting
- `<size>`: Blocking batch size
- `<skip>`: Skipped record count
- `<count>`: Retrieved record count
- `<condition>`: Filter condition only
- `<constraint>`: Clause activation condition
- `<attribute-i>`: Clause attribute
- `<data-selector>`: Reusable filter/order selector
- `<code-main>`: Main per-record block
- `<code-dupl>`: Duplicate key handler block
- `<code-none>`: No-records handler block

---

# CLAUSES

## Base Transactions
Explicitly defines the base Transaction objects used for data retrieval

Syntax: `[<transaction-1>[, … [, <transaction-N>]]]`

Behavior:
- Optional; GeneXus infers from attributes used in clauses and code
- Use explicit declaration when:
	* Ambiguity exists
	* Targeting specific transaction sublevel: `<transaction>.<level>`

Examples:

Implicit inference:
~~~
For each
	Where CustomerName LIKE '%Smith'
	… // GeneXus infers Customer transaction
EndFor
~~~

Explicit declaration:
~~~
For each Customer
	Where CustomerAge > 30
	…
EndFor
~~~

Sublevel:
~~~
For each Purchase.PurchaseItem  // Targeting PurchaseItem sublevel within Purchase
	Where ProductId = &TargetProductId
	…
EndFor
~~~

Multiple transactions:
~~~
For each Customer, Purchase
	Where PurchaseDate >= &StartDate
	… // Relationship navigation inferred from FK
EndFor
~~~

Rules:
- Same attribute name across transactions refers to same data (extended attributes)
- Relationships inferred from FK; explicit joins forbidden

---

## Skip
Skip first N records

Syntax: `Skip <skip>`

Example:
~~~
&PageSize = 10
&PageNumber = 3
For each Product
	Skip (&PageNumber - 1) * &PageSize
	Count &PageSize
	… // Retrieves page 3 (records 21-30)
EndFor
~~~

---

## Count
Limit to N records

Syntax: `Count <count>`

Example:
~~~
For each Product
	Count 10
	Order (ProductPrice)
	… // Top 10 most expensive products
EndFor
~~~

Combination with Skip (pagination):
~~~
For each Purchase
	Skip &Offset
	Count &Limit
	Order (PurchaseDate)
	… // Latest purchases pagination
EndFor
~~~

---

## Order
Sort results by attributes

Syntax: `Order (none|<attribute-1>, …, <attribute-N>) [when <constraint>]`

Notes:
- Use `none` for disabling ordering under a condition
- Use `( )` for indicating descending order (e.g. `(CustomerName)` = Z→A)

Examples:

Simple ascending:
~~~
For each Customer
	Order CustomerName
	…
EndFor
~~~

Descending:
~~~
For each Product
	Order (ProductPrice)
	…
EndFor
~~~

Multiple attributes:
~~~
For each Order
	Order CustomerName, (OrderDate)
	…
EndFor
~~~

Conditional ordering:
~~~
For each Product
	Order (ProductPrice) when &SortByPrice
	Order ProductName when NOT &SortByPrice
	…
EndFor
~~~

Disable order dynamically:
~~~
For each Customer
	Order none when &FastModeEnabled
	Order CustomerName when NOT &FastModeEnabled
	…
EndFor
~~~

---

## Using
Apply reusable DataSelector object for filtering and/or sorting

Syntax: `Using <data-selector>`

Example:
~~~
For each Customer
	Using ActiveCustomersDS()
	…
EndFor
~~~

Rules:
- DataSelector must exist and be applicable to transaction
- Combines with other clauses (Where, Order)

---

## Unique
Return only unique combinations of specified attributes

Syntax: `Unique <attribute-1>, …, <attribute-N>`

Example:
~~~
For each Order
	Unique CustomerId
	… // One record per customer (first occurrence)
EndFor
~~~

Multiple attributes:
~~~
For each Order
	Unique CustomerId, OrderDate
	… // One record per customer per date
EndFor
~~~

---

## Where
Filter records

Syntax:
- `Where <condition> [when <constraint>]`
- `Where [not] <attribute-k> in <data-selector>`

Example:
~~~
For each Customer
	Where CustomerAge > 30
	…
EndFor
~~~

Multiple conditions (AND implicit):
~~~
For each Order
	Where OrderDate >= #2024-01-01#
	Where OrderStatus = OrderStatusType.Complete
	… // Both conditions must be true
EndFor
~~~

Conditional with OR:
~~~
For each Product
	Where ProductCategory = 'Electronics' OR ProductCategory = 'Computers'
	…
EndFor
~~~

Conditional with literal list:
~~~
For each Product
	Where ProductCategory IN ('Electronics', 'Computers')
	…
EndFor
~~~

Conditional with collection variable:
~~~
For each Order
	Where ProductCategory IN &CategoryCollection
	// Equivalently: Where &CategoryCollection.IndexOf(ProductCategory) > 0
	…
EndFor
~~~

Conditional with DataSelector:
~~~
For each Order
	Where CustomerId IN ActiveCustomersDS()
	…
EndFor
~~~

Negated:
~~~
For each Product
	Where NOT ProductId IN DiscontinuedProductsDS()
	…
EndFor
~~~

Conditional where:
~~~
For each Product
	Where ProductCategory = &SelectedCategory when NOT &SelectedCategory.IsEmpty()
	…
EndFor
~~~

Conditional with LIKE pattern:
~~~
For each Customer
	Where CustomerName LIKE '%Smith'
	…
EndFor
~~~

Rules:
- Conditions exclusively for filtering; never for linking transactions (automatic via FK)
- No subqueries allowed (use DataSelector instead)
- Extended attributes usable in conditions

Notes:
- See [common-filter](./common-filter.md) for optimization details

---

## Blocking
Process records in batches of N

Syntax: `Blocking <size>`

Use case: Bulk operations, performance optimization

Example:
~~~
For each Product
	Where ProductStock < 10
	Blocking 100
	// Update stock in batches of 100
	ProductStock += 50
EndFor
~~~

---

# CODE BLOCKS

## Main Block
Executed for each record matching filters and includes any command sequences

Example:
~~~
For each Customer
	Where CustomerAge > 30
	&Count += 1
	msg(format("Customer: %1", CustomerName), status)
EndFor
~~~

---

## Duplicate Block
Executed when update causes duplicate Candidate Key violation

Context:
- Only during update operations with Candidate Keys defined

Example:
~~~
For each Product
	Where ProductId = &TargetId
	ProductCode = &NewCode
When duplicate
	msg("Product code already exists", status)
EndFor
~~~

---

## None Block
Executed when no records match filters

Example:
~~~
For each Customer
	Where CustomerId = &SearchId
	&Found = True
When none
	msg("Customer not found", status)
	&Found = False
EndFor
~~~

---

# DATA OPERATIONS

## Read
Default behavior; no modifications; same concept as `SELECT` from SQL

Example:
~~~
For each Product
	&TotalPrice += ProductPrice
EndFor
~~~

## Update
Use `Assign` command

Example:
~~~
For each Product
	Where ProductStock < 10
	ProductStock = 0
EndFor
~~~

## Delete
Use `Delete` command

Example:
~~~
For each Order
	Where OrderDate < #2020-01-01#
	Delete
EndFor
~~~

## Insert
Use `New` command

Example:
~~~
For each Customer
	New
		ProductId = CustomerDefaultProductId
		ProductQuantity = 1
	EndNew
EndFor
~~~

---

# SUBLEVEL SYNTAX
Target specific subordinate level (WEAK relationship)

Syntax: `For each <transaction>.<level>`

Example:

Transaction structure:
~~~
Transaction Order
{
	OrderId* [ DataType = 'Numeric(10.0)' ]
	OrderDate [ DataType = 'Date' ]

	OrderItem
	{
		OrderItemId* [ DataType = 'Numeric(10.0)' ]
		ProductId []
		Quantity [ DataType = 'Numeric(5.0)' ]
	}
}
~~~

Targeting sublevel:
~~~
For each Order.OrderItem
	Where ProductId = &TargetProductId
	// Only iterates OrderItem sublevel
	&TotalQuantity += Quantity
EndFor
~~~

---

# CONTROL BREAK (NESTED FOR EACH)
Use nested `For each` to implement hierarchical control breaks (group headers, subgroup headers, and detail rows)

Guidelines:
- Use `N + 1` nested `For each` commands for `N` control breaks
- Define `Order` in each outer level representing a break key
- All levels in the same control break must resolve to the same base table
- Keep filtering conditions consistent across levels; inner levels may add stricter conditions only when required
- GeneXus navigation uses the concatenation of `Order` clauses defined across levels
- Each `<level-i-source>` can be omitted or explicit (`<transaction>` or `<transaction>.<level>`)
- Use as many nested levels as needed by the control-break hierarchy
- Outer levels define break keys through `Order`; the inner-most level handles detail processing

Pattern:
~~~
For each [<level-1-source>]
	[Where <level-1-filters>]
	[Order <level-1-break-order>]
	<level-1-break-start>

	For each [<level-2-source>]
		[Where <level-2-filters>]
		[Order <level-2-break-order>]
		<level-2-break-start-or-code>

		…

		For each [<level-N-source>]
			[Where <level-N-filters>]
			[Order <level-N-order>]
			<detail-code>
		EndFor

		<level-2-break-end>
	EndFor

	<level-1-break-end>
EndFor
~~~

Examples:
~~~
For each Invoice
	Where InvoiceDate >= &FromDate
	Where InvoiceDate <= &ToDate
	Order CustomerId

	&CustomerTotal = 0

	For each Invoice
		Where InvoiceDate >= &FromDate
		Where InvoiceDate <= &ToDate
		Order CustomerId, InvoiceDate

		&DateTotal = 0

		For each Invoice
			Where InvoiceDate >= &FromDate
			Where InvoiceDate <= &ToDate
			Order CustomerId, InvoiceDate, InvoiceId
			&DateTotal += InvoiceAmount
			&CustomerTotal += InvoiceAmount
		EndFor

		msg(format(!"Date total (%1): %2", InvoiceDate, &DateTotal), status)
	EndFor

	msg(format(!"Customer total (%1): %2", CustomerId, &CustomerTotal), status)
EndFor
~~~

---

# ADVANCED PATTERNS

## Pagination
~~~
&PageSize = 20
&Page = 3
For each Product
	Skip (&Page - 1) * &PageSize
	Count &PageSize
	Order ProductName
	… // Retrieves page &Page number with &PageSize elements
EndFor
~~~

## Top N
~~~
For each Customer
	Count &Limit
	Order (PurchaseCount)
	… // Retrieves top &Limit elements
EndFor
~~~

## Conditional Filtering
~~~
For each Order
	Where PurchaseDate >= &StartDate when NOT &StartDate.IsEmpty()
	Where PurchaseDate <= &EndDate when NOT &EndDate.IsEmpty()
	Where PurchaseStatus = &Status when NOT &Status.IsEmpty()
	…
EndFor
~~~

## Nested For Each
~~~
For each Customer
	Where CustomerCountry = 'USA'
	&CustomerPurchaseCount = 0
	For each Purchase
		// Relationship navigation inferred from control break
		&CustomerPurchaseCount += 1
	EndFor
	msg(format("Customer %1 has %2 purchases", CustomerName, &CustomerPurchaseCount), status)
EndFor
~~~

## Using with Additional Where
~~~
For each Customer
	Using ActiveCustomersDS()
	Where CustomerAge > 30
	… // DataSelector AND additional filter
EndFor
~~~

---

# CONSTRAINTS
- Base transactions inferred from attributes; explicit declaration optional
- Same attribute name implies same data (extended attributes)
- FK relationships inferred automatically; explicit joins forbidden
- Where clauses for filtering only, never for joining
- Order none improves performance when order irrelevant
- Unique returns first occurrence per combination
- Skip/Count for pagination; both expressions must be numeric
- Blocking for batch processing; improves performance on bulk updates
- When duplicate only for Candidate Key conflicts
- When none executes once if no matches (not per non-match)
- Sublevel syntax targets nested structure within transaction
- Never mix `For Each` navigation scope with `Business Component` member scope in the same statement
- Never reference transaction-qualified members inside `For Each` assignments (invalid pattern: `<Transaction>.<Attribute> = …`)
- Never use subqueries of any kind (including SQL-like) inside `For Each` clauses
