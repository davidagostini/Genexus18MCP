---
name: frontend-design
description: UI/UX design intelligence for GeneXus
---

Complete UI/UX design reference for building professional, accessible, and token-driven interfaces in GeneXus applications

---

# GLOSSARY
Design terminology used throughout this reference:
- *Token*: A named, reusable design variable (color, spacing, size, duration)
- *Primitive token*: A raw palette value with no semantic meaning; e.g. `brand-500: #3b82f6`
- *Semantic token*: Role-based token mapping meaning to a primitive; e.g. `action-primary: $brand-500`
- *Component token*: A style rule that binds a semantic token to a specific control state
- *Design system*: The unified token, style, and visual contract shared across all screens
- *Visual hierarchy*: The perceived order of importance established through size, contrast, spacing
- *Surface*: A visual layer or container element; e.g. a card, panel, or sheet
- *Elevation*: The perceived depth of a surface; shadow (Web) or elevation (Native)
- *Spacing scale*: A base-unit multiplier system that produces all margin, padding, and gap values
- *Breakpoint*: A screen-width threshold at which layout adapts to a new configuration
- *CTA (call-to-action)*: The primary interactive element driving the most important task on screen
- *Progressive disclosure*: Show primary content first; defer secondary or contextual content
- *Skeleton*: A placeholder layout mirroring real content dimensions while data loads
- *Contrast ratio*: Luminance difference between foreground and background; measured per WCAG 2.2
- *Focus ring*: A visible outline on a focused interactive control; required for keyboard nav (Web)
- *Touch target*: The tappable hit area of a control; measured in density-independent pixels
- *Easing*: A timing function controlling the acceleration curve of a CSS transition
- *Micro-interaction*: A small, purposeful animation providing immediate feedback after user action
- *BEM (Block Element Modifier)*: A CSS naming convention; `.block`, `.block__element`, `.block--modifier`
- *GXML (GeneXus Markup Language)*: The XML-based layout language used in GeneXus `#Layout` sections
- *DSO (Design System Object)*: Holds tokens (`#DesignTokens`) and style classes (`#DesignStyles`)

---

# PURPOSE
Apply this reference whenever designing, reviewing, or generating:
- Screen layouts in `Panel`, `WebPanel`, `MasterPage`, `MasterPanel`, `WebComponent`, or `Stencil` objects
- Visual styling in `DesignSystem` objects
- Design tokens, component states, responsive breakpoints, or interaction patterns

Key concepts:
- GXML is analogous to HTML; `#DesignTokens` to CSS custom properties; `#DesignStyles` to CSS classes
- Web generators support full CSS; Native supports a bounded subset
- GeneXus-specific style properties are `gx-*` and implemented on top of standard CSS or native code

See also:
- [Frontend layout](frontend-layout.md)
- [Frontend styles](frontend-styles.md)
- [Design System object](object-design-system.md)
- [Panel/WebPanel/Stencil object](object-panel.md)

---

# DESIGN PRINCIPLES
Six non-negotiable constraints for every screen and style decision:
- *Clarity*: Interface must be understandable at a glance; hierarchy communicates what matters most
- *Consistency*: Similar elements look and behave identically; tokens are the single source of truth
- *Predictability*: Interactions match expectations; navigation is stable; feedback is immediate
- *Legibility*: Text reads under real conditions; small screens, bright light, accessibility settings
- *Resilience*: UI survives longer text, localization, zoom, large-text, dark mode, and low-vision
- *Meaningful restraint*: Every visual element earns its place; fewer strong decisions beat many weak

---

# VISUAL HIERARCHY
Visual hierarchy communicates priority, grouping, and interactivity through visual contrast

Mechanisms:
- *Size*: Larger elements draw attention first
- *Contrast*: Higher contrast signals importance; lower contrast recedes
- *Weight*: Heavier font weight carries more visual priority than regular weight
- *Spacing*: Generous whitespace groups related items and separates unrelated ones (proximity law)
- *Alignment*: Consistent alignment creates reading flow; deliberate breaks create emphasis
- *Grouping*: Elements close together are perceived as related; use `surface` and `surface-elevated`
- *Iconography*: Icons reinforce meaning when paired with labels; never rely on icons alone
- *Motion*: Reserve for state changes, panel entry, and user feedback; never for decoration

Rules:
- Keep one dominant CTA per screen section
- Establish weight order: page title, section headers, body copy, captions
- Never create hierarchy with color alone; combine size, weight, and spacing
- Eliminate visual noise competing with task completion

---

# TYPOGRAPHY
Typography maps directly to CSS font properties in `#DesignStyles` region for `DesignSystem` object
- Register font files as `File` objects and declare them with `@font-face` rule
- Native supports only `font-family`, `font-size`, `font-weight`, `color`, `text-align`, and `text-overflow`

## Type Roles
Define a small set of reusable type roles; map each to a token; apply consistently:
- `.text-display`: Hero headings, splash screens; display face, Bold
- `.text-title`: Screen-level headings; Bold or Semibold
- `.text-subtitle`: Section headings, card titles; Semibold
- `.text-body`: Primary readable content; Regular
- `.text-label`: Form labels, captions on controls; Medium
- `.text-caption`: Supporting text, metadata, timestamps; Regular, smaller

## Type Scale
Use a proportional scale; `px` for Web, `dip` for Native:
- `fontSizes.display`: 40px / 40dip, line-height 1.2, weight 700
- `fontSizes.title`: 24px / 24dip, line-height 1.3, weight 600
- `fontSizes.subtitle`: 18px / 18dip, line-height 1.4, weight 600
- `fontSizes.body`: 16px / 16dip, line-height 1.5, weight 400
- `fontSizes.body-sm`: 14px / 14dip, line-height 1.5, weight 400
- `fontSizes.label`: 14px / 14dip, line-height 1.4, weight 500
- `fontSizes.caption`: 12px / 12dip, line-height 1.3, weight 400

Rules:
- Minimum body text: `16px` on Web and `16dip` on Native
- Never use body text below `12px`/`12dip`
- Body line-height: `1.5` to `1.75`
- Ideal line length: `60` to `75` characters on desktop, `35` to `60` on mobile
- Headings must differ by size, weight, spacing, and placement; never by color alone
- Use tabular figures for dashboards, pricing, analytics, and financial data
- Limit to two type families maximum; prefer a single UI sans-serif
- Never tighten letter-spacing on small text
- Never use ultra-light font weights for body copy

## Font Declaration
Register custom fonts in `#DesignStyles` region for `DesignSystem` object
- Declare them with `@font-face` role
- Reference `File` object with font file content via `gx-file()` function

Example:
~~~
@font-face
{
	src: gx-file(MyModule.MyFontFile_ttf);
	font-family: MyFont;
	font-weight: 400;
	font-style: normal;
	font-display: swap;
};

@font-face
{
	src: gx-file(MyModule.MyFontFileBold_ttf);
	font-family: MyFont;
	font-weight: 700;
	font-style: normal;
	font-display: swap;
};
~~~

## Font Files
Integration process
- Accept only `.ttf`/`.otf`/`.eot`; reject `.woff`/`.woff2`/`.ttc`/`.otc`
- Select font files
	* Use user‑provided files immediately when supplied
	* Use Google Fonts otherwise:
		- Discover fonts with `curl -s "https://api.github.com/repos/google/fonts/git/trees/main?recursive=1"`
		- Download files with `curl -LO https://raw.githubusercontent.com/google/fonts/main/<path>/<file>.ttf`
- Create a `File` object for each font file
- Define a `@font-face` rule for each `File` object

---

# ICONOGRAPHY
GeneXus does not use font-based icons; icons are `Image` objects stored in the KB

## Image as Icon
Define each icon as an `Image` object
- Organize asset variants by density and optionally by style variant
- GeneXus selects the appropriate variant at runtime

Asset formats:
- `SVG`: Preferred for Web; infinitely scalable; no density variants required
- `PNG`: Required for Native; provide `1x` (mdpi), `2x` (xhdpi), and `3x` (xxhdpi) variants

## Icon Usage
Three ways to include an icon:
- Image control: Insert an `image` element in GXML; assign the Image object via `ImageObject`
- Image attribute/variable: An `Image`-typed attribute or variable renders as an image
- Style background: Use `gx-image(<image-name>)` in `background-image` in a style class

## Icon Sizing
Set icon dimensions via style class only; never hardcode dimensions in GXML attributes:
- Web: `width` and `height` in `px`
- Native: `width` and `height` in `dip`; minimum `24dip` standard, `44dip` for touch targets

Define icon size tokens for reuse:
- `icon-sm`: Use `16px` / `16dip`; compact inline glyphs
- `icon-md`: Use `24px` / `24dip`; default icon size
- `icon-lg`: Use `32px` / `32dip`; prominent standalone icons

## Icon Style
Rules for visual consistency across all icons in the product:
- Use one icon family only; never mix sets from different libraries
- Keep stroke width uniform within the same visual layer; e.g., always `1.5px` or always `2px`
- Filled style = active/selected; Outline style = default/inactive; never mix at the same level
- Never use emoji as icons: they are font-dependent, unthemeable, and platform-inconsistent
- Align icons to the text baseline; keep padding consistent around icon and label
- Icon contrast: `3:1` minimum for UI glyphs; `4.5:1` for small decorative icons (WCAG)
- Press states must use opacity/color/elevation change; never shift layout bounds on press

---

# COLOR TOKENS
Use a three-layer token architecture in `#DesignTokens` region of `DesignSystem` object
- Never hardcode color values in `#DesignStyles` region
- Always reference color tokens via `$colors.<token-name>` syntax

Layers:
- *Primitive*: Raw palette values; never used directly in style classes
- *Semantic*: Role-based tokens mapping meaning to primitives, scoped per theme variant
- *Component*: Style classes in `#DesignStyles` region consuming semantic tokens

## Primitive Tokens
Use only as a structural template
- Replace `brand-*` placeholders with the actual brand palette before use
- Reuse neutral and feedback primitives across applications when possible

Example:
~~~
#colors
{
	// Neutrals: stable across most applications
	gray-0: #ffffff;
	gray-50: #f8fafc;
	gray-100: #f1f5f9;
	gray-200: #e2e8f0;
	gray-400: #94a3b8;
	gray-600: #475569;
	gray-700: #334155;
	gray-900: #0f172a;

	// Brand: replace with the actual brand palette; examples below are illustrative only
	brand-50: <lightest-brand-tint>;
	brand-500: <primary-brand-color>;
	brand-600: <medium-dark-brand>;
	brand-700: <darkest-interactive-brand>;

	// Feedback: adjust hues to brand; these are universal semantic anchors
	green-500: #22c55e;
	green-700: #15803d;
	red-500: #ef4444;
	red-700: #b91c1c;
	amber-500: #f59e0b;
	amber-700: #b45309;
}
~~~

## Semantic Tokens
Define theme-specific values using `@<arg-name> = <arg-value>` conditional syntax

Example:
~~~
tokens MyAppTokens(color-scheme:[light]|dark)
{
	@color-scheme = light
	{
		#colors
		{
			// Surfaces
			page-bg: $gray-50;
			surface-1: $gray-0;
			surface-2: $gray-100;
			surface-overlay: rgba(15, 23, 42, 0.48);

			// Text
			text-primary: $gray-900;
			text-secondary: $gray-600;
			text-inverse: $gray-0;
			text-disabled: $gray-400;

			// Borders
			border-default: $gray-200;
			border-strong: $gray-600;
			border-focus: $brand-500;
			border-focus-ring: rgba($brand-500, 0.40);

			// Actions
			action-primary: $brand-500;
			action-primary-hover: $brand-700;
			action-secondary: $brand-50;

			// Feedback
			state-success: $green-500;
			state-success-bg: rgba($green-500, 0.10);
			state-error: $red-500;
			state-error-bg: rgba($red-500, 0.10);
			state-warning: $amber-500;
			state-warning-bg: rgba($amber-500, 0.10);
		}
	}

	@color-scheme = dark
	{
		#colors
		{
			// Surfaces: use darker, brand-tinted values; examples are illustrative anchors
			page-bg: <darkest-page-bg>;
			surface-1: <dark-card-bg>;
			surface-2: <dark-raised-bg>;
			surface-overlay: rgba(0, 0, 0, 0.64);

			// Text: invert; never reuse light-mode text tokens directly
			text-primary: <lightest-on-dark>;
			text-secondary: <muted-on-dark>;
			text-inverse: $gray-900;
			text-disabled: <disabled-on-dark>;

			// Borders: use subtle values; same focus tokens as light mode
			border-default: <dark-border>;
			border-strong: <strong-border-on-dark>;
			border-focus: $brand-500;
			border-focus-ring: rgba($brand-500, 0.40);

			// Actions: brand color typically stable; hover uses lighter brand step
			action-primary: $brand-500;
			action-primary-hover: $brand-600;
			action-secondary: rgba($brand-500, 0.15);

			// Feedback: same hues; increase alpha for bg tokens in dark contexts
			state-success: $green-500;
			state-success-bg: rgba($green-500, 0.12);
			state-error: $red-500;
			state-error-bg: rgba($red-500, 0.12);
			state-warning: $amber-500;
			state-warning-bg: rgba($amber-500, 0.12);
		}
	}
}
~~~

Rules:
- Never use raw hex values in `#DesignStyles` region style classes
- Always reference colors with `$colors.<token>` syntax in style classes
- Always define foreground and background token pairs explicitly for every surface
- Always verify contrast in both light and dark modes
- Never invert colors blindly for dark mode; create deliberate foreground/background pairs
- Never use red/green alone for charts or statuses; combine with shape, label, or icon
- Dark mode surfaces use desaturated, tonal variants of the brand palette; never simple inversion
- Secondary text (`text-secondary`) must meet `3:1` contrast on dark surfaces (not just `4.5:1`)
- Design light and dark variants together; dark mode is not an afterthought or a post-process
- Feedback colors (`state-error`, `state-success`) must meet `4.5:1` contrast on their backgrounds
- Dividers and borders must be visible in both light and dark themes; define `border-default` per theme
- Scrim behind modals and drawers: `rgba(0, 0, 0, 0.48)` minimum; range `0.40–0.60` opacity

---

# SPACING SCALE
Use a 4-point base scale; define in `#spacing` group:

~~~
#spacing
{
	xxs: 4px;	// Micro gaps, icon padding
	xs: 8px;	// Tight padding, badge offsets
	sm: 12px;	// Inner cell padding
	md: 16px;	// Standard component padding
	lg: 24px;	// Section spacing, card padding
	xl: 32px;	// Major section separation
	xxl: 48px;	// Page-level spacing
	xxxl: 64px;	// Hero-level spacing
}
~~~

Rules:
- Reference `$spacing.<token>` in all style classes; never hardcode pixel values
- Use `dip` values for Native styles; `px` for Web
- Keep density consistent; never mix tight and loose density in the same screen section
- Use `$spacing.md` as baseline cell padding; scale up to `lg` for card containers

---

# ELEVATION
Elevation communicates surface stacking and visual depth; mechanism differs by platform

## Web Elevation
Define elevation using `box-shadow` in the `#shadows` token group:

~~~
#shadows
{
	none: none;
	low:
		0 1px 3px rgba(0, 0, 0, 0.08),
		0 1px 2px rgba(0, 0, 0, 0.06);
	medium:
		0 4px 12px rgba(0, 0, 0, 0.10),
		0 2px 4px rgba(0, 0, 0, 0.06);
	high:
		0 10px 28px rgba(0, 0, 0, 0.14),
		0 4px 8px rgba(0, 0, 0, 0.08);
	overlay:
		0 20px 48px rgba(0, 0, 0, 0.20),
		0 8px 16px rgba(0, 0, 0, 0.12);
}
~~~

Usage:
- `.surface`: Use `$shadows.low`; standard cards and containers
- `.surface-elevated`: Use `$shadows.medium`; prominent cards, popovers, inline panels
- `.surface-overlay`: Use `$shadows.overlay`; modals, drawers, floating sheets

Rules:
- Apply shadow transitions on hover using `$times.normal` easing to signal interactivity
- Never apply `$shadows.high` to static non-interactive surfaces
- In dark mode, use lighter surface colors instead of heavier shadows for depth

## Native Elevation
Use `gx-elevation: <integer>` instead of `box-shadow` property on Native
- Values follow the Material Design depth model
- Requires an opaque control background
- Ensure the control and its shadow fit within the parent cell

Example:
~~~
.surface-elevated
{
	gx-elevation: 4;
}
~~~

Material Design elevation reference for `gx-elevation`:
- 1: Resting card
- 2: Raised card
- 4: App bar, raised interactive surface
- 6: Floating action button resting
- 8: Menu, popover
- 12: Drawer
- 16: Modal dialog

Separate `box-shadow` and `gx-elevation` into different style classes
- Useful when the same `DesignSystem` object targets both Web and Native
- Define Web and Native variants when both targets are required

---

# BORDER RADIUS
Define corner rounding in `#radius`:

~~~
#radius
{
	none: 0px;
	xs: 4px;
	sm: 6px;
	md: 10px;
	lg: 16px;
	xl: 24px;
	pill: 9999px;
}
~~~

Usage:
- `$radius.sm`: Icon buttons, badges, chips, tags
- `$radius.md`: Standard buttons, input fields, small cards
- `$radius.lg`: Large cards, sheet containers, surface blocks
- `$radius.xl`: Modal containers, drawer panels
- `$radius.pill`: Toggle pills, avatar containers, full-rounded CTAs

---

# ACCESSIBILITY
Every screen must pass all checks before completion

## Contrast
- *Normal text* (below 18px / 14px bold): Minimum `4.5:1`
- *Large text* (18px or above / 14px bold or above): Minimum `3:1`
- *Non-text UI elements* (borders, icons, controls): Minimum `3:1`
- *Enhanced target* (AAA) where feasible: `7:1`

Always define foreground/background token pairs explicitly; verify in both light and dark modes

## Keyboard Focus
- Implement logical tab order following visual flow
- Define visible focus ring (minimum 3px) on all interactive controls; use `$colors.border-focus-ring`, e.g.:
	~~~
	.button-primary:focus-visible
	{
		box-shadow: 0 0 0 3px $colors.border-focus-ring;
		outline: none;
	}
	~~~
- Ensure all controls are activatable by keyboard (Enter, Space, Arrow keys as appropriate)
- Provide escape/cancel routes for all overlays and modal surfaces
- Eliminate keyboard traps

## Tab Order
- Follow `#Layout` declaration order for tab traversal; no tab-order property exists
- Reorder `#Layout` elements to change tab sequence; never use CSS ordering; `order`, `position`, grid/flex placement
- Hide controls with `visible="False"` to remove them from traversal
- Disable controls with `enabled="False"` to skip them during traversal
- Wrap decorative interactive elements in `UserControl` with native `tabindex="-1"` to remove them from tab flow
- Verify traversal manually; fix mismatches by changing `#Layout` order, never CSS

## Touch Targets
- Minimum touch target: `48x48dip`
- Minimum gap between adjacent touch targets: `8dip`

## Labels
- Every input control must have a visible label or explicit `caption` property
- Every button `caption` must describe the action with a verb: "Save Order", never "OK"
- Avoid placeholder-only labels for input fields; placeholders disappear on focus
- Avoid icon-only buttons without accompanying text or accessible caption
- Never communicate status with color alone; always combine with text, icon, or pattern

## Accessible Names
Provide programmatic labels for controls that have no visible text:
- Set `accessibleName` attribute; mandatory when `labelPosition="None"`
- Use `accessibleNameCustom` attribute for label text directly

## Screen Reader Order
- On Web, visual reading order must match the DOM/focus traversal order
- On Native, GeneXus reads controls in layout order; arrange GXML elements accordingly
- Modals and overlays must trap focus within their boundary; restore focus on close (Web)
- After route change, move focus to the main content heading or landmark (Web)

## Heading Hierarchy (Web)
- Use `h1`→`h6` sequentially; never skip levels
- One `h1` per page; maps to the page title (`text-display` or `text-title`)
- Section headings use `h2`
- card/subsection headings use `h3`

## Skip Links (Web)
- Add a visually-hidden skip-to-main link as the first focusable element on every page:
  ~~~
  .skip-link { position: absolute; top: -100%; left: 0; }
  .skip-link:focus { top: 0; }
  ~~~

## Dynamic Type (Native)
- Use `gx-font-category` property on style classes targeting `text-body` and readable roles
- Leave `font-size` empty (`0dip`) to allow the OS to apply the user's preferred text size
- For custom fonts, specify a base size; the OS scales from that anchor for accessibility

## Reduced Motion
- Use motion only for state changes, continuity, or feedback
- Keep all transitions under `360ms` total duration
- Respect `prefers-reduced-motion`; keep transitions at `$times.fast` or remove entirely
- Never use motion that blocks task completion or distracts from content

---

# COMPONENT STATES
Define style coverage for interactive component states according to the target platform

# Common
Define interaction states as classes:
- *Default*: Initial render, resting state
- *Error* (`.field--error`): Validation styling with color and visible error message
- *Success* (`.field--success`): Confirmation styling with optional icon
- *Loading* (`.state-loading`): Reserved space with skeleton or spinner
- *Empty* (`.state-empty`): No-data message or illustration with optional CTA
- *Read-only* (`.button-readonly`): Non-editable appearance while keeping label visible

## Web
Define interaction states as selector variants:
- *Hover* (`:hover`): Subtle shadow lift or tint
- *Focus* (`:focus-visible`): Visible keyboard focus ring, minimum 3px
- *Active* (`:active`): Press state with slight scale or darker tint
- *Disabled* (`:disabled`): Reduced opacity using `$opacity.disabled` and no pointer interaction
- *Selected*: Toggle or radio state using `:checked` or custom classes

Rules:
- Never rely on color alone for error states; include visible text feedback
- Reserve layout space for loading states to avoid cumulative layout shift
- Keep loading, error, and empty feedback visible in the layout

## Native
Define interaction states using GXML attribtes or GeneXus-specific properties:
- Forbid CSS selectors strictly
- Configure visual states through `Design System` object GeneXus-specific properties
- Use GXML element attributes or bindings for dynamic states

---

# MOTION

## Philosophy
Motion serves one of four purposes:
- Communicating state change
- Signaling continuity between views
- Providing feedback after an action
- Communicating loading progress

All other motion is eliminated

## Timing Tokens
Define all durations in `#times`:

~~~
#times
{
	instant: 80ms;		// Micro-feedback; button press ripple
	fast: 120ms;		// Hover transitions, icon swaps
	normal: 220ms;		// Panel entrance, state changes
	slow: 360ms;		// Complex transitions, sheet open
	deliberate: 500ms;	// Onboarding, progress animations
}
~~~

## Easing Reference
Use standard CSS easing keywords or cubic-bezier functions:
- `ease`: General state changes (default)
- `ease-in`: Elements exiting the screen
- `ease-out`: Elements entering the screen
- `ease-in-out`: Elements that animate in place
- `cubic-bezier(0.34, 1.56, 0.64, 1.00)`: Spring entrance for modal/card reveal
- `cubic-bezier(0.16, 1, 0.3, 1)`: Decelerate-into-rest for drawer slide

## CSS Techniques (Web)
Use properties that trigger GPU compositing; never animate layout-affecting properties:
- `opacity`: Fade in/out, loading shimmer
- `transform: translateY()`: Panel entrance, reveal motion
- `transform: scale()`: Button press feedback, hover lift
- `box-shadow`: Elevation change on hover
- `background-color`: State change tint
- `border-color`: Focus ring activation

Never animate `width`, `height`, `margin`, or `padding` on interactive controls

## Transition Pattern (Web)
~~~
.button-primary
{
	transition:
		background-color $times.fast ease,
		box-shadow $times.normal ease,
		border-color $times.fast ease,
		transform $times.fast ease;
}

.button-primary:hover
{
	background-color: $colors.action-primary-hover;
	box-shadow: $shadows.medium;
	transform: translateY(-1px);
}

.button-primary:active
{
	transform: translateY(0px) scale(0.98);
	box-shadow: $shadows.low;
}
~~~

## Micro-Interactions (Web)
Standard patterns:
- *Button press*: Use `transform: scale(0.97)` at `:active`, restore at `$times.instant`
- *Card hover lift*: Use `translateY(-2px)` plus `$shadows.medium` at `$times.fast`
- *Input focus*: Use `border-color` shift to `$colors.border-focus` at `$times.fast`
- *Success confirmation*: Opacity pulse 0→1 on `.state-success` at `$times.normal`
- *Loading shimmer*: Use background keyframe from `$colors.surface-2` to `$colors.surface-1`

~~~
@keyframes shimmer
{
	0% { background-position: -200% 0; }
	100% { background-position: 200% 0; }
}

.state-loading
{
	background: linear-gradient(
		90deg,
		$colors.surface-2 25%,
		$colors.surface-1 50%,
		$colors.surface-2 75%
	);
	background-size: 200% 100%;
	animation: shimmer $times.deliberate ease infinite;
}
~~~

## Native Animations
CSS `transition` and `transform` are not supported on Native; use `gx-*` style properties

Enable animation on a class with `gx-animated: true` and `gx-animation-duration` (in ms):
~~~
.button-primary
{
	gx-animated: true;
	gx-animation-duration: 200;
}
~~~

### Panel Transitions
Apply `gx-enter-effect` and `gx-close-effect` on `Form`-scoped style classes
- Control panel navigation transitions
- Use the catalog to validate transition values
- Curl variants are Apple-only

On Web, apply `gx-enter-effect-*` and matching `gx-close-effect-*` properties on `Form`-scoped style classes

### Lottie Animations
Apply thse properties exclusively:
- `gx-animation-type: lottie`: Render a Lottie animation file
- `gx-lottie-file: ref(*)`: Lottie file reference
- `gx-animation-class)`: Animation class for a `Progress` indicator
- `gx-loading-animation-class)`: Loading animation class for `Form` or `Grid`
- `gx-launch-screen-animation-class)`: Animation played at app launch

### Motion Effect
Parallax-style tilt displacement on images and containers:
- `gx-motion-effect-max-horizontal-offset: <int>`: Max horizontal pixel displacement
- `gx-motion-effect-max-vertical-offset: <int>`: Max vertical pixel displacement

## Motion Rules
Additional motion quality constraints for both Web and Native:
- Animate at most 1-2 elements per view simultaneously; more dilutes intent
- Exit animations should run at 60-70% of the enter duration to feel responsive
- State changes (hover, expand, collapse) must animate smoothly; never snap instantly
- Fading elements must not linger below `opacity: 0.2`; either fade fully or stay visible
- Animations must be interruptible: a subsequent user action cancels the current animation (Web)
- Never block user input during a running animation; UI must stay interactive (Web)
- Stagger list/grid item entrances by 30-50ms per item; avoid all-at-once reveals (Web)
- Modal and sheet entrance: animate from trigger origin (scale+fade or directional slide) (Web)
- Forward navigation animates left/up; backward navigation animates right/down (Web)

---

# VISUAL POLISH

## Surface Depth
- Apply layered shadows:
	* On Web:
		- `$shadows.low` on cards
		- `$shadows.medium` on popovers
		- `$shadows.overlay` on modals
	* On Native: use `gx-elevation` equivalents
- On hover, increase shadow to signal elevation change: resting `$shadows.low` → `$shadows.medium`
- In dark mode, use lighter surface colors instead of heavier shadows; shadows lose contrast

## Gradients
Apply gradients sparingly for premium surfaces and brand moments
- Always reference named color tokens
- Never hardcode gradient colors
- Define gradient stop colors in `#DesignTokens` region before use

Example:
~~~
.hero-surface
{
	background: linear-gradient(135deg, $colors.action-primary 0%, $colors.action-secondary 100%);
}

.surface-brand-accent
{
	border-top: 3px solid;
	border-image: linear-gradient(90deg, $colors.action-primary, $colors.action-accent) 1;
}
~~~

## Glassmorphism (Web)
Apply to overlay panels, floating toolbars, and sticky navigation
- Set low-alpha background, `backdrop-filter: blur()` with saturate, and hairline low-alpha border
- Keep alpha values as `#colors` tokens for per-theme tuning

## Focus Ring
Define visible focus rings; never remove without a superior replacement
- Use `$colors.border-focus-ring` for theme-aware focus color
- Keep consistent focus states across interactive controls

---

# LAYOUT STRATEGY
Choose layout mechanism based on platform; Web and Native require different responsive adaptation approaches

## Web Controls

### Responsive Table
Use for responsive business forms with Bootstrap fluid grid
- Define cell widths as breakpoint percentages; cells wrap when total exceeds 100%
- Keep row span fixed to 1; express column behavior through cell width, not colspan

Breakpoints (Bootstrap-based):
- `xs`: Below 768px (phone portrait)
- `sm`: 768px and above (phone landscape / tablet portrait)
- `md`: 992px and above (desktop)
- `lg`: 1200px and above (wide desktop)

Per-cell configuration via `Responsive Sizes`:
- Width: Percentage of container width at each breakpoint
- Offset: Left offset percentage; used for visual centering without adding empty cells

Typical patterns:
- Single column mobile → two column desktop: 100% xs, 50% md
- Full-width hero: 100% xs md lg
- Sidebar layout: 25% md (sidebar) + 75% md (content) in the same row

### Table
Use for explicit cell merging or legacy form migration
- Support `Col Span` and `Row Span` design-time properties
- Keep rigid layout; combine with Responsive Table sections for page adaptation

Col Span and Row Span rules:
- `Col Span`: Merges horizontally; renders as a wider fixed cell
- `Row Span`: Merges vertically; only available in classic Table, not in Responsive Table
- Use for structured data entry forms, invoice-style layouts, and tabular read-only displays

### Flex
Use for flexible one-dimensional layouts with variable sizes or wrapping content
- Equivalent to CSS Flexbox; supports row and column directions
- Ideal for toolbars, button groups, wrapping card rows, and variable-width filter bars

### Smart Table
Use for precise two-dimensional grid layouts; closest to CSS Grid; use for:
- Dashboard KPI metric layouts
- Comparison grids with fixed columns
- Structured multi-column content that must not reflow

### Canvas
Use for overlapping controls or arbitrary positioning
- Equivalent to `position: absolute`; apply to media players, maps, badge overlays, and floating action buttons
- Require each child to define `width`, `height`, and one anchor per axis

## Mobile Layouts
Native `Panel` objects use multiple layouts instead of responsive CSS
- Define layouts per platform, orientation, and size class combination
- Select the most specific matching layout at runtime

Variant dimensions:
- Platform: Any / Android / Apple
- Orientation: Any / Portrait / Landscape
- Size class: Any / Phone / Tablet / Compact / Large

Rules:
- Always define at least one base layout (Any/Any/Any) as the fallback
- Define a separate Landscape layout to prevent portrait layout from stretching horizontally
- Define separate Tablet layouts for content-appropriate column arrangements
- Do not use Responsive Table in Native Panels; it is a Web-only control
- Keep mobile layouts single-column by default; use Tablet/Landscape variants for multi-column

## Container Selection
- Standard responsive Web form: Responsive Table
- Flexible horizontal or vertical section (toolbar, card row, chip bar): Flex
- Precise 2D composition (dashboard, metric grid): Smart Table
- Cell merging or rigid legacy alignment (classic forms, tabular invoices): Table
- Overlapping or absolutely positioned controls: Canvas
- Native mobile screen variants by orientation or device: Multiple Layouts

---

# PLATFORM DIFFERENCES
Target platform before authoring style classes
- Avoid CSS properties ignored or incorrect on Native
- Keep Web and Native styling behavior platform-specific

## Elevation
- On Web: Use `box-shadow` with standard CSS syntax
- On Native: Use `gx-elevation: <integer>` (Material Design dp depth); never use `box-shadow`

## Press States
- On Web: Use `:hover`, `:focus-visible`, `:active` CSS pseudo-selectors
- On Native: Pseudo-selectors are ignored; use instead:
	* `gx-highlighted-background-color`: Background during press/touch-down
	* `gx-highlighted-color`: Text or foreground color during press/touch-down

## Transitions
- On Web: Use `transition` and CSS `transform` properties
- On Native: Use `gx-animated: true`, `gx-animation-duration`, and GeneXus transform functions

## Units
- On Web: Use `px` for fixed sizes and `%` for relative; never use `dip`
- On Native: Use `dip` for fixed sizes and `%` for relative; never use `px`

## Application-Scope Theming (Native)
Some style properties apply globally at the `Application` level and affect platform-native input-based controls:
- `gx-primary-color`: Primary brand color for native system controls
- `gx-accent-color`: Accent color for interactive highlights
- `gx-activated-color`: Color when a native control is activated
- `gx-action-tint-color`: Tint color applied to all app bar action icons

Apply these on the `Application`-scope style class, not on individual control classes

## Sticky / Fixed Position
- On Web: Use `position: sticky` for scroll-following headers/footers; `position: fixed` to float above document flow
- On Native: Set `overflowBehaviour='Add Scroll'` and `autoGrow='False'` on the main container
	* That creates an independent scroll region
	* Without these, no scroll region is created

## Safe Area Insets (Apple)
Apple devices reserve space for the notch, status bar, and home indicator; also known as "unsafe areas"
- Use `expandBounds` attribute on `grid`, `tab`, `canvas`, `table`, and `smart` elements to manage expansion
- Use `expandBoundsLimit` attribute to control depth
- Use `expandBoundsDirections` to restrict edge
- Fixed bars and bottom CTAs that use `expandBounds="None"` are protected from the home indicator
- When using `flex` container, always set `expandBounds="None"`

## Viewport (Web)
- Use `min-height: 100dvh` instead of `100vh` to avoid address bar jump on mobile browsers
- Apply `touch-action: manipulation` on clickable controls to eliminate the 300ms tap delay
- Apply `cursor: pointer` on all clickable non-button elements; e.g. cards, custom controls
- Prevent horizontal scroll: ensure no child element overflows the viewport width

## Scroll Regions
- Avoid nested independent scroll areas that conflict with the page scroll
- On Web, scroll content behind fixed bars using `padding-bottom` equal to the bar height

---

# CONTROL CONSTRAINTS
Controls have rendering constraints that affect layout and styling decisions

## Button Icons
- Each button/action supports exactly one icon; assign via the `image` attribute
- Icon position relative to text is controlled by `imagePosition`; use catalog for valid values
- The `Behind Text` position value is Apple-only
- On Web, full CSS gives more icon placement flexibility via `background-image` and pseudo-elements

## Tab Icons
- Tab page icons: set `image` (selected) and `unselectedImage` (deselected) on `tabPage`; `imagePosition` controls placement
- Tab icon size is platform-controlled; no style class can target tab icon dimensions
- On Web, the `tab` control accepts style classes and full CSS for custom tab appearance
- Set `tabsBehavior` on `tab` for scroll vs fixed layout; fixed mode supports at most 4 tabs
- Use `moreButtonSelectedImage` / `moreButtonUnselectedImage` on `tab` to customize the overflow button

## Native App Bar
- The `actionBar` accepts `item` children; use `horizontalAlignment`, `priority`, `verticalAlignment` to position them
- The `actionBar` actions behave as buttons; each supports exactly one icon (same constraints as button icons)
- Icon size in app bar actions is platform-driven; not controllable per action
- Use `gx-icon` (Application scope) for central image in the app bar
- Use `gx-large-title-mode` (Application scope, Apple-only) for large title behavior; check catalog for values
- Use `gx-back-button-class` as style class for the back button appearance
- Use `gx-back-button-image` as custom image for the back button
- Use `gx-back-button-text` as custom text label for the back button

## Text Control Rendering
Define read-write `Attribute`/`Variable` by underlying type when displayed as inputs:
- `Character`/`VarChar` render single-line text edits
- `LongVarChar` renders multi-line text edits

Additional options for multi-line controls:
- `autoGrow="True"`: Control expands vertically with content; without it height is fixed
- `maxTextNumberLines`: Caps the visible line count before internal scroll begins
- `inviteMessage`: Placeholder text shown before the user types; do not use as the sole label

## Text Spacing
Size `TextBlock` or read-only `Attribute`/`Variable` cells for font and line-height requirements
- Avoid clipping and unexpected wrapping by providing enough height
- Use automatic or percentage height when text can expand

---

# RESPONSIVE DESIGN

## Media Queries
Define responsive breakpoints in `#mediaQueries`:
~~~
#mediaQueries
{
	mobile: (max-width: 599px);
	tablet: (min-width: 600px) and (max-width: 1023px);
	desktop: (min-width: 1024px);
	wide: (min-width: 1440px);
}
~~~

These tokens apply to Web style classes only; for Native mobile, use Multiple Layouts

## Adaptive Layout
- Mobile-first: Design for the smallest viewport and progressively enhance
- Reserve optional desktop areas using proportional `%` columns
- Never overload a single-column layout with more than one primary action area
- Use `page-content` to flex-fill available height; e.g. `rowsStyle="72dip;100%;64dip"`
- Web: Add responsive column behavior via `Responsive Sizes` per breakpoint on each cell
- Native: Define separate Panel `Layouts` by `orientation` and `size`

---

# AUTHORING GUIDE
See `object-design-system.md` for full `DesignSystem` object syntax, region definitions, and token group reference

## Workflow
1. Define visual direction: style mood, accessibility target, platform targets
2. Author primitive tokens: raw palette colors, spacing, sizes, radius values
3. Author semantic tokens: map roles to primitives per theme variant (light/dark)
4. Author `@font-face` declarations before any style class referencing a custom family
5. Organize style classes in `#region`/`#endregion` blocks
6. Compose classes with `@include` for shared base styles; add variant overrides below
7. Sync with Panel: every class used in `#Layout` must exist in the `DesignSystem` object

## Naming
- Use BEM notation for component classes; e.g. `.block`, `.block__element`, `.block--modifier`
- Use kebab-case for all token names and class names
- Use semantic names in `Panel` layout classes; never visual names

Semantic vs visual naming:
- Correct: `.button-primary`, `.surface-elevated`, `.state-error`, `.text-caption`
- Incorrect: `.blue-btn`, `.white-card-shadow`, `.red-text-box`, `.small-gray-font`

## Abbreviations
Never abbreviate class, token, region, or stencil names; use full words

Examples:
- `.btn-primary` (✘) → `.button-primary` (✓)
- `.acc-filled` (✘) → `.accordion-filled` (✓)
- `.nav-itm` (✘) → `.nav-item` (✓)
- `.txt-sm` (✘) → `.text-small` (✓)

Exceptions:
- Industry-standard acronyms may remain unexpanded; e.g. `CTA`, `URL`, `ID`
- Only when the expanded form adds confusion, not clarity

## Regions
Structure `#DesignStyles` using named `#region` blocks:
- `Reset`: Base resets and `@font-face` declarations
- `Layout`: `.page`, `.page-header`, `.page-content`, `.page-footer`
- `Surfaces`: `.surface`, `.surface-elevated`, `.surface-muted`, `.surface-glass`
- `Typography`: `.text-display`, `.text-title`, `.text-subtitle`, `.text-body`, `.text-label`, `.text-caption`
- `Buttons`: `.button-primary`, `.button-secondary`, `.button-ghost`, `.button-danger`, `.button-readonly`
- `Forms`: `.field`, `.field-label`, `.field-help`, `.field-error`, `.field-success`
- `Feedback`: `.state-loading`, `.state-empty`, `.state-error`, `.state-success`
- `Navigation`: `.nav-bar`, `.nav-item`, `.nav-item--active`, `.tab-bar`, `.tab-item`
- `Utilities`: `.divider`, `.badge`, `.chip`, `.avatar`, `.icon-container`

## Inheritance
Use `@import` to extend a base `DesignSystem` object; use `@include` to compose classes:

~~~
@import MyModule.BaseDesignSystem;

.button-secondary
{
	@include button-primary;
	text-color: $colors.action-primary;
	background-color: transparent;
	border-color: $colors.border-default;
}
~~~

---

# PANEL LAYOUT

## Class Contract
Every layout control in `#Layout` uses a class from the `DesignSystem` object contract:
- Page shell: `page`, `page-header`, `page-content`, `page-footer`
- Surfaces: `surface`, `surface-elevated`, `surface-muted`
- Typography: `text-title`, `text-subtitle`, `text-body`, `text-caption`, `text-label`
- Actions: `button-primary`, `button-secondary`, `button-ghost`, `button-danger`
- Forms: `field`, `field-label`, `field-help`, `field-error`
- Feedback: `state-loading`, `state-empty`, `state-error`, `state-success`

## State Coverage
Define all UI states per screen before writing `#Layout`:
- Default: Normal render via `Start` or `Refresh` event
- Loading: Apply `.state-loading` skeleton in reserved cell
- Empty: No data; apply `.state-empty` surface
- Error: Failed operation or validation; apply `.state-error` with retry action
- Success: Operation confirmed; apply `.state-success` with confirmation message

## Skeletons
- Define `.state-loading` with shimmer animation in `DesignSystem` object
- Mirror skeleton cell dimensions to match actual content dimensions
- Place loading placeholder with the same `height` as the real content
- Never collapse or hide loading area; reserved space prevents layout shift
- Show skeleton only when the operation exceeds 300ms; suppress for fast responses
- During async operations:
	*  Disable triggering control in event code; `<control-name>.Enabled = False`
	*  Restore enabled state after operation; `<control-name>.Enabled = True`
	*  Show progress indicator while operation runs

## Layout Rules
- Build clear hierarchy: `page-header` then `page-content` then `page-footer`
- Apply progressive disclosure; show primary action first, defer secondary
- Keep one dominant CTA per section; group related controls in one surface
- Use `rowsStyle` to fix header/footer heights and flex-fill content; e.g. `"72dip;100%;64dip"`
- Never hardcode colors or spacing in `#Layout` attributes; use `DesignSystem` classes
- Validate all layout units: `px` or `%` for Web, `dip` or `%` for Native

---

# SCREEN PATTERNS
Structural patterns for common GeneXus screens

## Hero + Action + Content
- Top row: Contextual title and copy; `page-header`, `text-title`, `text-caption`
- Mid row: Primary action with highest visual priority; `button-primary` in `surface-elevated`
- Bottom rows: Data or content cards; `surface-muted` grid items

## Form + Live Feedback
- Main area: Grouped fields with labels and helper text; `.field`, `.field-label`, `.field-help`
- Dedicated rows for `.field-error` and `.state-success`
- Secondary column on desktop for preview or summary
- Form UX Rules
	* Place error messages immediately below the related field (`.field-error`)
	* Mark required fields with a visible indicator: asterisk `*` or equivalent label
	* Validate on blur, not on keystroke; show errors only after the user leaves the field
	* Group related fields logically; fieldset section or visual grouping
	* Error messages must state cause and corrective action; not just "invalid value"
	* Destructive actions (delete, reset) use `.button-danger`; separate visually from primary CTA
	* After submit with errors: auto-focus the first invalid field using `<control-name>.SetFocus()` method
	* Toasts auto-dismiss after 3-5 seconds; never steal keyboard focus
	* Confirm before dismissing a sheet or modal that contains unsaved changes
	* Read-only fields (with `.button-readonly` class or `readOnly='True'` element) retain full contrast; disabled uses reduced opacity

## List + Detail
- Desktop: Two-column split, `columnsStyle="30%;70%"`; list left, detail right
- Mobile: Stacked flow with key actions in footer row
- Grid item height: Minimum `72dip` for touch-safe tap targets

## Bottom Nav + Top Context (Native)
- Bottom area: Persistent primary navigation, three to five destinations; `nav-bar`
- Top area: Contextual title and screen-level actions; `page-header`
- Destination state remains stable when switching tabs
- Navigation rules:
	* Every navigation item must have both icon AND text label; icon-only nav harms discoverability
	* Primary nav (tabs/bottom bar) vs secondary nav (drawer/sidebar) must be visually separated
	* Navigation placement must stay consistent across all screens; never change by screen type
	* Desktop screens (≥1024px): prefer sidebar navigation; small screens use bottom or top nav
	* Use sidebar/drawer for secondary navigation only; never for primary actions
	* Never mix `Tab` + `Sidebar` + `Bottom Nav` at the same hierarchy level
	* Never use modals for primary navigation flows; they break the forward/back path
	* Back navigation must restore previous scroll position and filter/input state
	* Deep links:
		- Set `Panel` name in `deepLinkName` property (Native)
		- Set `deepLinkBaseURL` on the Main object
	* Apple back gesture: handled by `Back` event; avoid custom top-left actions
	* Tab badge indicators: use sparingly; clear the badge when user visits the destination

## List + Filters + Refresh
- Top area: Quick filters and search entry
- Main area: Scrollable results list with progressive loading
- Mobile-first refresh behavior: Use `pull-to-refresh` and explicit retry on failures
- Keep filters sticky or quickly recoverable while scrolling results
- Show clear empty/loading/error states and preserve the latest successful filter set

## Multi-step Flow
- Stepper or progress indicator visible across all steps; `text-caption` in `surface-muted`
- One primary CTA fixed at bottom per step; `button-primary` in `page-footer`
- Validate incrementally; persist previous step data in state variables

## Dashboard
- Fixed header with KPI cards in responsive grid; `surface-elevated` containers
- Scrollable content area with charts and data grids
- Use tabular font figures for all numeric values

## Empty State
- Center-aligned illustration or icon inside `state-empty`
- Single descriptive title plus helper text
- One optional CTA; `button-primary` or `button-secondary`

## Offline + Retry
- Provide a dedicated empty/error surface for connectivity loss
- Show last synchronized data, tagged with last sync time; read-only if needed
- Use `state-error` on failed sync, `state-offline-cached` on stale local data
- Explicit retry action; `button-primary` labeled with the action, e.g. "Try Again"
	* Calls `Synchronization` external object
	* Shows a loading state while running
	* Stays visible alongside automatic sync
- Flag unsent records as `.state-pending`
	* Clear the flag once sync confirms success
	* Show the user a visible confirmation of the outcome

---

# STENCILS
Use `Stencil` objects for repeatable UI fragments across multiple screens

Constraints:
- All interaction logic goes in the parent `Panel` object
- All `Stencil` instances share the `DesignSystem` set in the parent `Panel` object

Naming:
- Use `<feature><role>Stencil` names based on reusable UI purpose
- Describe the fragment role, not the visual implementation or location
- Keep names stable across screens; avoid page-specific or positional names

Examples:
- `<feature>ItemStencil`: Single item in a list or grid
	* `ProductItemStencil`: Single product item in a product grid
	* `OrderLineItemStencil`: Single order line in an order summary
- `<feature>CardStencil`: Card layout for dashboard or summary display
- `<feature>FormStencil`: Reusable form field group

---

# WORKFLOW
Follow this workflow for every new screen or `DesignSystem` update:

1. Define purpose and context
	- Screen name, primary user goal, platform target (Web / Native / both)
	- Content hierarchy: most important content first, secondary second
	- Navigation path: which screen leads here and where the user goes next

2. Define screen states
	- List all states: Default, Loading, Empty, Error, Success
	- Map each state to a `DesignSystem` feedback class

3. Author or update tokens
	- Verify primitive tokens exist for all required colors
	- Add or update semantic tokens for new roles
	- Define or update spacing, radius, shadow, and timing tokens as needed

4. Author or update styles
	- Write style classes in `#DesignStyles` for all controls used in the screen; referece [Styles](./frontend-styles.md) catalog
	- Organize in `#region` blocks
	- Verify all classes consume tokens; zero hardcoded values

5. Author Panel layout
	- Define `#Variables` for all bound data
	- Define `#Events` for all user interactions and data loading
	- Write `#Layout` using GXML syntax; reference [Layout](./frontend-layout.md) catalog
	- Assign semantic class names to all layout controls
	- Set `Style = "<design-system-name>"` in `#Properties`

6. Run REVIEW CHECKLIST; all items must pass before delivery

---

# REVIEW CHECKLIST
Must ensure these checks pass before providing a solution:

## Accessibility
- [ ] Normal text contrast `4.5:1` or above
- [ ] Large text contrast `3:1` or above
- [ ] Non-text UI contrast `3:1` or above
- [ ] Focus ring visible on all interactive controls (Web)
- [ ] All controls keyboard-accessible (Web)
- [ ] Touch targets `48dip` minimum (Native)
- [ ] All input controls have visible labels
- [ ] Icon-only buttons have descriptive captions
- [ ] Status not conveyed by color alone
- [ ] Motion removable per `prefers-reduced-motion`
- [ ] Use `accessibleName` attribute on controls
- [ ] Skip-to-main link present as first focusable element (Web)
- [ ] Heading hierarchy sequential `h1`→`h2`→`h3`; no skipped levels
- [ ] Dividers and borders visible in both light and dark modes
- [ ] Modal scrim opacity between `40-60%` black
- [ ] Tab order matches `#Layout` order and visual flow; keyboard-verified

## Typography
- [ ] Body text `16px`/`16dip` or above
- [ ] No text below `12px`/`12dip`
- [ ] Line height `1.5` or above for body text
- [ ] Hierarchy clear without relying on color alone
- [ ] Numeric content uses tabular figures where appropriate
- [ ] Truncation is intentional and content is recoverable
- [ ] Cells containing text have sufficient height for font size and line-height

## Color Tokens
- [ ] No raw hex values in `#DesignStyles` section
- [ ] All colors reference `$colors.<token>` value
- [ ] Both light and dark mode tokens defined
- [ ] Feedback states tokenized
- [ ] Interactive states tokenized
- [ ] Gradient declarations use token references only
- [ ] Dark mode uses tonal/desaturated variants; not a simple light-mode inversion
- [ ] Secondary text meets `3:1` contrast on dark surfaces
- [ ] Feedback colors (`state-error`, `state-success`) meet `4.5:1` on their backgrounds
- [ ] Light and dark variants reviewed together before delivery

## Layout
- [ ] All classes exist in the target `DesignSystem` object
- [ ] The `Style` property references correct `DesignSystem` in `#Properties` section
- [ ] Units are `px`/`%` for Web, `dip`/`%` for Native
- [ ] Layout control type matches the use case
- [ ] No hardcoded colors or spacing in `#Layout` attributes
- [ ] All UI states have layout representation
- [ ] No abbreviated class, token, or region names; full words used throughout
- [ ] Multiple Layouts defined for orientation and device variants (Native)
- [ ] Set `expandBounds` correctly on main layout containers for Apple safe areas
- [ ] No horizontal scroll present on any screen width
- [ ] No nested independent scroll regions conflicting with page scroll

## Interaction
- [ ] One dominant CTA per section
- [ ] Every action has an event with visible user feedback
- [ ] Loading/empty/error surfaces reserve correct layout space
- [ ] Navigation is predictable and stable across screens
- [ ] Data grids have minimum `72dip` row height for touch safety
- [ ] Hover transitions defined for all interactive surfaces (Web)
- [ ] Focus rings use `$colors.border-focus-ring` (Web)
- [ ] Define `gx-elevation` instead of `box-shadow` for Native classes
- [ ] Define `gx-highlighted-background-color` for interactive Native controls
- [ ] Native animated controls have `gx-animated: true` and `gx-animation-duration`
- [ ] Panel transitions defined with `gx-enter-effect` and `gx-close-effect` (Native)
- [ ] No CSS `transition` or `transform` in Native-targeted style classes
- [ ] No emoji used as structural icons
- [ ] Single icon family used; stroke and style (filled/outline) consistent
- [ ] Skeleton shown only for operations expected to exceed 300ms
- [ ] Submit buttons are disabled during large operations
- [ ] Error messages placed below the related field; state cause and corrective action
- [ ] Required fields marked with visible indicator
- [ ] Navigation items have both icon and text label
- [ ] Back navigation restores scroll and filter state

---

# ANTI-PATTERNS
Patterns that break visual quality, accessibility, or maintainability:
- Hardcoding hex values in `#DesignStyles`: Breaks theming; requires full style rewrites
- Hardcoding hex values in gradient declarations: Biases palettes; use token references
- Using visual class names (`.blue-button`, `.red-box`): Breaks semantic contract
- Hiding focus rings: Breaks keyboard accessibility entirely
- Gray-on-gray text without contrast check: Fails WCAG; unreadable for low-vision users
- Body text below `12px`/`12dip`: Illegible on mobile; fails accessibility guidelines
- Placeholder-only form labels: Disappear on focus; fail labeling requirements
- Color-only feedback indicators: Fail color-blind accessibility
- Motion on every render: Distracts from task completion
- Mixing Canvas and flow layouts in the same section: Produces layout conflicts
- Skipping `Loading`, `Empty`, and `Error` states: Leaves users without context
- More than two type families: Creates visual noise and inconsistent reading rhythm
- Competing accent colors per screen: Dilutes hierarchy; no clear primary action
- Using classic Table for new responsive screens: Lacks responsive flow; use Responsive Table
- Using Responsive Table in Native panels: Web-only control; use Multiple Layouts
- Not syncing `DesignSystem` after Panel update: Runtime class-not-found errors
- Defining styles without token references: Prevents theming; creates maintenance debt
- Using `dip` units in Web layouts: Web generator does not support `dip`
- Using `px` units in Native layouts: Native generator uses `dip`
- Animating `width`, `height`, or `margin`: Triggers layout reflow; use `transform` and `opacity`
- Removing shadow entirely in dark mode: Depth invisible; use lighter surface-color differences
- Applying `$shadows.high` to static non-interactive surfaces: Overweights without signaling intent
- Avoid `transition` and `transform` values from CSS on Native; instead:
	* Use `gx-animated` property for lottie animations
	* Use GeneXus-specific transform functions; e.g. `gxResize`, `gxTranslateRelativeTo`, etc
- Forbid `position: sticky` and `position: fixed` from CSS on Native
	* Use `overflowBehaviour='Add Scroll'` attribute on the main container element
	* Set `autoGrow='False'` attribute for fixed scrolling behavior
- Using `:hover` in Native: No hover on touch; use `gx-highlighted-background-color`
- Using `:focus-visible` or `:disabled` in Native: Unsupported; use `gx-highlighted-*`
- Skipping PNG density variants in Native: Blurry icons on high-density screens; provide `1x`, `2x`, `3x`
- Using font-based icon libraries in Native: Not supported; always use `Image` objects
- Sizing tab or app bar icons via style classes on Native: Size is platform-controlled; cannot override
- Using `LongVarChar` for single-line input: Always renders multi-line; use `Character` or `VarChar`
- Using emoji as icons: Font-dependent, unthemeable, inaccessible to screen readers
- Mixing icon families or styles (filled/outline at the same level): Breaks visual coherence
- Showing skeleton/spinner for operations under 300ms: Causes flickering without value
- Not disabling the action button during async: Allows duplicate requests
- Ignoring `expandBounds` attribute on Apple layouts: CTAs and bars collide with notch or home indicator
- Using dark mode as a post-process inversion: Incorrect contrast; desaturate and redesign
- Icon-only navigation without text labels: Reduces discoverability for unfamiliar users
- Using modals for primary navigation flows: Destroys the forward/back history
- Scrim below `rgba(0,0,0,0.40)` on overlays: Background competes with foreground content
- Press states that shift layout bounds: Creates visual jitter; use opacity/color instead
- Validating on keystroke for every field: Noisy and frustrating; validate on blur
- Error messages without corrective guidance: Leaves users without a path to resolution
- Using `100vh` for full-screen web-based mobile layouts: Address bar causes height jump; use `100dvh`
