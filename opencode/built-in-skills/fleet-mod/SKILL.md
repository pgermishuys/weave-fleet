---
name: fleet-mod
description: Make a Fleet mod, a small change to how Fleet draws something, for the user. Use when the user asks to change how Fleet shows a tool's output, make output readable, add a chip, band or pane to Fleet, or says "mod". You write the mod as a draft, test it in this session, and ask the user to keep it.
---

# Make a Fleet mod

A mod is a folder with two files, `mod.json` and one hooks module. Its hooks run when Fleet is about to draw
something (a tool row, a band above the composer, a status chip, a pane). They return a tree of Fleet's own elements,
and Fleet draws it in its theme on desktop and phone. A mod never runs script in Fleet's page and can't reach files,
processes or the network.

What you write is a **draft**. It runs only in this session. The user decides whether to keep it for all their sessions.

## The loop

1. `fleet_mod_write` with the files. It returns the static check. Read it: errors have a line and column.
2. `fleet_mod_reload`. It says whether the draft loaded and what it registered.
3. `fleet_mod_test` with a realistic sample event. Read the tree or the failure that comes back. Nothing is drawn on screen.
4. Fix and repeat from 1 until the test shows what you intended.
5. `fleet_mod_keep` with a one-sentence note. The user sees what the mod touches and decides.

Rules:
- Never say a mod is kept until `fleet_mod_keep` says so. If it says it is waiting for the user, don't ask again.
- `fleet_mod_check` shows the check, why the last load failed and the log. `fleet_mod_list` shows drafts and kept mods.
- The tools exist only when Mods is on. If a tool says Mods are off, or Fleet started without mods, tell the user and stop.
- Test with real output. Copy the shape of what the tool really prints; a toy sample passes tests the real row fails.

## mod.json

See test-chips at the end for a whole one.

- `name`: lowercase letters, digits and `-`, 1 to 64, starting with a letter. It matches the folder. Not `fleet-...`, not `drafts`, not a Windows device name (`con`, `nul`, `com1`...).
- `version`: your version string.
- `description`: one line, 200 characters at most. The user reads it in the review.
- `hooks`: path to the module inside the folder: `.js`, `.mjs`, `.ts` or `.mts`.

Files go in `fleet_mod_write` with paths relative to the mod's folder. A mod may also ship `.html` files for `Page`.

## The hooks module

```ts
import type { Register } from "fleet-mods";

export const register: Register = (on) => {
  on("ui.render", { component: "ToolUse", props: { tool: "bash" } }, async ($, e, next) => {
    return next(e); // observe: pass through
  });
};
```

A hook does one of three things:
- **Observe**: `return next(e)`. Fleet draws as usual.
- **Rewrite**: `return next({ ...e, props: { ...e.props, title: "..." } })`. `e` is frozen; pass a copy.
- **Answer**: return a tree, or `null` to draw nothing, without calling `next`.

`on(event, matcher?, hook)`:
- Call it directly inside `register`, with a string-literal event and an object-literal matcher.
- A matcher compares fields with `e`: a value matches itself, an array matches any of its values, a RegExp matches a string, a nested object matches a nested field. `{ component: ["ToolUse", "ToolResult"], props: { tool: "bash" } }`.
- Registering one event twice without a matcher is refused. Put the cases in one hook, or give each a matcher.
- `.catch(handler)` on the returned registration runs when the hook throws or times out. In it, `next.error` is `{ kind, message }` and `next.called` says whether the hook had called `next`.
- A hook has 10 s; a `.catch` has 1 s.

## Render sites

`ui.render` has `e.component`, `e.requestId` and `e.props`. Five sites:

| Site | Draws | `e.props` |
| :- | :- | :- |
| `ToolUse` | On a tool row's line, after Fleet's label. **Inline only.** | tool row props |
| `ToolResult` | The body the row shows when opened, in place of Fleet's. | tool row props |
| `ComposerBand` | A band above the composer, shared by every mod. | `isWorking` |
| `StatusChip` | A chip at the end of the status bar. **Inline only.** | none |
| `Pane` | A pane opened with `$.ui.open({ id, title? })`: a canvas on desktop, a sheet on the phone. | `title` |

Inline means `Text`, `Pill`, `Icon`, `Button`, or a row `Box` holding only those. Anything else there makes the tree invalid.

`next(e)` at a site resolves to what the rest of the chain draws: another mod's tree, or `{ type: "Fleet" }` for Fleet's
own drawing (nothing at `ToolUse`, `ComposerBand` and `StatusChip`; Fleet's body at `ToolResult`). A returned tree
replaces it. To keep it, put `await next(e)` inside your `Box`, at most once.

Tool row props:
- `tool`: Fleet's canonical name, the same for every harness: `bash`, `read`, `edit`, `task`, `fleet_page_show`... `rawTool` is the harness's own name (`Bash`). Match on `tool`.
- `category`: `read`, `search`, `edit`, `shell`, `web`, `subagent`, `question`, `plan`, `skill`, `fleet` or `other`.
- `status`: `pending`, `running`, `completed`, `error`, `cancelled`. `title`: the row's title.
- `input`: the call's arguments (`{}` and `inputTruncated: true` over 64 KiB). `output`: the output text (the last 256 KiB, `outputTruncated`). `durationMs` once finished.
- A row is drawn when first shown and when its status changes, never on each streamed line. A running call's `output` is what had arrived at the last status change.

## Events

| Event | Fires when | `e` |
| :- | :- | :- |
| `session.start` | Once per mod per session before its first other event, and after a reload | `sessionId`, `reason` (`start`, `reload`) |
| `turn.complete` | A turn ended (watch only: return `next(e)`) | `sessionId`, `turnId`, `isAborted`, `isFailed`, `failure?`, `agent?`, `model?`, `usage?`, `cost?` |
| `ui.render` | Fleet is about to draw a site the mod hooked | `component`, `sessionId`, `requestId`, `props` |
| `ui.press` | A `Button` the mod drew is pressed | `sessionId`, `mod`, `element` (the key), `component`, `requestId`, `surface` |
| `ui.input` | An `Input` changes or is submitted | as `ui.press`, plus `kind` (`change`, `submit`), `value` |
| `ui.select` | A `Select` changes | as `ui.press`, plus `value` |

A control's own `onPress`, `onSubmit`, `onInput` or `onSelect` runs at the end of the chain. You rarely need
`ui.press` and the others; use them to swallow or rewrite a press (return `{ element }`, or `{ element, value }`).

## What `$` has

`$` is the only way out. Write every call in full as `$.ns.method(...)`.

| Call | Does |
| :- | :- |
| `$.mod.name`, `$.mod.version` | The mod's name; Fleet's number or `"draft"` |
| `$.ui.resolve(e)` | The element factories: `const { Box, Text, Pill } = $.ui.resolve(e)` |
| `$.ui.invalidate("ui.render")` | Draw this mod's sites again (10 a second, coalesced) |
| `$.ui.open({ id, title? })`, `$.ui.close({ id })` | Open or close a pane in this session |
| `$.ui.toast(text, { timeoutMs?, tone? })` | A Fleet notice, 500 characters at most |
| `$.ui.log(text, { level? })` | A line in the mod's log; `fleet_mod_check` and `fleet_mod_test` show it |
| `$.state.get(key)`, `$.state.set(key, value)` | JSON per mod per session. A render hook that reads a key redraws when it's written; it can't write. Lost when the session is archived or the host restarts |
| `$.store.get/set/delete/keys` | JSON per user per mod, saved by Fleet. Async |
| `$.session.id()/title()/harness()/cwd()/surfaces()` | The current session. Async |
| `$.clock.now()`, `.after(ms, fn)`, `.every(ms, fn)` | Timers (100 ms at least, 20 per mod per session). Stop on reload |

There is no `$.fs`, `$.process`, `$.http`, `$.model`, `$.prompt` or `$.tool`, and no `setTimeout`. If the user wants a
mod that needs those, say it isn't possible yet and offer one that draws what it can from the event.

## Elements

Get them from `$.ui.resolve(e)`. A prop not listed here makes the tree invalid.

| Element | Main props |
| :- | :- |
| `Box` | `flexDirection` (`row` default, `column`), `gap`, `padding`, `paddingX`, `paddingY`, `alignItems`, `justifyContent`, `flexWrap`, `flexGrow` (0/1), `width` (a percentage), `borderStyle` (`round`, `single`, `dashed`, `quote`), `borderColor`, `background` (`subtle`, `tint`), `children`. Spacing steps: 0, 1, 2, 3, 4, 6, 8. No fixed sizes. |
| `Text` | `color`, `bold`, `italic`, `strikethrough`, `code`, `dimColor`, `wrap` (`wrap`, `truncate`), `children` (strings, numbers, `Text`) |
| `Pill` | `tone` (`good`, `warn`, `bad`, `neutral`, `accent`), `label`, `icon?` |
| `Icon` | `name`, `color?`, `label?`. Names: `check x alert info circle dot clock loader play skip test bug terminal file folder git-branch search sparkles zap gauge arrow-right chevron-right external-link copy eye eye-off` |
| `Button` | `key`, `label`, `onPress`, `icon?`, `tone?` (`primary`, `danger`, `quiet`), `disabled?` |
| `Input` | `key`, `label?`, `placeholder?`, `value?`, `submitLabel?`, `onSubmit?`, `onInput?` |
| `Select` | `key`, `label?`, `options` (1 to 200 `{ value, label }`, values unique), `value?`, `onSelect` |
| `Markdown` | `text`, `key?`, `dimColor?` |
| `Code` | `source`, `language?`, `path?`, `startLine?`, `format?` (`source`, `diff`), `wrap?` |
| `Page` | `key`, `path`, `title`, `query?`. An `.html` file in the mod's folder, sandboxed, display only |

- **Colours by role only**: `text`, `muted`, `accent`, `good`, `warn`, `bad`. No hex, no named colours.
- **Keys**: every `Button`, `Input`, `Select` and `Page` needs a `key`, unique in the tree: letters, digits, `_`, `-`, `.`, 64 at most.
- **Limits that make the tree invalid**: 2,000 elements, depth 32, 256 KiB as JSON. Text past 100,000 characters is cut.
- `false` and `null` in `children` are fine (`counts.failed > 0 && Pill(...)`).

## What the static check refuses

The check reads the module without running it. A module that breaks these doesn't load, so don't write them:
- Imports other than `import type ... from "fleet-mods"`. No `require`, `import()`, `import.meta`.
- Globals outside plain JavaScript (`Object`, `Array`, `Math`, `JSON`, `Date`, `Map`, `Set`, `Promise`, `RegExp`, `String`, `Number`, `Error`, `Intl`, `structuredClone`, `console`...). Not `globalThis`, `window`, `process`, `fetch`, `setTimeout`, `setInterval`, `eval`, `Function`, `Reflect`, `Proxy`.
- Using `$` any way but `$.ns.method(...)`: no alias, `$[name]`, destructuring, spreading, storing it. A helper that takes `$` must name its parameter `$` too.
- `.constructor`, `.__proto__`, `.prototype` (also as a key to `Object.defineProperty` and the like), `Object.getPrototypeOf`, `setPrototypeOf`, `getOwnPropertyDescriptors`, `with`.
- `on(...)` outside `register`, with a variable event name or matcher, or for an event that isn't in the table above.
- `$.state.get(key)` or `set` with a key that isn't a string literal.
- A module over 512 KiB.

Shared values (regexes, helpers that don't touch `$`) at module level are fine.

## Worked example: test-chips

When the agent runs tests, the row shows passed, failed and skipped counts, and the failing names when opened.

`mod.json`:

```json
{
  "name": "test-chips",
  "version": "0.1.0",
  "description": "Draws test runs as passed, failed and skipped counts",
  "hooks": "./mod.ts"
}
```

`mod.ts`:

```ts
import type { Register } from "fleet-mods";

const TEST_COMMAND = /\b(dotnet test|bun (run )?test|vitest|pytest|go test)\b/;

type Counts = { passed: number; failed: number; skipped: number; failing: string[] };

function parse(output: string): Counts | undefined {
  // dotnet test: "Failed!  - Failed: 2, Passed: 212, Skipped: 4, Total: 218"
  const dotnet = /Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)/.exec(output);
  // vitest: "Tests  2 failed | 212 passed | 4 skipped (218)"
  const vitest = /Tests\s+(?:(\d+) failed \| )?(\d+) passed(?: \| (\d+) skipped)?/.exec(output);
  const failing = [...output.matchAll(/^\s*(?:Failed|FAIL|×)\s+(\S.*)$/gm)].map((m) => m[1].trim()).slice(0, 50);
  if (dotnet) return { failed: +dotnet[1], passed: +dotnet[2], skipped: +dotnet[3], failing };
  if (vitest) return { failed: +(vitest[1] ?? 0), passed: +vitest[2], skipped: +(vitest[3] ?? 0), failing };
  return undefined;
}

export const register: Register = (on) => {
  on("ui.render", { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolUse" && e.component !== "ToolResult") return next(e);
    if (!TEST_COMMAND.test(String(e.props.input.command ?? ""))) return next(e);
    const counts = parse(e.props.output);
    if (!counts) return next(e);

    const { Box, Text, Pill } = $.ui.resolve(e);
    const pills = Box({
      flexDirection: "row",
      gap: 1,
      children: [
        Pill({ tone: "good", label: `${counts.passed} passed` }),
        counts.failed > 0 && Pill({ tone: "bad", label: `${counts.failed} failed` }),
        counts.skipped > 0 && Pill({ tone: "neutral", label: `${counts.skipped} skipped` }),
      ],
    });
    if (e.component === "ToolUse") return pills;

    // Opened: the failing names, then Fleet's own output under them.
    return Box({
      flexDirection: "column",
      gap: 2,
      children: [
        ...counts.failing.map((name) => Text({ code: true, color: "bad", children: [name] })),
        await next(e),
      ],
    });
  });
};
```

Its check report should read:

```text
test-chips 0.1.0 · 47 lines
hooks:  ui.render ["ToolUse","ToolResult"] { props: { tool: ["bash","shell"] } }
calls:  ui.resolve
state:  (none)
pages:  (none)
```

If your report lists a call or state key you didn't mean to use, fix it before testing.

Test it with `fleet_mod_test`:

```json
{
  "name": "test-chips",
  "event": "ui.render",
  "e": {
    "component": "ToolUse",
    "props": {
      "tool": "bash",
      "status": "completed",
      "input": { "command": "dotnet test" },
      "output": "Failed!  - Failed: 2, Passed: 212, Skipped: 4, Total: 218"
    }
  }
}
```

In the answer, check that `Drawn by` names the draft, that the `Result` is a `Box` holding three `Pill`s labelled
"212 passed", "2 failed" and "4 skipped", and that there are no `Failures`. Then send `component: "ToolResult"` with
failing lines in `output` and look for the `Text` children before `{ "type": "Fleet" }`. Also test an output with no
counts: it should come back as `{ "type": "Fleet" }`, so the mod stays out of the way.

If the test says no hook matched, the matcher is wrong (`props.tool` is `bash`, not `Bash`) or the draft didn't load.

## When a mod fails

A hook that throws, times out or returns an invalid tree is a failure; Fleet draws the site as if the mod weren't
there. Three failures in a row, not answered by a `.catch`, turn the draft off in this session. The draft card shows
the error, and `fleet_mod_check` shows the log. Fix the files with `fleet_mod_write`, then `fleet_mod_reload`, which
turns it back on.

A hook that never returns (a busy loop) can't be stopped: Fleet restarts the mod host after 15 s and every mod's
`$.state` is lost. Keep loops bounded.
