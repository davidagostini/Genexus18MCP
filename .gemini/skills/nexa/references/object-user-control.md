---
name: object-user-control
description: Custom UI control definition for GeneXus toolbox extension
---

Defines custom reusable UI controls through `User Control` object for web interfaces

---

# DEFINITION
A `User Control` object defines a custom reusable UI control that extends the built-in toolbox

---

# SYNTAX
~~~
UserControl <name>
{
	#UserControlScreenTemplate
		<template>
	#End

	#UserControlProperties
		<definition>
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
- `<template>`: Markup with property/event bindings (check [TEMPLATE](#template)) section
- `<definition>`: Metadata with exposed properties/events (check [DEFINITION](#definition)) section
- `<properties>`: Optional object properties in TOML syntax; see [properties](./properties-object-user-control.md)
- `<documentation>`: Optional object documentation; see [common-markdown](./common-markdown.md)

---

# TEMPLATE
Use HTML with bindings for declaring properties, collections, events, and values

Property bindings:
- `{{Property}}`: Implicit `string` typed
- `{{Property:<data-type>}}`: Explicit typed
- `{{Property:<value-1>[|…[|<value-N>]]}}`: Enumerated values
- `{{Property=<default>}}`: Default value
- `{{Property:<value-1>[|…[|<value-N>]]=<default>}}`: Enumerated with default
- `{{#Property}} … {{/Property}}`: Custom property, typically an SDT
- `{{{Property}}}`: Raw HTML output (unescaped)
- `{{Property?}}text{{/Property}}`: Render only when property has value

Collection bindings:
- `{{#Items}}<li>{{.}}</li>{{/Items}}`: Iterable collection property
- `{{.}}`: Current item in simple collections
- `{{Field}}`: Field of current item in SDT/complex collections

Event bindings:
- `{{On<event-name>}}`: DOM event placeholder; place on the element that raises the event

Control Type binding:
- `{{DataElement}}`: Binds control value to the attribute/variable in the host panel
	* Required when `IsControlType = "True"` in `#Properties` section
	* Omit only when the control does not need to read or write a bound value

Container binding:
- `<slot></slot>`: Placeholder for content added inside the UC from the host panel

Example:
~~~html
<button {{OnClick}}>{{Caption}}</button>
{{#Items}}<li>{{.}}</li>{{/Items}}
~~~

---

# DEFINITION
Use XML nodes for declaring properties, events, and scripts exposed by the control

## Definition node
Container root node

Syntax:
~~~xml
<Definition
	auto="true|false"
	target="<target>"
	render-mode="always|first-time">
	…
</Definition>
~~~

Where:
- `auto`: Controls whether child nodes must be declared explicitly:
	* `true`: Infers from template; child nodes only needed to override type, default, or scope
	* `false`: Requires explicit `<Property>` and `<Event>` for every binding in the template
- `target`: Generator target name, e.g. `HTML`
- `render-mode`:
	* `always`: Re-renders on every change
	* `first-time`: Renders only on load

## Property node
Exposes a property with a name and type to the host

Syntax:
~~~xml
<Property
	Name="<property-name>"
	Type="string|numeric|boolean|sdt|domain"
	Default="<value>"
	Scope="DesignTime|RunTime" />
~~~

Where:
- `Name`: Public property name
- `Type`: Data type or enumerated domain
- `Default`: Default value
- `Scope`: Restricts availability; omit to allow both:
	* `DesignTime`: Design time only
	* `RunTime`: Runtime only

Rules:
- Required only when `auto="false"` or overriding inferred type, default, or scope

## Event node
Binds an event to a DOM event

Syntax:
~~~xml
<Event
	Name="On<event-name>"
	On="<dom-event>" />
~~~

Where:
- `Name`: Must match `{{On<event-name>}}` in the template; e.g. `OnClick`, `OnToggle`
- `On`: DOM event to bind; e.g. `click`, `change`, `blur`

Rules:
- Required only when `auto="false"`

## Script node
Embeds JavaScript in the control; return values are not supported

Syntax:
~~~xml
<Script
	Name="<script-name>"
	When="AfterShow|BeforeShow"
	Parameters="<param-1>[,…[,<param-N>]]"
	Type="js">
	…
</Script>
~~~

Where:
- `Name`: When `Parameters` is set, exposed as a callable public method on the control instance
- `When`:
	* `AfterShow`: Runs after render
	* `BeforeShow`: Runs before render
- `Parameters`: Comma-separated parameter names (no types); omit if none
- `Type`: Script type; e.g. `js`

---

# OUTPUT
Use [global-output](./global-output.md)

---

# CONSTRAINTS
- Use [global-constraints](./global-constraints.md)
- Map GeneXus data types to template types consistently:
	* `Character` / `VarChar` / `LongVarChar` → `string`
	* `Numeric` / `Decimal` → `numeric`
	* `Boolean` → `boolean`
	* `Structured Data Type` → `sdt`
	* `Domain` → `domain`

---

# CONVENTIONS
- Use semantic property names (`Caption`, `Value`, `Items`, `State`) instead of visual names
- Keep `#UserControlScreenTemplate` focused on structure
- Move behavior to `#UserControlProperties` section
- Prefer explicit public methods/events for host-panel integration
- Isolate framework dependencies through `BaseStyle`; avoid inline assets
- Include `{{DataElement}}` in template when `IsControlType = True`
- Use `<slot></slot>` to enable child content from the host panel

---

# EXAMPLES

## Example 1
Alert user control with enumerated type and conditional CSS class

~~~
UserControl Alert
{
	#UserControlScreenTemplate
		<div {{OnClick}} class="Alert {{Type?}}Alert--{{Type:info|warning|success|danger|none}}{{/Type}}">
			{{Text}}
		</div>
	#End

	#UserControlProperties
		<Definition auto="false" target="HTML" render-mode="always">
			<Property Name="Text" Type="string" Default="" />
			<Property Name="Type" Type="string" Default="info" />
			<Event Name="OnClick" On="click" />
		</Definition>
	#End

	#Properties
		BaseStyle = "Alert"
	#End
}
~~~

Populating from events object:
~~~
Event MyAlert.OnClick
	MyAlert.Text = ""
	MyAlert.Type = "none"
EndEvent
~~~

---

## Example 2
Accordion user control fed from an `SDT` collection

~~~
UserControl Accordion
{
	#UserControlScreenTemplate
		<div class="accordion">
			{{#Items}}
			<div class="accordion__section">
				<button class="accordion__header" {{OnToggle}}>{{Title}}</button>
				<div class="accordion__body">{{Content}}</div>
			</div>
			{{/Items}}
		</div>
	#End

	#UserControlProperties
		<Definition auto="false" target="HTML" render-mode="always">
			<Property Name="Items" Type="sdt" Default="" />
			<Event Name="OnToggle" On="click" />
			<Script When="AfterShow" Type="js">
				var sections = gx.fn.getElement(id).querySelectorAll('.accordion__section');
				sections.forEach(function(section) {
					var header = section.querySelector('.accordion__header');
					var body = section.querySelector('.accordion__body');
					body.style.display = 'none';
					header.addEventListener('click', function() {
						var isOpen = body.style.display !== 'none';
						body.style.display = isOpen ? 'none' : 'block'; /* handles collapse/expand client-side */
						header.classList.toggle('accordion__header--open', !isOpen);
					});
				});
			</Script>
		</Definition>
	#End

	#Properties
		BaseStyle = "Accordion"
	#End
}
~~~

Populating from events:
~~~
Event Start
	&AccordionItems.Clear()

	&AccordionItem = new()
	&AccordionItem.Title = "What is GeneXus?"
	&AccordionItem.Content = "GeneXus is a low-code development platform"
	&AccordionItems.Add(&AccordionItem)

	&AccordionItem = new()
	&AccordionItem.Title = "How do I create a User Control?"
	&AccordionItem.Content = "Create a User Control object and define its Template, Definition, and Properties"
	&AccordionItems.Add(&AccordionItem)

	MyAccordion.Items = &AccordionItems
EndEvent

Event MyAccordion.OnToggle
	Msg("Section toggled")
EndEvent
~~~

---

## Example 3
Numeric input as `Control Type` with value binding

~~~
UserControl NumericInput
{
	#UserControlScreenTemplate
		<div class="numeric-input">
			<label>{{Caption}}</label>
			<input type="number"
				min="{{Min:numeric}}"
				max="{{Max:numeric}}"
				placeholder="{{Placeholder}}"
				{{DataElement}}
				{{OnChange}} />
		</div>
	#End

	#UserControlProperties
		<Definition auto="false" target="HTML" render-mode="always">
			<Property Name="Caption" Type="string" Default="" />
			<Property Name="Min" Type="numeric" Default="0" />
			<Property Name="Max" Type="numeric" Default="100" />
			<Property Name="Placeholder" Type="string" Default="" Scope="DesignTime" />
			<Event Name="OnChange" On="change" />
		</Definition>
	#End

	#Properties
		IsControlType = true /* makes this control available in the Control Type dropdown */
		DataTypeFilter = "Numeric"
		BaseStyle = "NumericInput"
	#End
}
~~~

Populating from events:
~~~
Event Start
	MyNumericInput.Caption = "Quantity"
	MyNumericInput.Min = 1
	MyNumericInput.Max = 999
	MyNumericInput.Placeholder = "Enter a quantity"
EndEvent

Event MyNumericInput.OnChange
	If &Quantity < 1 Or &Quantity > 999
		Msg("Quantity must be between 1 and 999")
	EndIf
EndEvent
~~~