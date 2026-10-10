# Fleet mods: the Stage 1 API

This is the contract every Stage 1 PR builds against: what a mod is, what it can hook and call, what Fleet draws, how
Fleet talks to the mod host, and where mods live. The types are in [`mods/types/fleet-mods.d.ts`](../../mods/types/fleet-mods.d.ts);
when the two disagree, the types win and this page is the bug.

Fleet mods mirror [Claude Code's mods](https://code.claude.com/docs/en/plugins/mods/overview). Where the concept is the
same, the name is the same: `register(on, options)`, `on(event, matcher?, hook)`, hooks as `($, e, next)` middleware,
`$` as the only way out, `ui.render`/`ui.press`/`ui.input`/`ui.select`, `$.ui.resolve(e)`, `$.state`, `$.store`,
`$.clock`, `.catch`, `next.called`, `next.error`. Stage 1 is the subset that draws: no prompt, tool or permission
events, and nothing that reaches files, processes, the network or models. Those come in Stage 2 with the `calls:`
review.

- [A mod on disk](#a-mod-on-disk)
- [Hooks](#hooks)
- [Events](#events)
- [Render sites](#render-sites)
- [`$` in Stage 1](#-in-stage-1)
- [Elements](#elements)
- [The static check](#the-static-check)
- [Order and failure](#order-and-failure)
- [Drafts, Keep and Undo](#drafts-keep-and-undo)
- [Where mods live](#where-mods-live)
- [The protocol](#the-protocol)
- [Fleet and the browser](#fleet-and-the-browser)
- [Limits](#limits)
- [Worked example: test-chips](#worked-example-test-chips)
- [Differences from Claude Code](#differences-from-claude-code)

## A mod on disk

A mod is a folder named after it with two files:

```text
test-chips/
├── mod.json
└── mod.ts
```

`mod.json`:

```json
{
  "name": "test-chips",
  "version": "0.1.0",
  "description": "Draws test runs as passed, failed and skipped counts",
  "hooks": "./mod.ts"
}
```

| Field | Required | |
| :- | :- | :- |
| `name` | Yes | Lowercase letters, digits and `-`, 1 to 64, starting with a letter. Matches the folder. `fleet-` is reserved. |
| `version` | Yes | The author's version string. Fleet numbers kept versions itself (`v1`, `v2`…); this one is shown beside it. |
| `description` | Yes | One line, 200 characters at most. Settings → Mods and the review dialog show it. |
| `hooks` | Yes | Path to the hooks module, relative to `mod.json`, inside the folder. `.js`, `.mjs`, `.ts` or `.mts`. |

The hooks module is one ES module file that exports `register`. It may `import type` from `fleet-mods`; it may not
import anything else (see [the static check](#the-static-check)). A mod can also ship `.html` pages (and their CSS,
images and scripts) for the [`Page`](#elements) element, anywhere inside its folder.

## Hooks

```ts
import type { Register } from "fleet-mods";

export const register: Register = (on, options) => {
  on("ui.render", { component: "ToolUse", props: { tool: "bash" } }, async ($, e, next) => {
    // observe:  return next(e)
    // rewrite:  return next({ ...e, props: { ...e.props, title: "…" } })
    // answer:   return a tree (or null) without calling next
    return next(e);
  });
};
```

- `register(on, options)` runs each time the module loads. `options` is `{}` in Stage 1.
- `on(event, matcher?, hook)` registers a hook and returns a registration with `.catch(handler)`. Call it synchronously
  inside `register`, with a string literal event and, if any, an object-literal matcher: the static check reads both.
  Registering one event twice without a matcher refuses the module.
- A **matcher** is an object compared field by field with `e`: a value matches itself, an array any of its values, a
  RegExp a string by pattern, and a nested object a nested field the same way. `{ component: "ToolUse", props: { tool:
  ["bash", "shell"] } }` runs only for shell rows.
- `e` is deeply frozen plain data. To change it, pass a copy to `next`.
- `next(e)` runs the rest of the chain, later mods and then Fleet's own behaviour, and resolves to its result.
  `next.signal` aborts when the dispatch is abandoned, `next.budget` reads the hook's time limit, `next.event` names
  the event.
- `.catch(handler)` runs when the hook throws, times out or returns something the event doesn't take. Inside it,
  `next.error` is `{ kind: "throw" | "timeout", message }` and `next.called` says whether the hook had called `next`
  (if so, `next(e)` resolves to that result again without re-running the chain).

## Events

| Event | Fires when | `e` | A hook can return |
| :- | :- | :- | :- |
| `session.start` | Once per mod per session, before the mod's first other event in it, and again after the mod reloads | `sessionId`, `reason` (`start` or `reload`) | `next(e)` |
| `turn.complete` | A turn ended: finished, stopped by the user (`isAborted`) or failed (`isFailed`) | `sessionId`, `turnId`, `isAborted`, `isFailed`, `failure?`, `agent?`, `model?`, `usage?` (`input`, `output`, `cacheRead`, `cacheWrite`), `cost?` | `next(e)` (watch only) |
| `ui.render` | Fleet is about to draw a [render site](#render-sites) the mod hooked | `component`, `sessionId`, `requestId`, `props` | A tree, `null` to draw nothing, or `next(e)` |
| `ui.press` | A `Button` the mod drew is pressed | `sessionId`, `mod` (the name of the mod that drew it), `element` (the key), `component`, `requestId`, `surface` | `next(e)`, or `{ element }` to swallow the press |
| `ui.input` | An `Input` changes (`kind: "change"`, at most 4 a second) or is submitted (`kind: "submit"`) | as `ui.press`, plus `kind`, `value` | `next({ ...e, value })`, or `{ element, value }` to swallow it |
| `ui.select` | A `Select` changes | as `ui.press`, plus `value` | `next({ ...e, value })`, or `{ element, value }` to swallow it |

Fleet sends `session.start` when a session is opened on any screen or starts a turn; the host runs each mod's hook at
most once per loaded module per session, and runs it first if another event for that session arrives before it.

At the end of a `ui.press`/`ui.input`/`ui.select` chain, Fleet's own behaviour is the control's callback (`onPress`,
`onSubmit`/`onInput`, `onSelect`) in the mod that drew it. Another mod's hook on the same event runs first, as in
Claude Code.

## Render sites

| Site | Where Fleet draws it | `requestId` | `e.props` |
| :- | :- | :- | :- |
| `ToolUse` | On a tool row's line, after Fleet's label, desktop and phone. Inline elements only. | The tool call id | [Tool row props](#tool-row-props) |
| `ToolResult` | The body a tool row shows when opened, in place of Fleet's. | The tool call id | [Tool row props](#tool-row-props) |
| `ComposerBand` | A band above the composer (desktop: between the background strip and the queued messages; phone: above the phone composer). Shared by every mod. | The session id | `isWorking` |
| `StatusChip` | A chip at the end of the status bar, for the session on screen. Inline elements only. Nothing when no session is on screen. | The session id | none |
| `Pane` | A pane the mod opened with `$.ui.open`: a `mod` canvas in the session's content panel on desktop, a sheet from the session menu on the phone | The pane's id | `title` |

`next(e)` at a site resolves to what the rest of the chain draws: another mod's tree, or `{ type: "Fleet" }`, which is
Fleet's own drawing of the site (nothing at `ToolUse`, `ComposerBand` and `StatusChip`; Fleet's body at
`ToolResult`). A tree replaces what later mods draw; to keep theirs (or Fleet's body), put `await next(e)` inside a
`Box` of yours, at most once. `null` draws nothing (at `ToolResult`, an empty body).

**Inline elements** are `Text`, `Pill`, `Icon`, `Button` and a `Box` with `flexDirection: "row"` holding only those.
Anything else at `ToolUse` or `StatusChip` makes the tree invalid.

**When a site is drawn.** Fleet renders a tool row's two sites when the row is first shown, when the call's status
changes (pending → running → completed/error/cancelled) and when the mod invalidates; never on streamed output, so a
running call's `output` is what had arrived when its status last changed. `ComposerBand` and `StatusChip` are rendered
when the session is opened and when the mod invalidates. Renders are per session, not per screen: the desktop, the
phone and other tabs showing the session get the same tree.

### Tool row props

| Field | |
| :- | :- |
| `tool` | The canonical name from Fleet's tool registry (`client/src/lib/tools/registry.ts`), the same whichever harness made the call: `bash`, `read`, `edit`, `task`, `fleet_page_show`… |
| `rawTool` | The name as the harness sent it: `Bash`, `mcp__fleet__fleet_page_show` |
| `category` | `read`, `search`, `edit`, `shell`, `web`, `subagent`, `question`, `plan`, `skill`, `fleet` or `other` |
| `status` | `pending`, `running`, `completed`, `error` or `cancelled` |
| `title` | The row's title as Fleet shows it |
| `input`, `inputTruncated` | The call's arguments; `{}` and `true` when larger than 64 KiB of JSON |
| `output`, `outputTruncated` | The output (or error) text; the last 256 KiB and `true` when longer |
| `durationMs` | When the call has finished |

## `$` in Stage 1

`$` is the only way out of a mod. Write each call in full, `$.ns.method(…)`; the static check lists them.

| Namespace | Methods | Notes |
| :- | :- | :- |
| `$.mod` | `name`, `version` | `version` is Fleet's number, or `"draft"` |
| `$.ui` | `resolve(e)` | The element factories, as in Claude Code: `const { Box, Text, Pill } = $.ui.resolve(e)`. One set serves desktop and phone. |
| | `invalidate("ui.render")` | Draw this mod's sites again: in the current session, or in every session when called outside one. Throttled to 10 a second; calls in between coalesce. |
| | `open({ id, title? })`, `close({ id })` | Open (or bring forward) and close a pane in the current session. A `Pane` hook draws it. |
| | `toast(text, { timeoutMs?, tone? })` | A Fleet notice on the screens showing the session, titled with the mod's name. |
| | `log(text, { level? })` | A line in the mod's log: Settings → Mods shows it, and `fleet_mod_check`/`fleet_mod_test` return it to the agent. |
| `$.state` | `get(key)`, `set(key, value)` | Reactive JSON values for one mod in one session. A `ui.render` hook that reads a key redraws when it's written; it can't write. Survives a reload of the module; dropped when the session is archived or the host restarts. Keys are string literals. |
| `$.store` | `get`, `set`, `delete`, `keys` | Per user, per mod, saved by Fleet; a draft shares the store of the kept mod with its name. Async. |
| `$.session` | `id()`, `title()`, `harness()`, `cwd()`, `surfaces()` | The session of the current event, timer or callback. Async. `surfaces()` is where it's open now: `desktop`, `phone`, or empty. |
| `$.clock` | `now()`, `after(ms, fn)`, `every(ms, fn)` | Timers belong to the mod and session they were started in, and stop when the module reloads or the session is archived. `every` at 100 ms at least. |

There is no `$.fs`, `$.process`, `$.http`, `$.model`, `$.prompt` or `$.tool` in Stage 1, and no `setTimeout`: use
`$.clock`.

## Elements

A tree is data that Fleet draws with its own Vue components, in its theme, on desktop and phone. A mod never runs
script in Fleet's page. Callbacks stay in the host; the tree that reaches Fleet carries a handle for each.

| Element | Props | Notes |
| :- | :- | :- |
| `Box` | `key`, `flexDirection` (`row`, the default, or `column`), `gap`, `padding`, `paddingX`, `paddingY`, `alignItems`, `justifyContent`, `flexWrap`, `flexGrow` (0/1), `width` (a percentage), `borderStyle` (`round`, `single`, `dashed`, `quote`), `borderColor`, `background` (`subtle`, `tint`), `children` | Spacing in Fleet's steps (1 = 4 px): 0, 1, 2, 3, 4, 6, 8. No fixed sizes, so it fits the phone. |
| `Text` | `color`, `bold`, `italic`, `strikethrough`, `code` (monospace), `dimColor`, `wrap` (`wrap`/`truncate`), `children` | Children are strings, numbers and `Text`. |
| `Pill` | `tone` (`good`, `warn`, `bad`, `neutral`, `accent`), `label`, `icon?` | Fleet's status pill. |
| `Icon` | `name`, `color?`, `label?` | A fixed set of names Fleet maps to its icons: `check`, `x`, `alert`, `info`, `circle`, `dot`, `clock`, `loader`, `play`, `skip`, `test`, `bug`, `terminal`, `file`, `folder`, `git-branch`, `search`, `sparkles`, `zap`, `gauge`, `arrow-right`, `chevron-right`, `external-link`, `copy`, `eye`, `eye-off`. |
| `Button` | `key`, `label`, `onPress`, `icon?`, `tone?` (`primary`, `danger`, `quiet`), `disabled?` | |
| `Input` | `key`, `label?`, `placeholder?`, `value?`, `submitLabel?`, `onSubmit?`, `onInput?` | `value` is what the field holds when drawn. |
| `Select` | `key`, `label?`, `options` (1–200 `{ value, label }`, values unique), `value?`, `onSelect` | |
| `Markdown` | `text`, `key?`, `dimColor?` | Fleet's conversation Markdown. Links open in a new tab. |
| `Code` | `source`, `language?`, `path?`, `startLine?`, `format?` (`source`/`diff`), `wrap?` | |
| `Page` | `key`, `path`, `title`, `query?` | An `.html` file in the mod's folder, drawn sandboxed like a conversation page (`ConversationPage.vue`): opaque origin, Fleet's theme as `--fleet-*` variables, sized to fit. `query` becomes its query string: at most 4 KiB URL-encoded. It can't call back into the mod in Stage 1. |

**Colours by role only**: `text`, `muted`, `accent`, `good`, `warn`, `bad`. No hex, no named colours, so trees follow
the theme.

**Keys**: every control (`Button`, `Input`, `Select`) and every `Page` needs a `key`, unique in the tree: letters,
digits, `_`, `-` and `.`, up to 64.

**Invalid trees.** An element or prop not listed here, a missing key, a duplicate key, a non-inline element at an
inline site, or a tree over the [limits](#limits) makes the whole tree invalid. Fleet draws the site as if the mod
weren't there and counts a failure (`kind: "throw"`, with the reason, which the mod's log and the agent tools show).
Text past 100,000 characters per tree is cut rather than refused. What counts is everything drawn as text, in tree order
(an element's own text before its children's): child strings and numbers, `Markdown` `text`, `Code` `source`, the `label`
of `Pill`, `Icon`, `Button`, `Input` and `Select`, each `Select` option's `label`, `Input` `placeholder`, `submitLabel`
and `value`, and `Page` `title`. The string that crosses the limit is cut there; every later one is emptied.

## The static check

Before a module loads, the host reads it without running it, as `claude plugin validate` does. Keep shows the report as
"what this mod touches", and `fleet_mod_check` returns it to the agent.

The report (`CheckReport` in the types) lists:

- `hooks:` every `on(…)` call, with its matcher (`ui.render ToolUse { props: { tool: "bash" } }`)
- `calls:` every `$` call as `ns.method`, sorted, once each (`state.get`, `state.set`, `ui.invalidate`, `ui.resolve`)
- `state:` every `$.state` key
- `pages:` every `Page` path, each checked to exist inside the mod's folder
- the name, version, description, line count and sha256 of the module, and errors and warnings with line and column

A module **doesn't load** when:

- `mod.json` is missing, invalid, or names a module outside the folder
- it can't be parsed (TypeScript is stripped first, then parsed as an ES module)
- it imports anything other than `import type … from "fleet-mods"`, or uses `require`, dynamic `import()` or
  `import.meta`
- it reads a global outside JavaScript's own (`Object`, `Array`, `Math`, `JSON`, `Date`, `Map`, `Set`, `Promise`,
  `RegExp`, `String`, `Number`, `Boolean`, `Symbol`, `Error` and its kinds, `Intl`, `parseInt`, `parseFloat`, `isNaN`,
  `isFinite`, `encodeURIComponent`, `decodeURIComponent`, `structuredClone`, `console` (to the mod's log), `undefined`,
  `NaN`, `Infinity`). Refused by name: `globalThis`, `self`, `window`, `global`, `process`, `Bun`, `Deno`, `fetch`,
  `WebSocket`, `XMLHttpRequest`, `Worker`, `setTimeout`, `setInterval`, `queueMicrotask`, `eval`, `Function`,
  `Reflect`, `Proxy`, `WebAssembly`, `SharedArrayBuffer`, `Atomics`
- it reaches `$` other than as `$.ns.method(…)`: aliasing it, `$[name]`, destructuring, spreading, storing it, or
  passing it to a function whose parameter isn't also named `$`
- it reads `.constructor`, `.__proto__` or `.prototype` of anything (or the old `__lookupGetter__` family), reaches
  them through `Object` (`Object.getOwnPropertyDescriptor(x, "constructor")`, `defineProperty`, `create` with such a
  key), calls `Object.getPrototypeOf`, `setPrototypeOf` or `getOwnPropertyDescriptors`, or uses `with`
- `on` is called outside `register`, with a non-literal event name or a non-literal matcher, or for an event Stage 1
  doesn't have
- a `$.state` key isn't a string literal
- it has more than 2,000 nested scopes or is over 512 KiB

The check reads what a mod names; it can't see what a mod builds at run time (a key made of two strings, say). So
before any mod is imported, the host also locks its own JavaScript realm down (SES's `lockdown`): every built-in object
and prototype is frozen, so no mod can change `Object.prototype` or `Promise` under the host or another mod; the
constructor of every kind of function is inert, so no string can be turned into code; and the realm's own `Function`
and `eval` are gone. Together they make "`$` is the only way out" hold for code that only computes and draws.

It is still not a sandbox: every mod runs in the host's process, as the same user, with the user's permissions, as
Claude Code's mods do, and a hook that never yields blocks every other mod until Fleet restarts the host. Fleet starts
the host with an empty environment (no Fleet tokens) and its working folder in the mods data folder.

## Order and failure

**Order.** Hooks on one event form one chain, outermost first: kept mods by name, then the session's drafts by name. A
draft with a kept mod's name takes that mod's place in its session. Within a module, hooks run in the order `register`
called `on`.

**Budget.** A hook has 10 s of its own time per dispatch; time inside `next` or a `$` call doesn't count. A `.catch`
handler has 1 s. Past its budget a hook is skipped.

**A failure** is a hook that throws, times out, or returns what the event doesn't take (an invalid tree). With no
`.catch`, a hook that failed before calling `next` is skipped and the next handler runs in its place; one that failed
after `next` resolved leaves that result standing. A timer or a callback that throws is a failure too.

**Three strikes.** Three failures in a row from one mod, not answered by `.catch`, turn it off; any successful hook
resets the count. A kept mod is turned off for every session, with the error in Settings → Mods, until the user turns it
on again. A draft is turned off in its session, with the error on its draft card, until the agent reloads it.

**A blocked host.** A hook that never yields (a busy loop) can't be stopped inside the host. Fleet waits 15 s for a
dispatch, then restarts the host and strikes the mod that was running. All mods reload; `$.state` is lost and
`session.start` fires with `reason: "reload"`.

## Drafts, Keep and Undo

- **A draft** is a mod the agent is writing. It lives in a folder of its session's, loads for that session only, and its
  hooks only get that session's events. Its trees show wherever the session is open (desktop, phone, other tabs);
  `StatusChip` and `ComposerBand` trees from a draft are marked Draft. Saving the folder reloads it.
- **Keep** copies the draft into a new immutable version (`v1`, then `v2`…), makes it the active version, removes the
  draft and loads the version for every session of the user on this machine. Before Keep, Fleet shows the
  [check report](#the-static-check).
- **Undo** makes the previous version active. Undo on `v1` turns the mod off. Versions are never deleted by Undo.
- **Turn off** stops a mod at once, with no turn: a draft for its session, a kept mod everywhere.
- **Start without mods** (a menu item, and `?mods=off` on any Fleet address) stops the host and keeps it stopped until
  Fleet restarts or the user turns mods back on. It is a server-side flag, so no mod runs anywhere while it's set.
- **The Mods switch** (Settings → Experimental, off by default) gates all of it. With it off, nothing changes, no host
  runs and no Bun is downloaded.

## Where mods live

Under Fleet's data folder (beside `skills/`), per user, where `{user16}` is the first 16 hex characters of the SHA-256 of
the user id, as `FileSkillVersionStore` does:

```text
{data}/mods/{user16}/
├── test-chips/
│   ├── versions.json        the index: versions, why each was made, the active one, on/off
│   ├── store.json           $.store for this mod
│   ├── v1/                  immutable once written
│   │   ├── mod.json
│   │   └── mod.ts
│   └── v2/…
└── drafts/
    └── {sessionId}/
        └── test-chips/
            ├── mod.json
            └── mod.ts
```

`versions.json`:

```json
{
  "name": "test-chips",
  "active": 2,
  "off": null,
  "versions": [
    { "number": 1, "createdAt": "2026-10-10T09:12:00Z", "version": "0.1.0", "sha256": "…", "sessionId": "…", "sessionTitle": "Make test output readable", "note": null, "check": { "…": "the CheckReport" } },
    { "number": 2, "createdAt": "2026-10-11T14:03:00Z", "version": "0.2.0", "sha256": "…", "sessionId": "…", "sessionTitle": "…", "note": "show failing names", "check": { "…": "…" } }
  ]
}
```

`off` is `null`, `{ "by": "user", "at": … }` or `{ "by": "strikes", "at": …, "error": "…" }`. Writes are atomic (write a
temporary file, then move it), as in `FileSkillVersionStore`.

## The protocol

Fleet starts one host process when the Mods switch is on, Start without mods isn't set, and at least one mod is kept
and on or drafted. It runs `{bun} {fleet}/mods-host/host.js --stdio` with the Bun Fleet installed (see M4), restarts it
with backoff when it dies, and stops it on shutdown.

JSON-RPC 2.0 over stdin/stdout, one JSON object per line, UTF-8, 8 MiB per line at most. Both sides send requests,
responses and notifications. The host's stderr goes to Fleet's log. The types are the `fleet-mods/protocol` module.

**Fleet → host (requests)**

| Method | Params | Result |
| :- | :- | :- |
| `initialize` | `protocol: 1`, `fleetVersion` | `protocol`, `hostVersion`, `bunVersion`; error `-32000` for an unknown protocol |
| `check` | `root`, `manifest` | `CheckReport` |
| `load` | `id` (`name@v3` or `name@draft:{sessionId}`), `name`, `version`, `sessionId?`, `root` | `check`, `hooks` (what `register` registered); error `-32001` with the report when it doesn't load. Loading an `id` again reloads it. |
| `unload` | `id` | `{}` |
| `dispatch` | `event`, `sessionId`, `e`, `mods` (the chain, outermost first), `surface?` | `result`, `drawnBy?`, `failures` |
| `forget` | `sessionId` | `{}`: drop the session's `$.state`, timers and handles |
| `shutdown` | `{}` | `{}`, then the host exits 0 within 2 s |

Fleet only dispatches an event when a mod in the chain registered for it (by event and matcher), so streamed text and
unhooked events never cross the pipe. A `dispatch` for `ui.press`/`ui.input`/`ui.select` carries the handle in `e` and
ends in that callback.

**Host → Fleet (requests).** `$` calls that need Fleet, named after the call, each with `mod` and `sessionId`:
`store.get`, `store.set`, `store.delete`, `store.keys`, `ui.open`, `ui.close`, `ui.toast`, `session.get`. `$.state`,
`$.clock` and `$.ui.resolve` never leave the host.

**Host → Fleet (notifications).** `invalidate` (`mod`, `sessionId?`), `log` (`mod`, `sessionId?`, `level`, `text`),
`failed` (a timer or callback failure: `mod`, `event`, `kind`, `message`, `strikes`, `sessionId?`).

**Trees on the wire** are `WireElement`s: children flattened, and each callback replaced by a `handles` entry
(`{ "onPress": "h17" }`) that the host keeps until the site is drawn again or the session is forgotten. A `Page`
carries `mod`, the id of the mod whose folder it comes from (it may be an inner mod's, under another mod's `Box`): the
host takes it from its own record of who made each element, never from the mod.

**Versioning.** `protocol: 1` is this page. A change either side can't ignore bumps it, and the host refuses another
value.

## Fleet and the browser

These shapes are for M5 and M6; mods don't see them.

- **`mod.ui`** (a domain event on the session's topic): `{ site, requestId, tree, mods: [{ name, draft }] }`, where
  `tree` is a `WireElement` or `null`, and `null` with no `mods` means "no mod draws here any more". The client
  contributes it to the site's contribution point (`toolRowViews`, `composerBands`, `statusChips`, or a `mod` canvas).
- **`mod.pane`**: `{ id, title, open, mod }`, opening or closing a pane.
- **`mods.changed`**: a mod was kept, undone, turned on or off, or failed three times. Clients refetch `/api/mods`.
- **`POST /api/sessions/{id}/mods/render`** `{ site, requestIds }`: the client shows rows Fleet hasn't rendered for
  this session yet (after a reload). Answers arrive as `mod.ui` events.
- **`POST /api/sessions/{id}/mods/action`** `{ handle, kind: "press" | "input" | "submit" | "select", value?, surface }`:
  a control was used. The host knows each handle's mod and key, and fills `mod` and `element` in the event itself.
- `$.ui.toast` reaches the browser as a notice through the existing notices path.

## Limits

| Limit | Value |
| :- | :- |
| A hook's own time per dispatch | 10 s |
| A `.catch` handler's own time | 1 s |
| Fleet's wait for a whole dispatch, before it restarts the host | 15 s |
| Failures in a row that turn a mod off | 3 |
| Text drawn per tree | 100,000 characters; the rest is cut |
| Elements per tree | 2,000 |
| Tree depth | 32 |
| A tree as JSON | 256 KiB; larger is invalid |
| `$.store` per mod per user | 4 MiB of JSON |
| `$.state` per mod per session | 1 MiB of JSON |
| `$.ui.invalidate` redraws | 10 a second per mod per session; calls in between coalesce |
| `$.clock.every` | 100 ms at least; 20 timers per mod per session |
| `ui.input` changes | 4 a second per field |
| `$.ui.toast` text | 500 characters |
| Tool row `input` / `output` | 64 KiB of JSON / the last 256 KiB |
| A hooks module | 512 KiB, 2,000 nested scopes |
| Keys and pane ids | Letters, digits, `_`, `-` (and `.` for keys), up to 64 |

## Worked example: test-chips

The first mod. When the agent runs tests, the row shows passed, failed and skipped counts, and the failing names when
opened. Only the parsing is cut down here; the sample in `mods/samples/test-chips/` (M9) handles more runners.

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

Its check report:

```text
test-chips 0.1.0 · 47 lines
hooks:  ui.render ["ToolUse","ToolResult"] { props: { tool: ["bash","shell"] } }
calls:  ui.resolve
state:  (none)
pages:  (none)
```

## Differences from Claude Code

Deliberate, so a mod author coming from Claude Code isn't surprised:

- **A mod is `mod.json` plus one module**, not a plugin with `.claude-plugin/plugin.json` and `hooks/hooks.json`.
- **Sites are Fleet's.** `ToolUse` and `ToolResult` keep Claude Code's names. `ComposerBand` is Claude Code's
  `AbovePrompt`; `StatusChip` is new. There is no `e.surface` on `ui.render`: one tree serves desktop and phone.
- **`$.ui.log` goes to the mod's log**, not the transcript. Fleet has no row for a mod's line yet.
- **`$.state` is plain `get`/`set` with literal keys.** No `atom`/`read`/`update` helpers and no `types/index.d.ts`
  declaration; the check lists the keys instead.
- **Elements are Fleet's**: no `Link`, `Svg`, `Client`, `Raster` or `Image`; `Pill`, `Icon` and `Page` added; colours by
  role; no `hotkey`, `action`, `autoFocus` or `plain` on controls. `Page` is display-only (no `ui.message` back).
- **No tiers** (`prepend`, `append`, `builtin`) and no `next.to`, `next.origin` or `next.is`: one user, kept then
  drafts.
- **A hook can't block the host forever**: Fleet restarts the host after 15 s, which Claude Code's worker model doesn't
  need.
