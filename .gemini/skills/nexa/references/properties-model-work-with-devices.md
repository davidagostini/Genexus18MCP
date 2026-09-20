---
name: properties-model-workwithdevices
description: Configurable WorkWith pattern settings properties for native devices
---

Use this file to select editable properties, defaults, and valid options for the `WorkWith` (devices) pattern settings

---

# GENERAL

## Description
Human-readable label for the description attribute
- Type: `string`

---

# TEMPLATE node

## TabsForParallelTransactions
When `true`, adds a section to the detail for each parallel transaction instead of a single view
- Type: `boolean`
- Default: `False`

---

# PLATFORM node

## Name
Unique name
- Type: `string`

## Os
Target operating system for this platform entry
- Type: `enum{Any Platform,Android,Apple,Windows,Web}`
- Default: `Any Platform`
- Options:
	* `Any Platform`: Applies to all operating systems
	* `Android`: Android devices
	* `Apple`: iOS / iPadOS / tvOS / watchOS devices
	* `Windows`: Windows devices
	* `Web`: Web browser targets

## Version
Deployment unit version used by release and deployment pipelines
- Type: `string`

## DeviceKind
Device category this platform entry targets
- Type: `enum{Phone or Tablet,TV,Watch}`
- Options:
	* `Phone or Tablet`: Smartphones and tablets
	* `TV`: Smart TV devices
	* `Watch`: Wearable watch devices

## Size
Responsive size class assigned to this platform entry
- Type: `enum{Small,Medium,Large}`
- Options:
	* `Small`: Phone-sized screens
	* `Medium`: 7-inch tablet-sized screens
	* `Large`: 10-inch tablet, desktop, or TV screens

## Style
Name of a `DesignSystem` object used for styling
- Type: `string`

## AdditionalThemes
Comma-separated list of additional design system styles applied on top of the primary style for this platform
- Type: `string`

## NavigationStyle
Navigation transition style used for this platform
- Type: `enum{Default,Flip,Split,Cascade,Slide}`
- Default: `Default`
- Options:
	* `Default`: Standard slide navigation
	* `Flip`: Flip-card navigation transition
	* `Split`: Split-view (master-detail) navigation
	* `Cascade`: Cascading navigation
	* `Slide`: Sliding-menu navigation

## DefaultLayoutOrientation
Default screen orientation for generated objects on this platform
- Type: `enum{Any,Landscape,Portrait}`
- Default: `Any`
- Options:
	* `Any`: No orientation constraint
	* `Landscape`: Force landscape orientation
	* `Portrait`: Force portrait orientation

## Predefined
Marks this platform entry as predefined by the pattern; predefined entries must not be removed or renamed
- Type: `boolean`
- Default: `False`

## BoundsName
Identifier for the responsive bounds group this platform entry belongs to; omit if not applicable
- Type: `string`

## MinimumShortestBound
Minimum shortest screen dimension in dp that qualifies for this platform entry; omit if unbounded
- Type: `string`

## MaximumShortestBound
Maximum shortest screen dimension in dp that qualifies for this platform entry; omit if unbounded
- Type: `string`

## MinimumLongestBound
Minimum longest screen dimension in dp that qualifies for this platform entry; omit if unbounded
- Type: `string`

## MaximumLongestBound
Maximum longest screen dimension in dp that qualifies for this platform entry; omit if unbounded
- Type: `string`

## LabelPosition
Default attribute label position for generated controls
- Type: `enum{None,Left,Top,Right,Bottom,Float,Platform Default}`
- Options:
	* `None`: No label rendered
	* `Left`: Label to the left of the control
	* `Top`: Label above the control
	* `Right`: Label to the right of the control
	* `Bottom`: Label below the control
	* `Float`: Floating placeholder label
	* `Platform Default`: Inherit from the platform default

---

# LABELS node

## GeneralTab
Caption for the general tab in the detail view
- Type: `string`
- Default: `General`

## WorkWithTitle
Title format for the Work With selection screen; use `<Object>` as a placeholder for the transaction name
- Type: `string`
- Default: `Work With <Object>`

## DetailDescription
Description format for the detail view; use `<Object>` as a placeholder for the transaction name
- Type: `string`
- Default: `<Object> Information`

## LabelPosition
Default attribute label position for generated controls
- Type: `enum{None,Left,Top,Right,Bottom,Float,Platform Default}`
- Options:
	* `None`: No label rendered
	* `Left`: Label to the left of the control
	* `Top`: Label above the control
	* `Right`: Label to the right of the control
	* `Bottom`: Label below the control
	* `Float`: Floating placeholder label
	* `Platform Default`: Inherit from the platform default

---

# ACTION node

## Caption
Human-readable label for various UI elements
- Type: `string`

## DefaultMode
Include this action in the generated object by default
- Type: `boolean`
- Default: `True`

## Image
Image displayed on action button or when selected
- Type: `string`

## DisabledImage
Image resource shown when the action is disabled; omit if not applicable
- Type: `string`
