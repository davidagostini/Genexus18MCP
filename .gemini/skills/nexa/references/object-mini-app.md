---
name: object-mini-app
description: Native mini app metadata and Super App API binding definition
---

Defines one native mobile Mini App and its integration contract with a `Super App`

---

# DEFINITION
A `Mini App` object designates a native mobile mini app entry point plus `Super App` integration settings

Purpose:
- Marks one mobile `Main Object` as the `Mini App` entry point
- Binds `Mini App` with a mock `Super App` contract for local testing when needed
- Binds `Mini App` with an `External Object` contract for non-GeneXus `Super Apps` when needed

Relationship with [Panel](./object-panel.md):
- The `Main Object` property can reference a mobile `Panel`, `Menu`, or `Work With` objects
- Keep `Panel` objects aligned with [Panel object](./object-panel.md) mobile constraints

---

# SYNTAX
~~~
MiniApp <name>
{
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
- `<properties>`: Optional object properties in TOML syntax; see [properties](./properties-object-mini-app.md)
- `<documentation>`: Optional object documentation; see [markdown](./common-markdown.md)

Notes:
- Applies to native mobile mini apps
- Object does not have source, events, variables, or layout sections

---

# OUTPUT
Use [global-output](./global-output.md)

---

# CONSTRAINTS
- Use [global-constraints](./global-constraints.md)
- Use `Android` and `Apple` generators only
- Set `Main Object` to one mobile main object: `Panel`, `Menu`, or `Work With`
- Keep `Main Object` aligned with the real `Mini App` entry flow
- Use these properties only when applicable and keep aligned with the target `Super App` contract:
	* `Super App API Mock`: Local testing against a mock `Super App` program
	* `Super App API External Object`: Target `Super App` not implemented in GeneXus

---

# CONVENTIONS
- Use one `Mini App` object per `Mini App `entry point
- Prefer a dedicated mobile `Panel` as `Main Object` when the `Mini App` starts on a screen
- Keep mock configuration isolated from production integration settings

---

# EXAMPLES

## Example 1
Native Mini App using a mobile panel as entry point
~~~
MiniApp CoffeeMiniApp
{
	#Properties
		MainObject = "CoffeeHome"
	#End
}
~~~

## Example 2
Native Mini App using a mock Super App API during development
~~~
MiniApp CoffeeMiniApp
{
	#Properties
		MainObject = "CoffeeHome"
		SuperAppAPIMock = "SuperAppVerdantBankMock"
	#End
}
~~~

## Example 3
Native Mini App targeting a non-GeneXus Super App through an external object
~~~
MiniApp PaymentsMiniApp
{
	#Properties
		MainObject = "Payments"
		SuperAppAPIExternalObject = "Payments"
	#End
}
~~~
