# Design System

## Overview

The prototype at `.weave/prototype/index.html` is the visual source of truth. All interactive elements follow a consistent pattern: 180ms transitions, zero border radius (except circular elements), and theme-aware hover states.

## Button Variants

| Variant | Use Case | Example |
|---------|----------|---------|
| `default` | Primary actions | Create, Save, Send |
| `destructive` | Dangerous actions with text | Delete (with text) |
| `outline` | Secondary bordered actions | Cancel, Add, Retry |
| `ghost` | Subtle/borderless actions | Back, Load more |
| `toolbar-icon` | Icon-only toolbar buttons | Edit, Fork, Copy |
| `toolbar-icon-danger` | Destructive icon buttons | Delete, Abort, Stop |
| `filter` | Toggle/filter buttons | Open/Closed, Label, Author |
| `link` | Text links | Navigation links |

All variants are defined in `src/components/ui/button/index.ts` using CVA (class-variance-authority).

## Button Sizes

| Size | Dimensions | Use Case |
|------|-----------|----------|
| `default` | h-9 (36px) | Standard buttons |
| `sm` | h-8 (32px) | Compact buttons |
| `toolbar` | 28x28 | Toolbar icon buttons |
| `toolbar-lg` | 32x32 | Larger toolbar buttons |
| `icon` | 36x36 | Square icon buttons |

## CSS Utility Classes

Use these for non-button interactive elements:

- **`list-item-hover`** - For list items (session items, file items, nav items). Provides background hover state.
- **`tab-hover`** - For tab navigation buttons with underline active state.
- **`interactive-icon`** - For standalone icons that change color on hover.

All utilities are defined in `src/assets/main.css` under the `@layer utilities` block.

## Design Tokens

All interactive elements use these CSS custom properties:

- **`--transition: 180ms ease-out`** - ALL interactive transitions (hover, focus, active)
- **`--bg`** - Hover background color (theme-aware)
- **`--border`** - Hover border color
- **`--text`** - Hover text color
- **`--muted`** - Default interactive element color
- **`border-radius: 0`** on all non-circular elements

These tokens are defined in `src/assets/main.css` and automatically adapt to light/dark themes.

## How to Add a New Pattern

### If it's a button:
1. Add a new variant in `src/components/ui/button/index.ts` using CVA
2. Reference the prototype for visual spec (spacing, colors, states)
3. Use existing design tokens (`--transition`, `--bg`, etc.)
4. Run `bun run lint:design` to verify no drift

### If it's a non-button interactive element:
1. Add a `@utility` class in `src/assets/main.css`
2. Use `--transition` for timing
3. Use theme tokens (`--bg`, `--muted`, etc.) for colors
4. Apply `border-radius: 0` unless it's circular
5. Run `bun run lint:design` to verify

### Example: Adding a new button variant

```typescript
// In src/components/ui/button/index.ts
export const buttonVariants = cva(
  "...",
  {
    variants: {
      variant: {
        // ... existing variants
        "my-new-variant": "bg-blue-500 text-white hover:bg-blue-600",
      },
    },
  }
);
```

### Example: Adding a new utility class

```css
/* In src/assets/main.css */
@layer utilities {
  .my-interactive-element {
    transition: var(--transition);
    color: hsl(var(--muted));
    border-radius: 0;
  }
  
  .my-interactive-element:hover {
    color: hsl(var(--text));
    background: hsl(var(--bg));
  }
}
```

## Lint Script

The design system lint script detects common drift patterns:

- Raw `<button>` elements (should use `<Button>` component)
- Hardcoded border radius values (should use `border-radius: 0` or `rounded-full`)
- Hardcoded transition timings (should use `var(--transition)`)

**Run before committing:**

```bash
bun run lint:design
```

**Note:** Files in `src/components/ui/` are excluded from linting because they ARE the design system.

## Quick Reference

**Need a primary action button?**
```vue
<Button variant="default">Save</Button>
```

**Need a toolbar icon button?**
```vue
<Button variant="toolbar-icon" size="toolbar">
  <Icon name="edit" />
</Button>
```

**Need a list item with hover state?**
```vue
<div class="list-item-hover">
  <!-- content -->
</div>
```

**Need a custom interactive element?**
```vue
<div class="interactive-icon">
  <Icon name="star" />
</div>
```
