# snappiness

A repeatable check of how quickly Fleet's UI responds, on a scratch Fleet seeded with invented data, comparing builds.

```powershell
cd tests/snappiness
bun install
bun x playwright install chromium        # once
bun run.ts                                # v0.60.0 vs this checkout, CPU 1x, 5 runs x 2 rounds (~35 min)
bun run.ts --refs v0.65.0,HEAD --throttle 1,4 --runs 5 --rounds 2
bun run.ts --refs HEAD --only typing,switchLong --runs 3      # just measure this checkout
```

Results land in `C:\fleet-scratch\snappiness\results` (`--work` to move it): one JSON per build, throttle and round,
a CPU profile per scenario, and `compare.md` / `compare.json` with median and p95 for each metric, old vs new.

## Safety

Every scratch Fleet runs with its own profile folder (HOME, USERPROFILE, APPDATA, LOCALAPPDATA and XDG_* point
inside `--work`), its own databases, `127.0.0.1` and its own port (7311 and up; never 6262). It never touches the
installed Fleet or its data. `run.ts` stops the Fleets it started; `taskkill /F /T /PID <pid>` cleans up after a crash.

## What it seeds (`lib/seed.ts`, `lib/content.ts`)

Six git projects, 150 sessions (3 long ones of 60 turns with 4 to 10 tool calls each, 12 of 10 turns, the rest short),
markdown answers with code blocks and tables, tool calls with real-looking output and diffs. Four sessions are primed to
stream slowly on their next prompt. Everything is invented and deterministic (seeded PRNG). It goes through the real API
in test-harness mode (`--harness=test`), so each build stores it its own way.

The API takes no scenario id, so the seeder names the next session's scenario in `next-scenario.txt` and the test
harness reads it at spawn (`TestHarnessRuntime.NextScenarioFromFile`). Older refs get this from `harness.patch`, which
`run.ts` applies when it's missing (it touches only `src/WeaveFleet.TestHarness`).

## What it measures (`measure.ts`)

| Scenario | From → to |
|---|---|
| coldStart | navigation start → session list painted; also → long conversation painted, requests, JS size |
| openSession | click a session not opened before → its conversation painted |
| switchLong | click between two long sessions already opened once |
| typing | keystroke → next paint, per key (rAF + task), and INP from Event Timing |
| sendPrompt | Enter → your bubble painted; settled = the reply rendered |
| railSessions / railSettings / railAnalytics / railFleet | rail click → view painted |
| openChanges | Changes tab click → its file list painted (files edited after the session started) |
| scrollLong | 3 s of wheel scrolling up a long conversation: frame p95, dropped frames, long tasks |
| newSessionPage | New session click → form painted |
| streamingList / streamingWatch | 4 sessions streaming while you read another / watch one, and type: long tasks per 6 s, worst frame, keystroke p95 |

Timing starts at the real input (`pointerdown`/`keydown` `timeStamp`), so it includes input delay. "Shown" is the frame
after the condition first holds; "settled" is when the DOM has been quiet for 300 ms after that. Clicks go to coordinates
resolved before timing starts, so Playwright's locator work never lands inside a measurement. Each scenario's CPU profile
comes from one extra run after the timed ones.

## Finding causes

```powershell
bun attribute.ts <results>/<label>/<scenario>.cpuprofile <repo>/src/WeaveFleet.Api/wwwroot --app   # self/inclusive time by src file:line
bun trace-typing.ts --scratch <scratch> --throttle 4 --early 1      # where a keystroke goes: script, style, layout, layerize, hit test
bun soak.ts --scratch <scratch> --switches 120                      # heap, DOM nodes, listeners and switch time as a tab ages
bun measure.ts --scratch <scratch> --label whatif --only typing --css ".x{position:static!important}"   # what-if a CSS change
```

`run.ts` builds the client with source maps (`vite build --sourcemap`) so profiles map back to `src/...:line`. To keep a
seeded Fleet up between experiments: `bun serve.ts <repo> <scratch> <port>` (writes `<scratch>/seed.json`).

## Noise

Use the same machine for both builds, and keep other heavy work off it. `run.ts` alternates the order of builds each
round so background load falls on both. `--throttle 4` (Chrome CPU throttling) magnifies differences that are a few
milliseconds on a fast machine.
