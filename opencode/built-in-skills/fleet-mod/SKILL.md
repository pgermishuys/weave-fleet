---
name: fleet-mod
description: Make a Fleet mod, a small change to how Fleet draws something, for the user. Use when the user asks to change how Fleet shows a tool's output, make output readable, add a chip or band to Fleet, or says "mod". You write the mod as a draft, check it in this session, and ask the user to keep it.
---

# Make a Fleet mod

A mod is a folder with two files: `mod.json` and one hooks module. Its hooks run when Fleet is about to draw
something. They return a tree of Fleet's own elements, and Fleet draws it in its theme. A mod can't reach files,
processes or the network.

What you write is a **draft**. It runs only in this session. The user decides whether to keep it.

## The loop

- `fleet_mod_write`: writes the files (paths relative to the mod's folder). Fleet loads the draft and says whether it
  loaded. If not, it says why, with the check's errors at line:column.
- `fleet_mod_check`: the static check's report as JSON.
- `fleet_mod_reload`: loads the draft again. It also turns a draft back on after three failures.
- `fleet_mod_keep`: raises a request on the draft card with a one-line note, and returns at once.

- Never say a mod is kept. The user decides on the card. Don't ask again.
- If a tool says Mods are off, or Fleet started without mods, tell the user and stop.

## mod.json

- `name`: lowercase letters, digits, `-`; 1 to 64; starts with a letter; matches the folder. Not `fleet-...`, not
  `drafts`, not a Windows device name (`con`, `nul`, `com1`...).
- `version`: your version string.
- `description`: one line, 200 characters at most.
- `hooks`: path of the module in the folder: `.js`, `.mjs`, `.ts` or `.mts`.

## Hooks

A module exports `register(on)`; see test-chips below. `on(event, matcher?, async ($, e, next) => ...)`.

- Observe: `return next(e)`. Fleet draws as usual.
- Rewrite: `return next({ ...e, props: { ...e.props, title: "..." } })`. `e` is frozen; pass a copy.
- Answer: return a tree, or `null` to draw nothing, without calling `next`.
- Call `on` directly in `register`, with a string-literal event and an object-literal matcher.
- A matcher compares fields of `e`: a value matches itself, an array any of its values, a RegExp a string.
- `$.ui.resolve(e)` gives the element factories: `const { Box, Text, Pill } = $.ui.resolve(e)`. `$.state`,
  `$.store` and `$.clock` exist too. There is no fs, process, network or `setTimeout`. If the user needs those, say
  it isn't possible yet.
- Events you can use: `ui.render` (draws; a hook has 10 s), `prompt.submit` (changes a prompt; see below, a hook
  has 1.5 s) `turn.complete` (a turn ended; see the next section) and `permission.ask` (refuse an agent's ask; see the last section).
  There are no tool events.

## Changing a prompt: `prompt.submit`

When the user wants to reword what they send, or have the model know something without typing it, hook
`prompt.submit`. `e` is `{ sessionId, text, context }`; `context` is a list of strings, empty at first.

- `return next(e)`: leave it.
- `return next({ ...e, text: "..." })`: reword it. The user's bubble shows the new words, and the agent gets them.
- `return next({ ...e, context: [...e.context, "..."] })`: notes only the model sees. The bubble does not show them.
  Pi ignores them; OpenCode, OpenCode 2 and Claude Code take them. 8 KiB in total.
- Always pass `...e` and keep `text` a non-empty string and `context` a list of strings, or Fleet ignores the answer.
- It fails open: a hook that throws, is slow (1.5 s) or answers badly leaves the prompt as typed. A mod can't block or
  drop a prompt. Slash commands skip it.
- A matcher compares `text`: `{ text: /branch|merge/i }`. Keep hooks fast; the user waits for them.
- `await $.session.branch()` is the git branch in the session's folder, or `null` (no repository, or a detached HEAD).
  Also `$.session.cwd()` and `title()`. Call it only when the matcher passed.

```ts
import type { Register } from "fleet-mods";

export const register: Register = (on) => {
  on("prompt.submit", { text: /branch|\bprs?\b|pull request|push|merge/i }, async ($, e, next) => {
    const branch = await $.session.branch();
    if (!branch) return next(e);
    return next({ ...e, context: [...e.context, `Fleet read the session's git branch just now (git symbolic-ref HEAD in the session folder): ${branch}. It is current; answer from it.`] });
  });
};
```

This is Fleet's `branch-context` sample (`mod.json` like any mod: name `branch-context`, `hooks: "./mod.ts"`).

## After a turn: `turn.complete` and `$.prompt.submit`

When the user wants something to happen once the agent finishes ("run the tests after edits"), hook `turn.complete`.
`e` is `{ sessionId, turnId, isAborted, isFailed, failure?, changedFiles }`. `changedFiles` lists the files the agent's
edit, write and patch tools changed in that turn (relative to the session's folder; `[]` on Claude Code and Pi; a shell
`sed` isn't seen). The hook only watches: always `return next(e)`.

- `await $.prompt.submit({ text: "..." })` queues a message in the session. It shows in the queue list and is sent next.
  It rejects with Fleet's reason when refused, so `try`/`catch` it.
- A mod may queue 3 messages in a row; the 4th is refused until the user sends one of their own. Check `isAborted`
  and `isFailed` first, or the mod keeps nagging after a Stop or a failure.
- Fleet waits 2 s for the hooks, then sends the queue. Keep hooks quick.

```ts
import type { Register } from "fleet-mods";

export const register: Register = (on) => {
  on("turn.complete", async ($, e, next) => {
    if (e.changedFiles.length > 0 && !e.isAborted && !e.isFailed) {
      try {
        await $.prompt.submit({ text: "Run the tests" });
      } catch (err) {
        $.ui.log(`Could not queue the tests: ${err instanceof Error ? err.message : String(err)}`, { level: "warn" });
      }
    }
    return next(e);
  });
};
```

This is Fleet's `tests-after-edits` sample (`mod.json` like any mod).

## The two sites

`e.component` is `ToolUse` or `ToolResult`. `e.props` is the tool row.

- `ToolUse` draws on the row's line, after Fleet's label. **Inline only**: `Text`, `Pill`, `Icon`, `Button`, or a
  row `Box` holding only those.
- `ToolResult` draws the body shown when the row is opened.
- `next(e)` resolves to what the rest of the chain draws. At `ToolUse` that is nothing. At `ToolResult` it is Fleet's
  body. To keep it, put `await next(e)` in your `Box`, once.
- `tool`: Fleet's canonical name, the same on every harness: `bash`, `read`, `edit`, `task`. Match on this, not on
  `rawTool`.
- Also `status` (`pending`, `running`, `completed`, `error`, `cancelled`), `title`, `input`, `output` (the output
  text) and `durationMs`.
- A row is drawn when first shown and when its status changes, never on each streamed line.

## Elements

A prop not listed makes the tree invalid.

| Element | Props |
| :- | :- |
| `Box` | `flexDirection` (`row`, `column`), `gap`, `padding`, `paddingX`, `paddingY`, `alignItems`, `justifyContent`, `flexWrap`, `flexGrow`, `width` (percent), `borderStyle`, `borderColor`, `background` (`subtle`, `tint`), `children`. Spacing: 0, 1, 2, 3, 4, 6, 8 |
| `Text` | `color`, `bold`, `italic`, `strikethrough`, `code`, `dimColor`, `wrap`, `children` |
| `Pill` | `tone` (`good`, `warn`, `bad`, `neutral`, `accent`), `label`, `icon?` |
| `Icon` | `name`, `color?`, `label?` (`check x alert info circle dot clock loader play test bug terminal file folder search sparkles zap`...) |
| `Button` | `key`, `label`, `onPress`, `icon?`, `tone?` (`primary`, `danger`, `quiet`; not Pill's tones), `disabled?` |
| `Input`, `Select` | `key`, `label?`, `value?`, plus `onSubmit`/`onInput` or `options` and `onSelect` |
| `Markdown`, `Code` | `text`; `source`, `language?`, `path?`, `startLine?`, `format?` (`source`, `diff`) |

- Colours by role only: `text`, `muted`, `accent`, `good`, `warn`, `bad`. No hex, no names.
- Every `Button`, `Input` and `Select` needs a `key`, unique in the tree: letters, digits, `_`, `-`, `.`, 64 at most.
- Limits: 2,000 elements, depth 32, 256 KiB as JSON. `false` and `null` in `children` are fine.

## Buttons

A `Button` (or `Input`, `Select`) works at every site. Give it a `key` and a callback; Fleet runs the callback in your
module when the user uses the control.

- `Button({ key: "go", label: "Go", onPress: () => { ... } })`. The callback is a closure over `$`, so it can write
  `$.state` or call `$.prompt.submit`. A `ui.render` hook can read `$.state` but not write it.
- Keep what the tree shows in `$.state` (per mod, per session). Writing a key the render read draws the site again, so
  a press that changes the count redraws the row with no other call. `$.ui.invalidate("ui.render")` draws again by hand.
- A press on a tree that was since drawn again (the mod reloaded) does nothing; Fleet draws the current tree.
- `Input`: `onInput` gets changes (at most 4 a second), `onSubmit` the submit. `Select`: `onSelect`. The value is the
  callback's first argument.
- Hooks on `ui.press`, `ui.input` and `ui.select` see the event first (`e.element` is the control's `key`); you
  rarely need them.

```ts
import type { Register } from "fleet-mods";

export const register: Register = (on) => {
  on("ui.render", { component: "ToolResult", props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolResult") return next(e);
    const { Box, Text, Button } = $.ui.resolve(e);
    const presses = $.state.get<number>("presses") ?? 0;
    return Box({
      flexDirection: "column",
      gap: 2,
      children: [
        Box({
          flexDirection: "row",
          gap: 2,
          alignItems: "center",
          children: [
            Button({ key: "count", label: "Count a press", tone: "primary", onPress: () => { $.state.set("presses", ($.state.get<number>("presses") ?? 0) + 1); } }),
            Text({ dimColor: true, children: [`Pressed ${presses} times`] }),
          ],
        }),
        await next(e),
      ],
    });
  });
};
```

This is Fleet's `press-counter` sample (it reads the count again inside the callback, so quick presses don't lose one).

## What the static check refuses

A module that does these doesn't load:
- Imports other than `import type ... from "fleet-mods"`. No `require`, `import()`, `import.meta`.
- `globalThis`, `window`, `process`, `fetch`, `setTimeout`, `eval`, `Function`, `Reflect`, `Proxy`.
- `$` used any way but `$.ns.method(...)`: no alias, `$[name]`, destructuring, spreading. A helper that takes `$` must
  name its parameter `$`.
- `.constructor`, `.__proto__`, `.prototype`, also as keys to `Object.defineProperty` and the like;
  `Object.getPrototypeOf`, `setPrototypeOf`, `getOwnPropertyDescriptors`; `with`.
- `on(...)` outside `register`, or with a variable event name or matcher.
- `$.state.get` or `set` with a key that isn't a string literal.
- A module over 512 KiB.

## Worked example: test-chips

When the agent runs tests, the row shows passed, failed and skipped counts. Opened, it lists the failing names.

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

Its check should list this hooks line:

```text
hooks:  ui.render ["ToolUse","ToolResult"] { props: { tool: ["bash","shell"] } }
```

Ask to keep it with a one-line note once it loads.

## Refusing an agent's ask: `permission.ask`

When the user wants a command or edit refused outright ("never let the agent push from main"), hook `permission.ask`.
`e` is `{ sessionId, requestId, kind, tool, title, patterns }`: `kind` is `shell`, `edit`, `web`, `read` or `other`, and
`title` is the command for a shell ask. Match on `kind`/`tool`/`title` and decide in the hook.

- Return `{ decision: "deny", reason: "..." }` to refuse. The user is not asked; the agent is told
  `<mod name>: <reason>` and can change course. `reason` is required, at most 500 characters.
- Return `next(e)` to let the user answer as usual. Anything else (a throw, no reason, a slow hook) also lets the user
  answer. There is no `allow` yet.
- It only sees asks that would reach the user, so the session's permission level (Settings → Permissions) must be `Ask`
  or `Edits`; at `Allow everything` nothing asks and the hook never runs. Say so if the user's mod seems to do nothing.
- A hook has 1.5 s and Fleet waits 2 s. Keep it quick; `$.session.branch()` is fine.

```ts
import type { Register } from "fleet-mods";

export const register: Register = (on) => {
  on("permission.ask", { kind: "shell", title: /\bgit\s+push\b/ }, async ($, e, next) => {
    if ((await $.session.branch()) === "main") return { decision: "deny", reason: "Don't push from main. Make a branch and push that instead." };
    return next(e);
  });
};
```

This is Fleet's `push-guard` sample.
