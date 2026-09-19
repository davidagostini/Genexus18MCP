---
name: properties-object-menu
description: Configurable menu properties for native and Angular targets
---

Use this file to select editable Menu properties and apply target-specific navigation settings

---

# GENERAL
Include [General](./properties-common.md) properties

## Main program
Marks the object as an executable native application entry point
- Type: `boolean`
- Default: `True`

## Connectivity Support
Defines whether the object runs online, offline, or inherits connectivity behavior from its caller
- Type: `enum{Online,Offline,Inherit}`
- Options:
	* `Online`: Works with data from the application server
	* `Offline`: Works with data from the local offline database
	* `Inherit`: Uses the connectivity behavior of the caller object

## Enable Data Caching
Specifies whether native mobile data caching is used
- Type: `boolean`
- Default: `True`

## Check For New Data
Determines whether cached data is reused or the server is checked for changes
- Type: `boolean`
- When: `EnableDataCaching = True`

---

# MENU

## Title
Title shown for this menu
- Type: `string`

## Background
Background image used by the menu
- Type: `string`

## Header
Header image used by the menu
- Type: `string`

## Class
Style class applied to the menu
- Type: `string`

## Control
Defines how menu entries are rendered
- Type: `enum{List,Tab,Table}`
- Options:
	* `List`: Renders menu actions as a vertical list
	* `Tab`: Renders menu actions as top-level tabs
	* `Table`: Renders menu actions as a table or grid-like launcher
- Default: `List`

## Tabs Distribution
Defines how tab options are distributed when the menu is rendered as tabs
- Type: `enum{Platform Default,Fixed Size,Scroll}`
- Options:
	* `Platform Default`: Uses the platform default tab distribution
	* `Fixed Size`: Gives each tab the same width; intended for a small number of options
	* `Scroll`: Gives each tab the required width and allows horizontal scrolling
- When: `Control = "Tab"`

## Show Application Bars
Shows or hides the native application bar for this menu
- Type: `boolean`
- Scope: `Android, Apple`

## Application Bars Class
Style class applied to the application bar
- Type: `string`
- Scope: `Android, Apple`
- When: `Show Application Bars = True`

## Show Ads
Enables or disables advertisements for this menu
- Type: `boolean`
- Deprecated: `True`
- Availability:
	* ProductVersion: `< 15.10`

## Ads Position
Position where advertisements are displayed
- Type: `enum{Top,Bottom}`
- Deprecated: `True`
- Availability:
	* ProductVersion: `< 15.10`

---

# ACTION
Applies to actions declared under `Items` and `Notifications`

## Name
Action identifier used to bind the action with its event
- Type: `string`
- Syntax: represented by `<name>` in the Menu source

## Description
Text shown for the action; use a single blank space when the rendered description must be empty
- Type: `string`

## Image
Image object used as the action icon
- Type: `string`

## Unselected Image
Image object used when a tab-style action is not selected, when supported by the target
- Type: `string`
- When: `Control = Tab`

## Class
Menu item theme class applied to the action
- Type: `string`
