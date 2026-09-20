---
name: properties-object-work-with-devices
description: Configurable WorkWith pattern instance properties
---

Use this file to select editable properties, defaults, and valid options for a `WorkWith` pattern instance and its nodes

---

# GENERAL
Include [General](./properties-common.md) properties

## Caption
Human-readable label for various UI elements
- Type: `string`

## Lapse
Seconds of user inactivity before auto-refresh; `0` disables.
- Type: `integer`
- Default: `0`

## ShowAds
Displays advertisements in the generated panel
- Type: `boolean`

## AdsPosition
The Position of the Advertising Banner
- Type: `enum{Top,Bottom}`
- Options:
	* `Top`: At the Top
	* `Bottom`: At the Bottom

## Share
Content string passed to the native share sheet when the user triggers a share action
- Type: `string`

## DeepLinkName
URL path registered for deep linking to this node
- Type: `string`

---

# LEVEL NODE

## Name
Existing Transaction level name
- Type: `string`

## Description
Human-readable label for the description attribute
- Type: `string`

---

# LIST NODE

## Caption
Human-readable label for various UI elements
- Type: `string`

## Lapse
Seconds of user inactivity before auto-refresh; `0` disables.
- Type: `integer`
- Default: `0`

## ShowAds
Displays advertisements in the generated panel
- Type: `boolean`

## AdsPosition
The Position of the Advertising Banner
- Type: `enum{Top,Bottom}`
- Options:
	* `Top`: At the Top
	* `Bottom`: At the Bottom

## Share
Content string passed to the native share sheet when the user triggers a share action
- Type: `string`

## DeepLinkName
URL path registered for deep linking to this node
- Type: `string`

## DataSelector
A Data Selector object used to filter grid data in nodes
- Type: `string`

## DataSelectorParameters
Comma-separated parameters for DataSelector; can be constant, attribute, variable or expression
- Type: `string`

---

# GRIDDATA NODE

## Lapse
Seconds of user inactivity before auto-refresh; `0` disables.
- Type: `integer`
- Default: `0`

## Conditions
GeneXus condition expression applied to filter grid rows
- Type: `string`

## BaseTrn
Source `Transaction` that drives the grid navigation
- Type: `string`

---

# ORDER NODE

## Name
Existing Attribute or Variable name
- Type: `string`

## BreakBy
Enable control-break grouping on this order; groups rows by the break attribute(s)
- Type: `boolean`
- Default: `False`

## EnableAlphaIndexer
Show an alphabetical side-scroller (A-Z index) for fast scrolling
- Type: `boolean`
- Default: `False`
- Require: `BreakBy = True`

## BreakByUpTo
For multi-attribute orders, the attribute up to which grouping is applied; attributes after this are used only for sorting within each group
- Type: `string`

## DescriptionAttribute
Attribute displayed as the group header label for each control-break group
- Type: `string`

---

# BREAKBY NODE

## DescriptionAttribute
Attribute displayed as the group header label for each control-break group
- Type: `string`

---

# SEARCH NODE

## Caption
Human-readable label for various UI elements
- Type: `string`

## OptionForIndividualFields
iOS only: allows the user to restrict the search to a single field; `False` searches all fields simultaneously
- Type: `boolean`
- Default: `False`

## AlwaysVisible
Keep the search box permanently visible; when `False` the search box is hidden until triggered
- Type: `boolean`
- Default: `False`

## FilterOperator
Match operator applied when filtering by the search term
- Type: `enum{Begins with,Contains}`
- Default: `Contains`
- Options:
	* `Begins with`
	* `Contains`

## CaseSensitive
Apply case-sensitive matching to the search term
- Type: `boolean`
- Default: `False`

## BreakBy
How the search result interacts with the order's break-by grouping
- Type: `enum{Disabled,Use order's break by,Platform Default}`
- Default: `Platform Default`
- Options:
	* `Disabled`: ignores any break-by grouping during search
	* `Use order's break by`: applies the break-by defined in the active Order
	* `Platform Default`: Android behaves like `Use order's break by`; iOS behaves like `Disabled`

---

# DETAIL NODE

## Caption
Human-readable label for various UI elements
- Type: `string`

## Lapse
Seconds of user inactivity before auto-refresh; `0` disables.
- Type: `integer`
- Default: `0`

## ShowAds
Displays advertisements in the generated panel
- Type: `boolean`

## AdsPosition
The Position of the Advertising Banner
- Type: `enum{Top,Bottom}`
- Options:
	* `Top`: At the Top
	* `Bottom`: At the Bottom

## Share
Content string passed to the native share sheet when the user triggers a share action
- Type: `string`

## DeepLinkName
URL path registered for deep linking to this node
- Type: `string`

## DataSelector
A Data Selector object used to filter grid data in nodes
- Type: `string`

## Display
How sections inside `Detail` are presented
- Type: `enum{Inline,Link,Tabs,Platform Default}`
- Default: `Platform Default`
- Options:
	* `Inline`: Renders sections stacked in a single view
	* `Link`: Navigates to each section via a link
	* `Tabs`: Renders sections as tab pages
	* `Platform Default`: Inherits platform recommendation

---

# SECTION NODE

## Caption
Human-readable label for various UI elements
- Type: `string`

## Lapse
Seconds of user inactivity before auto-refresh; `0` disables.
- Type: `integer`
- Default: `0`

## ShowAds
Displays advertisements in the generated panel
- Type: `boolean`

## AdsPosition
The Position of the Advertising Banner
- Type: `enum{Top,Bottom}`
- Options:
	* `Top`: At the Top
	* `Bottom`: At the Bottom

## Share
Content string passed to the native share sheet when the user triggers a share action
- Type: `string`

## DeepLinkName
URL path registered for deep linking to this node
- Type: `string`

## Name
Unique name
- Type: `string`

## DataSelector
A Data Selector object used to filter grid data in nodes
- Type: `string`

## DataSelectorParameters
Comma-separated parameters for DataSelector; can be constant, attribute, variable or expression
- Type: `string`

## LinkClass
Theme or style class applied to the section item when rendered as a link
- Type: `string`

## Image
Image displayed on action button or when selected
- Type: `string`

## UnselectedImage
Image displayed for unselected tab-style actions
- Type: `string`
