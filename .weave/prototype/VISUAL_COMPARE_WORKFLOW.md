# Visual Comparison Workflow for Agents

**Purpose:** Enable agents to visually compare the running Weave Fleet app against the static prototype during implementation, using Playwright screenshots.

**Verified:** 2026-07-29 (prototype screenshots + live app screenshots confirmed working)

---

## Quick Start

```bash
cd tests/beta-harness
bun install
node node_modules/@playwright/test/cli.js install chromium

# Screenshot the prototype (target state)
bun run tsx visual-compare.ts --prototype-only

# Screenshot the running app (current state, fleet must be running)
bun run tsx visual-compare.ts --app-only

# Screenshot both for comparison
bun run tsx visual-compare.ts

# Override app URL if fleet is on a different port
FLEET_URL=http://localhost:3002 bun run tsx visual-compare.ts --app-only
```

---

## How It Works

The script at `tests/beta-harness/visual-compare.ts`:

1. Opens the prototype HTML via `file://` protocol (no server needed)
2. Navigates through views by clicking the icon rail and UI elements
3. Opens the running Fleet app (default: `http://localhost:5001`)
4. Navigates through equivalent routes
5. Saves all screenshots to `tests/beta-harness/findings/visual-compare/`

Agents read the resulting PNG files using the Read tool, which renders images inline.

---

## Commands

| Flag | Behaviour |
|------|-----------|
| (no flags) | Screenshot both prototype and running app |
| `--prototype-only` | Prototype only (no app needed) |
| `--app-only` | Running app only |
| `--boot-fleet` | Start a fresh fleet instance, screenshot both, then stop |
| `--interactive` | Open headed browser on prototype for manual exploration |

---

## Output Files

```
tests/beta-harness/findings/visual-compare/
  prototype-conversation.png      # Conversation view (default state)
  prototype-settings.png          # Settings page with sidebar nav
  prototype-automations.png       # Automations list with cards
  prototype-board.png             # Board/kanban placeholder
  prototype-artifact-viewer.png   # Right panel: rendered markdown viewer
  app-landing.png                 # Fleet dashboard / session grid
  app-settings.png                # Settings page
  app-board.png                   # Board view
  app-analytics.png               # Analytics view
  app-pipelines.png               # Pipelines view
  app-session-detail.png          # Session conversation (if a session exists)
```

All files are gitignored. They are ephemeral comparison artifacts.

---

## Agent Workflow During Implementation

### 1. Capture the target (once, or after prototype changes)

```bash
bun run tsx visual-compare.ts --prototype-only
```

Then read the screenshots to understand the visual target:
```
Read tests/beta-harness/findings/visual-compare/prototype-conversation.png
```

### 2. Make frontend changes

Edit Vue components, Tailwind config, CSS variables, etc.

### 3. Rebuild the client

```bash
cd client && npm run build
```

This outputs the built SPA to `src/WeaveFleet.Api/wwwroot/`, which the running .NET server serves.

### 4. Capture the current state

```bash
cd tests/beta-harness
bun run tsx visual-compare.ts --app-only
```

### 5. Compare visually

Read both screenshots and assess fidelity:
```
Read tests/beta-harness/findings/visual-compare/prototype-conversation.png
Read tests/beta-harness/findings/visual-compare/app-landing.png
```

Look for:
- Colour matching (coral #D95A3A, indigo #5B6EC7, bg #FAF9F7)
- Corner radius (should be 0px everywhere)
- Layout structure (icon rail, panels, gaps)
- Typography (Inter for UI, monospace for code/paths)
- Spacing and border treatment

### 6. Iterate

Repeat steps 2-5 until the views converge.

---

## View Mapping: Prototype to App

| Prototype View | App Equivalent | Route |
|----------------|---------------|-------|
| Conversation (default) | Session detail | `/sessions/:id` |
| Settings | Settings | `/settings` |
| Automations | Pipelines | `/pipelines` |
| Board | Board | `/board` |
| Analytics | Analytics | `/analytics` |
| New Session form | New Session flow | (modal or `/`) |
| Artifact viewer (right panel) | No equivalent yet | N/A |

---

## Technical Details

### Prototype access
- Self-contained HTML at `.weave/prototype/index.html`
- Loads Inter font from Google Fonts CDN
- Loads Lucide icons from unpkg CDN
- Loads Mermaid.js from jsdelivr CDN
- No build step; opens directly via `file://`

### App access
- Fleet .NET server serves built SPA from `src/WeaveFleet.Api/wwwroot/`
- Default port: 5001 (production/dev mode)
- Vite dev server: port 3002 (if running separately with `cd client && npm run dev`)
- Set `FLEET_URL` environment variable to override

### Navigation in the prototype
- Icon rail buttons use `[data-nav="..."]` attributes
- Top tabs use `[data-view="..."]` attributes
- Artifact items use `[data-file="..."]` attributes
- After navigating to settings/automations/board, the top-bar is hidden; must reload or use icon rail to return to conversation

### Extending the script
- The script exports `openPrototype()`, `openApp()`, and `screenshot(page, name)` as reusable functions
- Import from `./visual-compare.js` in custom scripts for ad-hoc navigation sequences
- Add new views to the `PROTOTYPE_VIEWS` or `APP_VIEWS` arrays

---

## Prerequisites

| Requirement | Install |
|-------------|---------|
| Bun runtime | Already at `C:\Users\piete\.bun\bin\bun.exe` |
| Node.js | Already at `C:\Users\piete\AppData\Local\weave\fleet\bin\node.exe` |
| Dependencies | `bun install` in `tests/beta-harness/` |
| Chromium | `node node_modules/@playwright/test/cli.js install chromium` |
| Fleet running | Start fleet normally, or use `--boot-fleet` flag |

---

## Limitations

- **Auth:** The app may redirect to login if no session cookie exists. Screenshots will show the login page. For full navigation, ensure the app is in a state where routes are accessible (dev mode bypass or pre-authenticated session).
- **CDN dependency:** Prototype screenshots require internet access for Google Fonts and Lucide icons. Without internet, icons render as empty boxes.
- **Session detail:** The `app-session-detail.png` only captures if the script can find and click a session link on the landing page.
