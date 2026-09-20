---
name: object-menu
description: Native application menu definition for Android, Apple, and Angular environments
---

Defines a native `Menu` object, formerly known as `Dashboard`, used as an application entry point or navigation surface

---

# DEFINITION
A `Menu` object defines a native navigation screen for Android, Apple, and Angular environments

Main goals:
- Present actions as menu items, notifications, tabs, or list/table entries
- Route each menu action through an event with the same action name
- Serve as a mobile main object when the application starts from a navigation menu

Relationship with [Panel](object-panel.md):
- Menu actions usually call `Panel`, `Work With`, or other executable objects from events
- Keep target objects compatible with the same native environment
- Use `Panel` objects for rich screens and `Menu` objects for top-level navigation

---

# SYNTAX
~~~
Menu <name>
{
	<source>

	#Events
		<events>
	#End

	#Properties
		<properties>
	#End

	#Documentation
		<documentation>
	#End
}
~~~

Where:
- `<name>`: Object name using alphanumeric or underscore, starting with letter
- `<source>`: Menu source definition; see [SOURCE](#source) section
- `<events>`: Event handlers triggered by menu actions; see [EVENTS](#events) section
- `<properties>`: Optional object properties in TOML syntax; see [properties](./properties-object-menu.md)
- `<documentation>`: Optional object documentation; see [markdown](./common-markdown.md)

---

# SOURCE
Defines the root menu control and the action groups rendered by the object

Syntax:
~~~
Menu
[
	<properties>
]
{
	Items
	{
		<items>
	}
	Notifications
	{
		<notifications>
	}
}
~~~

Where:
- `<properties>`: Properties for the visual menu control; see [properties](./properties-object-menu.md#menu)
- `<items>`: Menu action entries; see [ITEMS](#items) section
- `<notifications>`: Notification action entries; see [NOTIFICATIONS](#notifications) section

Notes:
- Use `Tab` only when each item represents a stable top-level destination
- Prefer `List` when the menu has many entries or text-heavy destinations
- Prefer `Table` when entries are icon-oriented and have similar importance

---

# ITEMS
Defines primary menu actions rendered as regular destinations or commands

Syntax:
~~~
<name> [ <properties> ]
~~~

Where:
- `<name>`: Unique name for the navigation action and associated event
- `<properties>`: Optional action properties; see [properties](./properties-object-menu.md#action)

Notes:
- Items are the main navigation entries of the menu
- Keep action names semantic and stable; avoid names tied to temporary captions
- Keep descriptions short, especially for `Tab` menu
- Define a matching event when the item performs navigation or custom logic

Example:
~~~
Customers [ Description = "Customers", Image = "CustomersIcon", Class = "MenuItem" ]
Orders [ Description = "Orders", Image = "OrdersIcon", Class = "MenuItem" ]
~~~

---

# NOTIFICATIONS
Defines secondary menu actions used for notification-like or alert-oriented entries

Syntax:
~~~
<name> [ <properties> ]
~~~

Where:
- `<name>`: Name for the notification action and associated event
- `<properties>`: Optional action properties; see [properties](./properties-object-menu.md)

Notes:
- Notifications represent secondary, time-sensitive, or status-oriented actions
- Define a matching event when the notification opens a screen or runs custom logic

Example:
~~~
PendingApprovals [ Description = "Pending approvals", Image = "AlertIcon", Class = "MenuItemAlert" ]
UnreadMessages [ Description = "Unread messages", Image = "MessagesIcon", Class = "MenuItemAlert" ]
~~~

---

# EVENTS
Defines code executed when menu items or notifications are selected

Syntax:
~~~
Event '<event-name>'
	<event-code>
EndEvent
~~~

Where:
- `<event-name>`: Event name matching an action declared in `Items` or `Notifications`
- `<event-code>`: Navigation or orchestration code executed by the event

Example:
~~~
Event 'Customers'
	ListCustomer()
EndEvent
~~~

Notes:
- Use one event per actionable menu entry
- Match event names with item or notification action names
- Keep events focused on navigation and lightweight orchestration
- Move business logic to `Procedure`, `Data Provider`, or domain-specific objects

---

# OUTPUT
Use [global-output](./global-output.md)

---

# CONSTRAINTS
- Use [global-constraints](./global-constraints.md)
- Use `Android`, `Apple`, or `Angular` generators only
- Treat `Menu` as the textual representation of the former `Dashboard` object
- Keep every action event name aligned with its declared action name
- Use `Tab` menu only for a small number of top-level destinations
- Avoid duplicating navigation logic across multiple action events; extract shared logic when needed
- Keep target objects reachable from the native application flow

---

# CONVENTIONS
- Use `Menu` for top-level navigation, not for complex content screens
- Prefer action names based on destinations: `Home`, `Customers`, `Orders`, `Settings`
- Keep `Items` for primary destinations and `Notifications` for alert-like or time-sensitive entries
- Use concise descriptions and recognizable images for native ergonomics
- Keep event code short and delegate screen behavior to called `Panel` or `Work With` objects

---

# EXAMPLES

## Example 1
Native menu with list navigation
~~~
Menu SalesMenu
{
	Menu
	[
		Control = "List"
	]
	{
		Items
		{
			Customers
			[
				Description = "Customers",
				Image = "CustomersIcon",
				Class = "MenuItem"
			]
			Orders
			[
				Description = "Orders",
				Image = "OrdersIcon",
				Class = "MenuItem"
			]
			Reports
			[
				Description = "Reports",
				Image = "ReportsIcon",
				Class = "MenuItem"
			]
		}
		Notifications
		{
			PendingApprovals
			[
				Description = "Pending approvals",
				Image = "AlertIcon",
				Class = "MenuItemAlert"
			]
		}
	}

	#Events
		Event Customers
			Panels.ListCustomers()
		EndEvent

		Event Orders
			Panel.ListOrders()
		EndEvent

		Event Reports
			Panel.ListReports()
		EndEvent

		Event PendingApprovals
			Panel.ListApprovals()
		EndEvent
	#End

	#Properties
		Description = "Sales Menu"
	#End
}
~~~

## Example 2
Menu using tabs for top-level destinations
~~~
Menu CustomerTabs
{
	Menu
	[
		Control = "Tab"
		ShowApplicationBars = False
	]
	{
		Items
		{
			Home
			[
				Description = "Home",
				Image = "HomeIcon",
				UnselectedImage = "HomeIconInactive"
			]
			Customers
			[
				Description = "Customers",
				Image = "CustomersIcon",
				UnselectedImage = "CustomersIconInactive"
			]
			Settings
			[
				Description = "Settings",
				Image = "SettingsIcon",
				UnselectedImage = "SettingsIconInactive"
			]
		}
	}

	#Events
		Event Home
			UI.Home()
		EndEvent

		Event Customers
			UI.Customer.ViewCustomers()
		EndEvent

		Event Settings
			UI.Settings.ShowSettings()
		EndEvent
	#End

	#Properties
		Description = "Customer Tabs"
	#End
}
~~~
