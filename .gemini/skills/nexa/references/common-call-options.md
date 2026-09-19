---
name: common-call-options
description: Runtime navigation modifiers for UI object invocations
---

Runtime properties configured on the caller to modify navigation behavior at invocation time

---

# DEFINITION
Runtime API that configures navigation behavior when calling a UI object

---

# SYNTAX
~~~
<object>.CallOptions.<property> = <value>
<object>([<arg-1>, …, <arg-n>])
~~~

Where:
- `<object>`: Invocable UI object name
- `<property>`: Property name; one of:
	* `Type`: Call stack behavior
		- Domain: `CallType, GeneXus` (built-in)
		- Default: `Push`
	* `EnterEffect`, `ExitEffect`: Enter/exit animation
		- Domain: `Effect, GeneXus` (built-in)
		- Default: `Default`
	* `Target`: Navigation target region
		- Domain: `CommonCallTarget, GeneXus` (built-in)
		- Default: `Content`
	* `TargetSize`: Popup overlay size
		- Domain: `CallTargetSize, GeneXus` (built-in)
		- Default: `Large`
	* `TargetWidth`, `TargetHeight`: Popup overlay width/height
- `<value>`: Value for the given property
- `<arg-i>`: Argument matching the `parm` rule

---

# CONSTRAINTS
- Use [global-constraints](./global-constraints.md)
- Never assign `CallOptions` after the invocation statement
- Never set `Target` when `Type` has `CallType.Popup` value
- Only set `Target` in web when `MasterPage` exists in the calling object
- Always place call as last statement when `Type` has `CallType.Replace` value
- Only assign `TargetWidth` and `TargetHeight` in native environment; always use `dip` values
- Only assign `TargetSize`, `TargetHeight`, `TargetWidth` when `Type` has `CallType.Popup` value
- Only assign `EnterEffect` and `ExitEffect` when overriding `Form` transitions (native-only)
- Never use `EnterEffect` or `ExitEffect` with Apple-specific values in cross-platform code
- Consider `EnterEffect` from the entering object and `ExitEffect` from the leaving object
- Always show/hide `Target` values using:
	* `Navigation, GeneXus.Common.UI` methods
	* `CommonCallTarget, GeneXus` values as arguments
- Always restrict `Target` values by navigation style:
	* `Split`: Use `Left` or `Content` only
	* `Slide`: Use `Left`, `Right`, or `Content` only

---

# EXAMPLES

## Example 1
Push navigation with slide transition
~~~
Event 'OpenOrders'
	OrderList.CallOptions.Type = CallType.Push
	OrderList.CallOptions.EnterEffect = Effect.SlideLeft
	OrderList.CallOptions.ExitEffect = Effect.SlideRight
	OrderList()
EndEvent
~~~

## Example 2
Replace navigation to release current object from stack
~~~
Event 'GoHome'
	HomePanel.CallOptions.Type = CallType.Replace
	HomePanel()
EndEvent
~~~

## Example 3
Popup with fixed size
~~~
Event 'ShowConfirm'
	ConfirmDialog.CallOptions.Type = CallType.Popup
	ConfirmDialog.CallOptions.TargetSize = CallTargetSize.Small
	ConfirmDialog(&OrderId)
EndEvent
~~~

## Example 4
Split navigation targeting content region
~~~
Event GridCustomers.ItemClick
	CustomerDetail.CallOptions.Target = CommonCallTarget.Content
	CustomerDetail(&CustomerId)
EndEvent
~~~

## Example 5
Slide navigation with right drawer
~~~
Event 'ShowFilters'
	FilterPanel.CallOptions.Target = CommonCallTarget.Right
	FilterPanel()
	Navigation.ShowTarget(CommonCallTarget.Right)
EndEvent
~~~
