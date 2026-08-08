# Weave Fleet — Layout Vocabulary

Canonical naming for UI panels and their relationships. All agents and code should use these terms consistently.

## Shell Structure

```
[rail][context]
```

The **rail** is fixed. The **context** area is the entire region to its right and changes composition per route.

## Route Compositions

| Route        | Full layout                                     |
|--------------|-------------------------------------------------|
| Sessions     | `[rail][session-list][conversation][content]`   |
| Settings     | `[rail][settings-menu][settings-detail]`        |
| Automations  | `[rail][automations-list]`                      |

## Panel Definitions

| Panel             | Description                                                                 | Collapsible |
|-------------------|-----------------------------------------------------------------------------|-------------|
| `rail`            | Icon-only vertical nav bar (left edge, always visible). Controls which view fills the context area. | No |
| `session-list`    | List of sessions for the current workspace (tree with project groupings).   | Yes |
| `conversation`    | The message/activity stream for the active session. Contains the composer at the bottom. | No |
| `content`         | Right-side panel. Context-dependent: shows artifacts (files, diffs, source) for sessions, rendered markdown, HTML preview, or raw source. | Yes |
| `settings-menu`   | Left nav for settings categories (General, Agent, Models, etc.)             | No |
| `settings-detail` | Detail pane for the selected settings category                              | No |
| `automations-list`| Full-width list/card view of configured automations                         | No |

## Resize Gutters

Resize gutters exist only between specific panel pairs:

- `[conversation]` ↔ `[content]`

No gutter between rail and context, or between session-list and conversation.

## Component Mapping

| Panel             | Vue component file                                  |
|-------------------|-----------------------------------------------------|
| `rail`            | `client/src/components/layout/IconRail.vue`         |
| `session-list`    | (in AppShell / ContextPanel — to be extracted)      |
| `conversation`    | `client/src/components/layout/CenterContent.vue`    |
| `content`         | `client/src/components/layout/ContextPanel.vue`     |
| `settings-menu`   | (within settings route)                             |
| `settings-detail` | (within settings route)                             |
| `automations-list`| `client/src/routes/pipelines.tsx`                   |
| shell             | `client/src/components/layout/AppShell.vue`         |

## Status Bar

A persistent bottom bar spanning the full width below all panels:

- **Left:** Keyboard shortcut hints (`Ctrl N` New session, `Ctrl K` Command, etc.)
- **Right:** Session state dot + status label + model badge + token count

Component: `client/src/components/layout/StatusBar.vue` (to be created)

## Design Tokens

| Token | Value | Usage |
|-------|-------|-------|
| `--indigo` | `#5B6EC7` | Active rail indicator, primary CTAs |
| `--indigo` | `#5B6EC7` | Secondary accent, links |
| `--bg` | `#FAF9F7` | Page background (light theme) |
| `--border` | `#E8E6E3` | Panel/card borders (light theme) |
| Border radius | `0px` | All corners are sharp (no rounding) |
| Font (UI) | Inter | All interface text |
| Font (code) | JetBrains Mono | Code blocks, file paths, commands |
