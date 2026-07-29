# Weave Fleet — Agent Instructions

## Layout Vocabulary

The application shell is a composable layout built from named panels:

```
[rail][context]
```

- **Rail** — Always-visible icon navigation fixed to the left edge. Controls which view fills the context area.
- **Context** — Everything to the right of the rail. Its composition changes per route.

### Route Compositions

| Route        | Context layout                              |
|--------------|---------------------------------------------|
| Sessions     | `[session-list][conversation][content]`     |
| Settings     | `[settings-menu][settings-detail]`          |
| Automations  | `[automations-list]`                        |

### Panel Definitions

| Panel             | Description                                                                 |
|-------------------|-----------------------------------------------------------------------------|
| `rail`            | Icon-only vertical nav bar (left edge, always visible)                      |
| `session-list`    | List of sessions for the current workspace                                  |
| `conversation`    | The message/activity stream for the active session                          |
| `content`         | Right-side artifact viewer (rendered markdown, HTML preview, raw source)     |
| `settings-menu`   | Left nav for settings categories                                            |
| `settings-detail` | Detail pane for the selected settings category                              |
| `automations-list`| List/card view of configured automations                                    |

### Resize Gutters

Resize gutters exist only between specific panel pairs:

- `[conversation]` ↔ `[content]`

No gutter between rail and context, or between session-list and conversation.
