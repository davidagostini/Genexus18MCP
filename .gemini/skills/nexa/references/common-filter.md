---
name: common-filter
description: Filter expressions in data retrieval contexts for query optimization
---

Defines how filter expressions are optimized into SQL queries in data retrieval contexts

---

# DEFINITION
GeneXus optimizes data queries by translating filter expressions into SQL `WHERE` clauses:
- All translatable functions → Full SQL filter
- Any non-translatable function → Partial SQL filter + row-by-row filter

Degraded queries raise this warning:
_⚠️ Constraint evaluated in the client. This may lead to poor performance_

Must check the `Environment` data store DBMS definition

---

# CONTEXTS
- [For Each command](./common-commands-foreach.md)
	* `Where` clause
	* `Using` clause with `DataSelector` object
- [DataProvider object](./object-data-provider.md)
	* `Where` clauses in groups
	* `Using` clause with `DataSelector` object
- [DataSelector object](./objet-data-selector.md)
	* `#Conditions` section
- [Procedure object](./object-procedure.md)
	* `#Conditions` section
	* `Where` clauses in `For Each` commands
- [Panel object](./object-panel.md)
	* `#Conditions` section
	* `Conditions` property in `Grid` controls
	* `Where` clauses in `Load` event with `For Each` commands

---

# FUNCTIONS
Translatable functions with DBMS-dependent SQL translations

## Date/Time
- [Day](./common-functions.md#day)
- [Month](./common-functions.md#month)
- [Year](./common-functions.md#year)
- [Hour](./common-functions.md#hour)
- [Minute](./common-functions.md#minute)
- [Second](./common-functions.md#second)
- [AddDays](./common-functions.md#adddays)
- [AddMth](./common-functions.md#addmth): Except Oracle
- [AddYr](./common-functions.md#addyr)
- [TAdd](./common-functions.md#tadd): Only SQLServer, MySQL, Informix, PostgreSQL
- [TDiff](./common-functions.md#tdiff)
- [Age](./common-functions.md#age)
- [DoW](./common-functions.md#dow): Except Oracle
- [EoM](./common-functions.md#eom): Except MySQL, DB2

## Numeric
- [Int](./common-functions.md#int)
- [Round](./common-functions.md#round)
- [Trunc](./common-functions.md#trunc): Except SQLite
- [Str](./common-functions.md#str): Except DB2, DB2 ISERIES

## String
- [Concat](./common-functions.md#concat)
- [Len](./common-functions.md#len)
- [Asc](./common-functions.md#asc): Except DB2 ISERIES
- [Val](./common-functions.md#val)
- [Trim](./common-functions.md#trim)
- [LTrim](./common-functions.md#ltrim)
- [RTrim](./common-functions.md#rtrim)
- [Upper](./common-functions.md#upper)
- [Lower](./common-functions.md#lower)
- [SubStr](./common-functions.md#substr)
- [StrReplace](./common-functions.md#strreplace): Except DB2 ISERIES
- [StrSearch](./common-functions.md#strsearch): Except Informix
- [StrSearchRev](./common-functions.md#strsearchrev): Only SQLServer, Oracle
- [PadL](./common-functions.md#padl): Except DB2 ISERIES
- [PadR](./common-functions.md#padr)
- [IsMatch](./common-data-types.md#character): Only Oracle, MySQL, PostgreSQL
- [ReplaceRegEx](./common-data-types.md#character): Only Oracle, PostgreSQL

## GUID
- [FromString](./common-data-types.md#guid): Only SQLServer, Oracle, MySQL, PostgreSQL
- [ToString](./common-data-types.md#guid): Except SQLite

## Geography
- [Distance](./common-extended-type-geography.md#distance): Only SQLServer (2012+), MySQL
- [Intersect](./common-extended-type-geography.md#intersect): Only SQLServer

## Embedding
- [Distance](./common-data-types.md#embedding): Only PostgreSQL

## Conditional
- [Iif](./common-functions.md#iif)

## Formulas
- [Average](./common-formulas.md#average)
- [Count](./common-formulas.md#count)
- [Find](./common-formulas.md#find)
- [Sum](./common-formulas.md#sum)
- [Max](./common-formulas.md#max)
- [Min](./common-formulas.md#min)

---

# OPTIMIZATION
Filter queries become SQL queries that can be optimized

## Degraded
~~~
For Each Invoice
    Where Year(InvoiceDate) = &Year
    Where Format(!"%1", InvoiceId) = &Id
	…
EndFor
~~~

Internally:
- Generated SQL filter: `WHERE YEAR(InvoiceDate) = :year`
- Performs row-by-row filtering for `Format` function

## Optimized
~~~
For Each Invoice
    Where Year(InvoiceDate) = &Year
    Where Str(InvoiceId) = &Id
	…
EndFor
~~~

Internally:
- Generated SQL filter: `WHERE YEAR(InvoiceDate) = :year AND STR(InvoiceId) = :Id`
- Performs complete filtering in the DBMS

---

# CONSTRAINTS
- Only filter expressions with supported functions for query optimization
- Avoid unsupported functions; any usage forces row-by-row filtering
