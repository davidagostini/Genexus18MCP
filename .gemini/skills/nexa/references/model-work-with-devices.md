---
name: model-work-with-devices
description: Work With pattern setting for native defining defaults, platforms, labels, and standard action captions
---

Generates or interprets the `WorkWith` for devices configuration file

---

# DEFINITION
The `WorkWith` pattern setting for devices configures global defaults for the [WorkWithDevices object](./object-work-with-devices.md): template defaults, target device platforms, label defaults, and captions for standard CRUD actions

---

# SYNTAX
~~~
PatternSettings WorkWith
{
	WorkWithConfiguration
	{
		<template>
		<platforms>
		<labels>
		<actions>
	}

	#Properties
		<properties>
	#End
}
~~~

Where:
- `<template>`: Defaults for generated objects; see [TEMPLATE](#template) section
- `<platforms>`: One or more `Platform` blocks; see [PLATFORMS](#platforms) section
- `<labels>`: Default label values; see [LABELS](#labels) section
- `<actions>`: Standard action blocks; see [ACTIONS](#actions) section
- `<properties>`: Optional object properties in TOML syntax; see [properties](./properties-model-work-with-devices.md)

---

# TEMPLATE
Configures default behavior for generated native objects

Syntax:
~~~
Template
[
	<properties>
]
~~~

Where:
- `<properties>`: See [TEMPLATE node](./properties-model-work-with-devices.md#template-node) properties

---

# PLATFORMS
Container for one or more `Platform` element blocks

Syntax:
~~~
Platforms
{
	<platform>
	…
}
~~~

Where:
- `<platform>`: One `Platform` element block; see [PLATFORM](#platform) section

---

# PLATFORM
Declares a target device platform available for the native `Work With` pattern

Syntax:
~~~
Platform
[
	<properties>
]
~~~

Where:
- `<properties>`: See [PLATFORM node](./properties-model-work-with-devices.md#platform-node) properties

Rules:
- Omit any property that does not apply to the platform; never set it to an empty string
- Define an OS-wide catch-all entry before device-specific entries for the same OS
- Use `MinimumShortestBound` and `MaximumShortestBound` only for `Os` other than `'Web'`
- Use `MinimumLongestBound` and `MaximumLongestBound` only for `Os = 'Apple'` or `Os = 'Web'` entries
- Never modify or remove entries where `Predefined = 'True'`

---

# LABELS
Configures default label text and positioning used across generated native objects

Syntax:
~~~
Labels
[
	<properties>
]
~~~

Where:
- `<properties>`: See [LABELS node](./properties-model-work-with-devices.md#labels-node) properties

---

# ACTIONS
Container for the four standard CRUD action blocks

Syntax:
~~~
StandardActions
{
	<action>
	…
}
~~~

Where:
- `<action>`: See [ACTION](#action) section

Rules:
- All four actions must be present: `Insert`, `Update`, `Delete`, `Search`

---

# ACTION
Declares the default caption and appearance for one standard CRUD action in the native `Work With` pattern

Syntax:
~~~
<name>
[
	<properties>
]
~~~

Where:
- `<name>`: One of `Insert`, `Update`, `Delete`, `Search` modes
- `<properties>`: See [ACTION node](./properties-model-work-with-devices.md#action-node) properties

Rules:
- Omit `Image` and `DisabledImage` when no image override is needed

---

# OUTPUT
Use [global-output](./global-output.md) with:
- Location: `src/#patternsettings/`
- Main: `WorkWith.gx`

---

# CONSTRAINTS
- Use [global-constraints](./global-constraints.md)
- Exactly one `PatternSettings WorkWith` file exists per Knowledge Base
- Never rename the artifact; the identifier must always be `WorkWith`
- Keep `#Properties` `Description` equal to `"Work With"`
- Define all four standard actions: `Insert`, `Update`, `Delete`, `Search`
- Preserve all predefined Platform entries; do not remove or rename them
- Omit any section whose properties all retain their default values; do not write empty or default-only sections

---

# EXAMPLES

## Example 1
Default WorkWith pattern settings with predefined platforms and standard action captions
~~~
PatternSettings WorkWith
{
	WorkWithConfiguration
	{
		Platforms
		{
			Platform
			[
				Name = 'Any Platform',
				Style = 'UnanimoMobile',
				Predefined = 'True',
				LabelPosition = 'Top'
			]
			Platform
			[
				Name = 'Any Phone',
				DeviceKind = 'Phone or Tablet',
				Size = 'Small',
				Predefined = 'True',
				BoundsName = 'Phone',
				MaximumShortestBound = '599'
			]
			Platform
			[
				Name = 'Any Tablet 7"',
				DeviceKind = 'Phone or Tablet',
				Size = 'Medium',
				Predefined = 'True',
				BoundsName = 'Tablet 7',
				MinimumShortestBound = '600',
				MaximumShortestBound = '719'
			]
			Platform
			[
				Name = 'Any Tablet 10"',
				DeviceKind = 'Phone or Tablet',
				Size = 'Large',
				Predefined = 'True',
				BoundsName = 'Tablet 10',
				MinimumShortestBound = '720'
			]
			Platform
			[
				Name = 'Any TV',
				DeviceKind = 'TV',
				Size = 'Large',
				Predefined = 'True'
			]
			Platform
			[
				Name = 'Any Watch',
				DeviceKind = 'Watch',
				Size = 'Small',
				Predefined = 'True'
			]
			Platform
			[
				Name = 'Any Android Device',
				Os = 'Android',
				Predefined = 'True'
			]
			Platform
			[
				Name = 'Android Phone',
				Os = 'Android',
				DeviceKind = 'Phone or Tablet',
				Size = 'Small',
				Predefined = 'True',
				BoundsName = 'Phone',
				MaximumShortestBound = '599'
			]
			Platform
			[
				Name = 'Android Tablet 7"',
				Os = 'Android',
				DeviceKind = 'Phone or Tablet',
				Size = 'Medium',
				Predefined = 'True',
				BoundsName = 'Tablet 7',
				MinimumShortestBound = '600',
				MaximumShortestBound = '719'
			]
			Platform
			[
				Name = 'Android Tablet 10"',
				Os = 'Android',
				DeviceKind = 'Phone or Tablet',
				Size = 'Large',
				Predefined = 'True',
				BoundsName = 'Tablet 10',
				MinimumShortestBound = '720'
			]
			Platform
			[
				Name = 'Any Apple Device',
				Os = 'Apple',
				Predefined = 'True'
			]
			Platform
			[
				Name = 'iPad',
				Os = 'Apple',
				DeviceKind = 'Phone or Tablet',
				Size = 'Large',
				Predefined = 'True',
				BoundsName = 'iPad',
				MinimumShortestBound = '768'
			]
			Platform
			[
				Name = 'iPhone',
				Os = 'Apple',
				DeviceKind = 'Phone or Tablet',
				Size = 'Small',
				Predefined = 'True',
				BoundsName = 'iPhone',
				MaximumShortestBound = '767'
			]
			Platform
			[
				Name = 'Apple TV',
				Os = 'Apple',
				DeviceKind = 'TV',
				Size = 'Large',
				Predefined = 'True'
			]
			Platform
			[
				Name = 'Apple Watch',
				Os = 'Apple',
				DeviceKind = 'Watch',
				Size = 'Small',
				Predefined = 'True'
			]
			Platform
			[
				Name = 'Any Web Screen',
				Os = 'Web',
				Style = 'UnanimoAngular',
				Predefined = 'True'
			]
			Platform
			[
				Name = 'Web Phone',
				Os = 'Web',
				DeviceKind = 'Phone or Tablet',
				Size = 'Small',
				Predefined = 'True',
				BoundsName = 'Web Phone'
			]
			Platform
			[
				Name = 'Web Small',
				Os = 'Web',
				Size = 'Medium',
				Predefined = 'True',
				BoundsName = 'Web Small',
				MaximumLongestBound = '719'
			]
			Platform
			[
				Name = 'Web Desktop',
				Os = 'Web',
				Size = 'Large',
				Predefined = 'True',
				BoundsName = 'Web Desktop',
				MinimumLongestBound = '720',
				MaximumLongestBound = '1199'
			]
			Platform
			[
				Name = 'Web Big Screen',
				Os = 'Web',
				Size = 'Large',
				Predefined = 'True',
				BoundsName = 'Web Big Screen',
				MinimumLongestBound = '1200'
			]
		}
		StandardActions
		{
			Insert
			[
				Caption = 'GXM_insert'
			]
			Update
			[
				Caption = 'GXM_update'
			]
			Delete
			[
				Caption = 'GX_BtnDelete'
			]
			Search
			[
				Caption = 'GX_BtnSearch'
			]
		}
	}

	#Properties
		Description = "Work With"
	#End
}
~~~

Saved as:
~~~
src/#patternsettings/WorkWith.gx
~~~
