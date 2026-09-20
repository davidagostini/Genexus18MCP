---
name: object-panel
description: Screen definition for Android, Apple, Angular, or Web environments
---

Defines user interface screens for multiple platforms with layout, events, and data binding

---

# DEFINITION
A `Panel` object defines a screen for Android, Apple, Angular, or Web environments

Types:
- `Panel` (or `SDPanel`): Build multi-platform screens; Android, Apple, and Angular
- `WebPanel`: Build Web pages
- `MasterPanel`: Share Panel layout
- `MasterPage`: Share Web page layout
- `WebComponent`: Reuse Web UI sections
- `Stencil`: Reuse visual layouts

See [Frontend Design](./frontend-design.md) for UI/UX guidelines

---

# SYNTAX
~~~
<type> <name>
{
	#Events
		<events>
	#End

	#Rules
		<rules>
	#End

	#Conditions
		<conditions>
	#End

	#Variables
		<variables>
	#End

	#Layout
		<layout>
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
- `<type>`: Object type: `Panel`, `WebPanel`, `WebComponent`, `MasterPage`, `MasterPanel`, `Stencil`
- `<name>`: Object name using alphanumeric or underscore, starting with letter
- `<events>`: Event handlers triggered by user actions; see [EVENTS](#events) section
- `<rules>`: Business rules (parm, etc.); see [RULES](#rules) section
- `<conditions>`: Boolean filter predicates; multiple lines imply `AND` operations
- `<variables>`: Variable definitions with `DataType`
- `<layout>`: Hierarchical/composable XML-based layout definition; see [LAYOUT](#layout) section
- `<properties>`: Optional object properties in TOML syntax; see [properties](./properties-object-panel.md)
- `<documentation>`: Optional object documentation; see [markdown](./common-markdown.md)

Notes:
- See [common-filter](./common-filter.md) for optimization details

---

# EVENTS
See [common-events](./common-events.md)

Allowed event names / execution sequence:
- Initialize (client-side)
	* `ClientStart` (native-only): Native UI setup
	* `<navigation>.Start` (native-only): Navigation UI setup
- Initialize (server-side)
	* `Start`: One-time data and Web UI setup
- Refresh (server-side)
	* `[<grid-name>.]Refresh`: Fixed data loading
	* `[<grid-name>.]Load`: Grid loading; executed:
		- For each row on grids with base table
		- Only once on grids without base table
- Interaction (client-side)
	* `Enter` (web-only): Enter/confirm
	* `Back` (native-only): Back action/gesture
		- Use empty body for disabling the back action
		- Use `Return` command for closing current screen
	* `'<custom-name>'`: Custom action; required for buttons
	* `<control-name>.<event-name>`: Control action/gesture
	* `<external-object>.<event-name>`: External object event

Navigation event names:
- `Slide`,
  `Split`,
  `Cascade`,
  `Flip`: Only for matching `NavigationStyle` value in [Platform WorkWithDevices settings](./model-work-with-devices.md#platform)
- `Tabs`: Only for `Panel` objects referenced by [Menu object](./object-menu.md) having `Control` property with `Tabs` value

Control event names:
- Web:
	* `Click`: Left click
	* `DblClick`: Double click
	* `RightButton`: Right-button click; input-based controls only
- Native:
	* `Tap`: Short touch
	* `DoubleTap`: Two quick-touches
	* `LongTap`: Touch and hold
	* `Swipe`: Fast swipe in any direction
	* `SwipeTop`: Upward swipe
	* `SwipeLeft`: Leftward swipe
	* `SwipeRight`: Rightward swipe
	* `SwipeBottom`: Downward swipe
	* `Drag`: Start drag; define dragged data
	* `Drop(&arg)`: Drop dragged data
	* `DropAccepted`: Before `Drop` when accepted
	* `DragCanceled`: Drag cancelled
	* `PullRelease`: Release pull-to-refresh gesture
	* `SelectionChanged`: Grid item selection changed
	* `ActivePageChanged`: Tab active page changed
- Common:
	* `IsValid`: After basic validation
	* `ControlValueChanged`: After input value changed
	* `ControlValueChanging(&arg)`: While input value change; receives new value

Rules:
- Use `Composite … EndComposite` blocks in native-only client-side events with multiple actions
- Use `Refresh` command for `Form` refresh and subsequent `Refresh` event execution
- Use `Load` command only in `Load` event for `Grid` controls without base table

Example:
~~~
Event 'Calculate' /* button event */
	&Total = &Price * &Quantity
	msg(format(!"Total: %1", &Total), status)
EndEvent

Event &LanguageCombo.ControlValueChanged /* combobox event */
	SetLanguage(&LanguageCombo)
	Refresh
EndEvent

Event AppLifecycle.AppStateChanged(&oldApplicationState, &newApplicationState) /* external object event */
	msg(format(!"Change from %1 to %2", &oldApplicationState, &newApplicationState), status)
EndEvent
~~~

---

# RULES
See [common-rules](./common-rules.md)

---

# LAYOUT
Declarative XML-based screen layout schema used in `#Layout` region

Scopes:
- Mirror HTML concepts with GeneXus-specific syntax
- Define hirarchical structure and control composition
- Define visual styling in `DesignSystem` object classes

Rules:
- See [GeneXus Layout](./frontend-layout.md) for available GXML syntax elements and attributes
- Escape XML special characters; e.g. `&` (✘) → `&amp;` (✓), `"` (✘) → `&quote;` (✓)
- Ensure all measures only use `px`, `dip`, or `%` units
- Bind `action` controls with named events in `#Events` section
- Prefer `smart` containers over other containers
	* Use `table` for legacy alignment
	* Use `canvas` for overlapping controls

---

# OUTPUT
Use [global-output](./global-output.md)

---

# CONSTRAINTS
- Use [global-constraints](./global-constraints.md)
- Include [common-standard-variables](./common-standard-variables.md) according to panel context
- Place code only inside `Panel` object sections
- Forbid `#Rules` and `#Conditions` sections in `Stencil` object definition
- Events use qualifiers when needed: `[WEB]`, `[WIN]`, `[TEXT]`
- Ensure all classes from `Layout` section exist in linked `DesignSystem` object
	* Create linked `DesignSystem` object when missing
	* Add missing classes required by `Layout` section

---

# CONVENTIONS
- Plan screen purpose, actions, and navigation before designing the screen layout
- Give each actionable control an interaction trigger and feedback
- Keep layout structure in `Panel` object; keep visual styling in `DesignSystem` object
- Split layout into clear regions:
	* Use `header` for context title and global actions
	* Use `content` for primary task content
	* Use `aside` as optional secondary/support content
	* Use `footer` for persistent primary or closing actions
- Place language selector for multi-language apps:
	* Add `VarChar` variable in panel definition
	* Put variable-bound `<combobox>` element in panel layout
	* Set `Values` attribute in `<combobox>` with supported languages
	* Use `GetLanguage` function in `Start` event to initialize language
	* Use `SetLanguage` function in `ControlValueChanged` combo event to switch language

---

# EXAMPLES

## Example 1
Simple Panel with Grid
~~~
Panel CustomerList
{
	#Events
		Event Start
			&Customers.Clear()
		EndEvent

		Event Load
			For &Customer in ListCustomersDP()
				&Customers.Add(&Customer)
				load
			EndFor
		EndEvent

		Event GridCustomers.ItemClick
			ViewCustomer(&Customer.CustomerId)
		EndEvent

		Event 'NewCustomer'
			NewCustomer()
		EndEvent

		Event Back
			// disable back button or gesture
		EndEvent
	#End

	#Rules
	#End

	#Conditions
	#End

	#Variables
		Customers [ DataType = 'CustomerInfo', Collection = 'True' ]
		Customer [ DataType = 'CustomerInfo' ]
	#End

	#Layout
		<layout>
			<view>
				<smart
					name="MainTable"
					class="page"
					width="100%"
					height="100%"
					columns="100%"
					rows="96dip;100%;72dip">
					<row>
						<cell
							class="page-header"
							hAlign="Left"
							vAlign="Middle">
							<label
								name="Title"
								caption="Customers"
								class="text-title"/>
						</cell>
					</row>
					<row>
						<cell class="page-content">
							<smartGrid
								name="GridCustomers"
								class="surface"
								autoGrow="True">
								<smart
									name="GridCustomerItem"
									width="100%"
									height="88dip"
									class="surface-muted">
									<row>
										<cell>
											<input
												name="ctlCustomerName"
												data="&amp;Customer.Name"
												readonly="True"
												class="text-body"/>
										</cell>
									</row>
								</smart>
							</smartGrid>
						</cell>
					</row>
					<row>
						<cell
							class="page-footer"
							hAlign="Right"
							vAlign="Middle">
							<action
								name="ButtonNew"
								caption="New Customer"
								onClickEvent="'NewCustomer'"
								class="button-primary" />
						</cell>
					</row>
				</smart>
			</view>
		</layout>
	#End

	#Properties
		Caption = "Products List"
		Style = "MyDesignSystem"
	#End
}
~~~

## Example 2
Panel with Events
~~~
Panel ProductDetail
{
	#Events
		Event Start
			&ProductId = &Parm.ProductId
		EndEvent

		Event Refresh
			For Each Product
				Where ProductId = &ProductId
				&ProductName = ProductName
				&ProductPrice = ProductPrice
				&ProductImage = ProductImage
			EndFor
		EndEvent

		Event 'BuyNow'
			AddToCart(&ProductId)
			msg(!"Added to cart", status)
		EndEvent

		Event Back
			Return // close current screen; back to CustomerList panel
		EndEvent
	#End

	#Rules
		parm(in: &Parm);
	#End

	#Conditions
	#End

	#Variables
		Parm [ DataType = 'ProductDetailParm' ]
		ProductId [ DataType = 'Numeric(10.0)' ]
		ProductName [ DataType = 'VarChar(128)' ]
		ProductPrice [ DataType = 'Numeric(10.2)' ]
		ProductImage [ DataType = 'Image' ]
	#End

	#Layout
		<layout>
			<view>
				<smart
					name="Container"
					width="100%"
					height="100%"
					columns="100%"
					rows="200dip;60dip;60dip;100%">
					<row>
						<cell hAlign="Center">
							<image
								name="ctlProductImage"
								data="&amp;ProductImage"
								width="100%"
								height="200dip"/>
						</cell>
					</row>
					<row>
						<cell>
							<input
								name="ctlProductName"
								data="&amp;ProductName"
								readonly="True"
								class="text-title"/>
						</cell>
					</row>
					<row>
						<cell>
							<input
								name="ctlProductPrice"
								data="&amp;ProductPrice"
								readonly="True"
								class="text-body"/>
						</cell>
					</row>
					<row>
						<cell hAlign="Center" vAlign="Middle">
							<action
								name="ButtonBuy"
								caption="Buy Now"
								onClickEvent="'BuyNow'"
								class="button-primary"/>
						</cell>
					</row>
				</smart>
			</view>
		</layout>
	#End

	#Properties
		Caption = "Product Detail"
		Style = "MyDesignSystem"
	#End
}
~~~

## Example 3
Nested stencils and runtime event interaction
~~~
Stencil FieldStencil /* Module: Component.Common */
{
	#Variables
		FieldValue [ DataType = 'VarChar(256)' ]
	#End

	#Layout
		<layout>
			<view>
				<smart
					name="FieldContainer"
					width="100%"
					class="field">
					<row>
						<cell
							width="100%"
							height="24dip">
							<label
								name="FieldTextBlock"
								caption="Field"
								class="field-label"/>
						</cell>
					</row>
					<row>
						<cell
							width="100%"
							height="40dip">
							<input
								name="ctlFieldValue"
								data="&amp;FieldValue"
								inviteMessage="Value..."
								class="field"/>
						</cell>
					</row>
				</smart>
			</view>
		</layout>
	#End
}
~~~

~~~
Stencil FormStencil /* Module: Component.Common */
{
	#Variables
		Name [ DataType = 'VarChar(128)' ]
		Email [ DataType = 'Email, GeneXus' ]
		Message [ DataType = 'LongVarChar(1M)' ]
	#End

	#Layout
		<layout>
			<view>
				<smart
					name="FormContainer"
					width="100%"
					class="surface">
					<row>
						<cell width="100%">
							<stencil:Component.Common.FieldStencil name="NameField">
								<label
									name="FieldTextBlock"
									caption="Full name"/>
								<input
									name="ctlName"
									data="&amp;Name"
									bind="&amp;FieldValue"
									inviteMessage="Your name..."/>
							</stencil:Component.Common.FieldStencil>
						</cell>
					</row>
					<row>
						<cell width="100%">
							<stencil:Component.Common.FieldStencil name="EmailField">
								<label
									name="FieldTextBlock"
									caption="Email address"/>
								<input
									name="ctlEmail"
									data="&amp;Email"
									bind="&amp;FieldValue"
									inviteMessage="Your email..."/>
							</stencil:Component.Common.FieldStencil>
						</cell>
					</row>
					<row>
						<cell width="100%">
							<stencil:Component.Common.FieldStencil name="MessageField">
								<label
									name="FieldTextBlock"
									caption="Message"/>
								<input
									name="ctlMessage"
									data="&amp;Message"
									bind="&amp;FieldValue"
									inviteMessage="Your message..."/>
							</stencil:Component.Common.FieldStencil>
						</cell>
					</row>
					<row>
						<cell width="100%">
							<!-- button-like table with label -->
							<table
								name="SendFrame"
								width="100%"
								columns="100%"
								rows="48dip"
								class="action-table">
								<row>
									<cell
										hAlign="Center"
										vAlign="Middle">
										<label
											name="SendLabel"
											caption="Send"
											class="acton-label"/>
									</cell>
								</row>
							</table>
						</cell>
					</row>
				</smart>
			</view>
		</layout>
	#End
}
~~~

~~~
WebPanel ContactWebPanel
{
	#Events
		Event ContactForm.SendFrame.Click /* override caption and submit */
			ContactForm.SendLabel.Caption = "Contact us again"
			msg(!"Your message was sent.", status)
		EndEvent
	#End

	#Variables
		Contact [ DataType = 'ContactData' ] /* SDT with Name, Email, and Message fields */
	#End

	#Layout
		<layout>
			<view>
				<smart
					name="PageShell"
					width="100%"
					height="100%"
					columns="100%"
					rows="72dip;100%"
					class="page">
					<row>
						<cell
							class="page-header"
							hAlign="Left"
							vAlign="Middle">
							<label
								name="ContactTitle"
								caption="Contact us"
								class="text-title"/>
						</cell>
					</row>
					<row>
						<cell class="page-content">
							<stencil:Component.Common.FormStencil name="ContactForm">
								<input
									name="ctlContactName"
									data="&amp;Contact.Name"
									bind="&amp;Name"/>
								<input
									name="ctlContactEmail"
									data="&amp;Contact.Email"
									bind="&amp;Email"/>
								<input
									name="ctlContactMessage"
									data="&amp;Contact.Message"
									bind="&amp;Message"/>
								<label
									name="SendLabel"
									caption="Contact Us"/>
							</stencil:Component.Common.FormStencil>
						</cell>
					</row>
				</smart>
			</view>
		</layout>
	#End

	#Properties
		Caption = "Contact"
		Style = "MyDesignSystem"
	#End
}
~~~
