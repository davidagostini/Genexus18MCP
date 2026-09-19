---
name: object-super-app
description: Super app host definition with Mini App API exposure
---

Defines a native mobile `Super App` host, its Mini App provisioning metadata, and the API exposed to Mini Apps

---

# DEFINITION
A `Super App` object designates the host application, connects it to a `Mini App Center`, and exposes services callable by `Mini Apps` programs

Main goals:
- Mark one mobile `Main Object` as the `Super App` host
- Identify the `Super App` in the `Mini App Center` and resolve published `Mini Apps` programs

Relationship with [Panel](./object-panel.md):
- The `Main Object` property can reference a mobile `Panel`, `Menu`, or `Work With` objects
- The `Panel` objects can be both:
	* Rhe `Main Object` of the `Super App`
	* Implementations of exposed contract
- Keep exposed `Panel` parameters and mobile flow aligned with the `Mini App` invocation contract

Related object contracts:
- Source syntax follows the same delegation model as [API object](./object-api.md)
- Service implementations may reference `Panel`, `Procedure`, or `Data Provider` objects

---

# SYNTAX
~~~
SuperApp <name>
{
	<source>

	#Events
		<events>
	#End

	#Variables
		<variables>
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
- `<source>`: Definition source; see [SOURCE](#source) section
- `<events>`: Event handlers executed around service calls; see [EVENTS](#events)
- `<variables>`: Variable definitions with mandatory `DataType`
- `<properties>`: Optional object properties in TOML syntax; see [properties](./properties-object-super-app.md)
- `<documentation>`: Optional object documentation; see [markdown](./common-markdown.md)

---

# SOURCE
Defines the contract exposed by the `Super App` to connected `Mini Apps` programs

Syntax:
~~~
<group>
{
	<services>
}
~~~

Where:
- `<group>`: Logical service group name
- `<services>`: Exposed service definition list; see [SERVICE](#service) section

Notes:
- Keep source notation aligned with [API object](./object-api.md#service)
- Use one or more logical groups to organize exposed capabilities

---

# SERVICE
Defines services exposed by the `Super App` program

Syntax:
~~~
<name>(<parameters>)
	=> <implementation>(<arguments>);
~~~

Where:
- `<name>`: Exposed service name
- `<parameters>`: Comma-separated variable parameters with operator (`in`, `out`, `inout`)
- `<implementation>`: Internal implementation object; use `Panel`, `Procedure`, or `Data Provider`
- `<arguments>`: Comma-separated variables or constants passed to the implementation

Notes:
- Keep one implementation call per service contract
- Match implementation signature with the declared parameter contract

---

# EVENTS
Defines hooks executed before and after service execution

Allowed event names:
- `Before`
- `<service>.Before`
- `<service>.After`
- `After`

Execution sequence:
1. `Before`
2. `<service>.Before`
3. Service implementation call
4. `<service>.After`
5. `After`

Notes:
- Use `Before` and `After` for cross-service orchestration
- Use `<service>.Before` and `<service>.After` for service-specific logic

Example:
~~~
Event GetPaymentMethods.Before
	&MiniAppId = GeneXusSuperApps.MiniApps.CurrentMiniAppId
EndEvent
~~~

---

# OUTPUT
Use [global-output](./global-output.md)

---

# CONSTRAINTS
- Use [global-constraints](./global-constraints.md)
- Use `Android` and `Apple` generators only
- Set `Main Object` to one mobile main object: `Panel`, `Menu`, or `Work With`
- Use `Panel`, `Procedure`, or `Data Provider` as service implementations only
- Add invoked source objects to `Additional References` of the associated `Main Object`
- Expose only services needed by `Mini Apps` programs
- Keep `SDT` parameters used by exposed `Data Provider` services inside the same module
- Keep event code orchestration-oriented; avoid unrelated business duplication in hooks
- Keep these properties aligned with the target `Mini App Center` specification:
	* `Super App Identifier`
	* `Super App Version`
	* `Provisioning Url`

---

# CONVENTIONS
- Prefer a mobile `Panel` as `Main Object` when the host starts on a screen
- Group services by business capability, not by implementation object
- Expose stable contracts to `Mini App` developers and evolve them version by version
- Use cache properties to control downloaded `Mini App` retention explicitly

---

# EXAMPLES

## Example 1
Super App exposing a payment panel
~~~
SuperApp VerdantBank
{
	Payment
	{
		NewPayment(in:&ExternalReference, in:&Amount, out:&Success, out:&PaymentId)
			=> PaymentPanel(&ExternalReference, &Amount, &Success, &PaymentId);
	}

	#Variables
		ExternalReference [ DataType = 'VarChar(80)' ]
		Amount [ DataType = 'Numeric(12.2)' ]
		Success [ DataType = 'Boolean' ]
		PaymentId [ DataType = 'Numeric(10.0)' ]
	#End

	#Properties
		MainObject = "HomePanel"
		SuperAppIdentifier = "verdant-bank"
		SuperAppVersion = "1.0.0"
		ProvisioningUrl = "https://miniapp-center.example.com"
	#End
}
~~~

## Example 2
Super App exposing procedure and data provider services
~~~
SuperApp CommerceHost
{
	Customer
	{
		GetUserInformation(in:&UserName, out:&UserAddress, out:&UserPhone)
			=> GetUserInfoProc(&UserName, &UserAddress, &UserPhone);

		GetUserInformationDP(in:&UserName, out:&UserInfoSDT)
			=> GetUserInfoDP(&UserName, &UserInfoSDT);
	}
}
~~~
