# Spike: OpenCode 2's browser plugin in Fleet (2026-10-01)

Branch `spike/opencode2-browser-plugin`. Spike code only; not for `main`.
Mockups and verdict page: https://claude.ai/artifact/WTGjVDAVqNUEVkA9iZjRqv (source `mockup/src.html`, built with `mockup/build.py`).

**Verdict: viable, with conditions.** Fleet can be the plugin's "desktop". A C# attachment in Fleet, driving Fleet's
own headless Chrome over CDP, ran a real agent's Code Mode script end to end: open a tab on the canvas's page, read
it, fill a field, click, read it again, take a screenshot and read the console. The image and the text came back to
the model. The conditions: the agent drives a different browser from the one the user watches, OpenCode 2 doesn't ask
permission for browser actions, and today the unusable catalog already costs every Fleet OpenCode 2 session about
1,000 tokens.

## 1. Is it in 2.0.18, and is it on?

- Yes and yes. `opencode.browser` is in the built-in plugin list of the installed 2.0.18 (`GET /api/plugin`), and
  the binary contains the plugin's strings.
- **2.0.18 shows the browser catalog to every session, attached or not.** The model gets
  `- browser (45 tools, 7 shown)` in the Code Mode part of its system prompt (`tabs.list`, `tabs.open`, `preview`,
  `back`, `forward`, `reload`, `stop`). With `"plugins": ["-opencode.browser"]` the Code Mode section shrinks from
  17,080 to 12,497 characters: about **1,150 tokens per session** that Fleet pays today for tools that can't work.
  In a real Fleet session the browser block is 3,994 characters (~1,000 tokens).
  Evidence: `q1-codemode-section-2.0.18*.txt`, `q5-token-costs.txt`.
- With no attachment, a call fails with `[browser.disconnected] No desktop browser is connected to this session. Open
  this session in the desktop app…` (`q1-no-attachment-2.0.18.txt`). That advice is wrong inside Fleet.
- **2.0.21 hides the tools until something attaches** (upstream #52309): the plugin appends a `browser: deny` rule to
  every agent and an attachment adds `browser: allow` to its session. Proved: a session with no rules of its own sees
  no `browser` namespace and gets `Unknown tool 'browser.tabs.list'` (`q1-no-attachment-2.0.21-default.txt`).
- **But Fleet's sessions defeat that hiding.** Fleet creates sessions with `{"*": allow}` ("Allow everything"), and the
  session rule wins over the agent's deny: the catalog is back, 45 tools (`q1-no-attachment-2.0.21-allowall.txt`).
  Any Fleet fix has to add its own `browser: deny` rule, or turn the plugin off.
- After an attachment on 2.0.21, the catalog arrives as a `<system-update>` user message (8,924 characters, ~2,230
  tokens) rather than a change to the system prompt, so the prompt cache survives
  (`q2-2.0.21-catalog-update-message.txt`).
- The plugin changed three times in the week after 2.0.18 (hiding, element comments with `evaluate` on a `ref`,
  moving GUI features into extensions). The RPC id is `experimental.browser` and the protocol is `version: 4`.

## 2. Can Fleet be the attachment? Yes, proved twice.

The wire (read from `plugin-browser/src/connection.ts` and `desktop/src/main/browser-pane.ts`, then used live):

1. Read V2's event stream `GET /api/event` and wait for `server.connected`.
2. `POST /api/rpc/experimental.browser/attach?location[directory]=…` with
   `{"input":{"sessionID","connectionID","version":4}}`. The call stays pending for the attachment's life and returns
   `"closed"` or `"replaced"`.
3. Events of type `rpc.experimental.browser.control` carry `{type:"attached"|"command"|"cancel", connectionID,
   requestID}`. Only ids travel on the shared event stream, never arguments or results.
4. On `attached`, post `state` (the tab inventory). On `command`, post `command` to fetch
   `{"output":{"action":{type,…},"generation","files"}}`, run it, then post `result` with
   `{"outcome":{"type":"success","result":{"value":…, "files":[{id,name,mime,data(base64)}]}}}` or
   `{"type":"failure","code","message"}`.
5. A tab must be in V2's inventory before a result names it: publish `state` before answering `tabs.open`.
6. V2 times a command out after 60 s, and fails pending work when the attachment ends. Images in `files` are attached
   to the model's next request.

Prototypes:

- `kit/proto/attach.ts` (bun, ~190 lines) against V2 alone, 2.0.18 and 2.0.21. 7 of 7 commands succeeded.
- `src/WeaveFleet.Infrastructure/Harnesses/OpenCode2/OpenCode2BrowserSpike.cs` (C#, ~490 lines) inside a scratch
  Fleet, using **Fleet's own headless Chrome** (`HeadlessChromeScreenshotter.SharedBrowserAsync`, a spike accessor)
  and `CdpConnection`. Endpoint `POST /api/spike/sessions/{id}/browser-attach`. The agent first opened the page in a
  browser canvas with `fleet_browser_open`, then ran `kit/scripts/drive.js` through `execute`: tabs.open, snapshot,
  fill, click, snapshot, screenshot, console. All 7 succeeded in under a second (`q2-fleet-attachment.log`), and the
  model received the before/after snapshots ("Saved, Ada!"), the console line and the screenshot as an image
  (`q2-fleet-roundtrip-model-sees.json`, `q2-fleet-agent-screenshot.png`).

What each family needs from Fleet (CDP, no packages):

| Family | CDP | Proved | Size |
|---|---|---|---|
| Tabs, navigation | Target.createTarget/attachToTarget/closeTarget, Page.navigate + loadEventFired, Page.getNavigationHistory | open, navigate | S |
| Snapshot/find | Accessibility.getFullAXTree → refs to backendDOMNodeId | yes | S |
| Input | DOM.getBoxModel + Input.dispatchMouseEvent, DOM.focus + Input.insertText, Input.dispatchKeyEvent | click, fill | M (select/check/drag/fill_form/dialog) |
| Screenshot | Page.captureScreenshot | yes | S |
| Console | Runtime.consoleAPICalled (or an init script, as the spike did) | yes (init script) | S |
| Evaluate | Runtime.evaluate | not run | S |
| Network | Network.* events + getResponseBody, bounded buffer | no | M |
| Files | DOM.setFileInputFiles, Browser.setDownloadBehavior | no | M (and see security) |
| Trace / CPU | Tracing.start/end, Profiler.start/stop + analysis | no | M each |
| Heap | HeapProfiler.takeHeapSnapshot + parser | no | L |
| Lighthouse | needs the `lighthouse` npm package (the desktop imports it) | no | L, not in .NET; answer `unsupported` |

## 3. Which browser does the agent drive?

- Fleet's headless Chrome, with its own temporary profile. The user's canvas is an iframe in their own browser, which
  Fleet can't reach over CDP. Proved: right after the agent saved "Ada", the canvas's proxied page loaded in a
  separate browser showed an empty form and no status (`q3-user-canvas-view.png`).
- Sign-ins and cookies don't carry over: the agent's profile starts empty every time Fleet's browser starts.
- Mirroring options:
  - Replaying the agent's clicks in the user's iframe through `preview-bridge.js`: rejected. A form would submit
    twice, and the two pages drift.
  - Streaming the agent's tab (CDP `Page.startScreencast`) into the canvas as an "Agent's view": honest and cheap.
    Not built in the spike.
  - In Fleet's desktop app (Electron), the canvas could be a webContents Fleet controls, so the agent drives the page
    the user sees. That's what OpenCode's own desktop does. Later.
- Recommended v1: the browser canvas gets **Your view / Agent's view**. Agent's view is the live picture of the
  agent's tab plus the step list; your view stays yours. Fleet knows every step because it runs them, so it can
  caption them without parsing the script.

## 4. Conflicts with Fleet's tools and built-in skills

What a Fleet OpenCode 2 session is offered today (`q4-fleet-session-tools.json`): nine `fleet_*` tools as regular
tools, plus `execute` with the browser catalog in the system prompt.

| Overlap | Decision |
|---|---|
| `fleet_browser_screenshot` vs `browser.screenshot` | Keep both. Fleet's loads the canvas page fresh at desktop or phone size, works for page canvases and every harness. The plugin's shoots the agent's tab as it is now. Add one sentence to each description. |
| `fleet_browser_open` vs `browser.tabs.open` | Keep both. One shows a page to the user; the other opens the agent's own tab. When the agent opens a tab with `focus`, Fleet shows Agent's view in that page's canvas. |
| `fleet_page_show` vs `browser.preview` | Route the plugin's `preview` through Fleet: open the file in a page canvas, as `fleet_page_show` does. |
| "Review pane", "desktop app" wording in the plugin | Fleet can't edit another plugin's descriptions (not checked whether V2's tool editor allows it). Fleet makes them true instead: focus = show in the canvas; disconnected = turn the plugin off. |

Skill edits (the skills are shared by every harness, so the wording is harness-neutral):

- `fleet-run` (Web app section): add after the screenshot bullet: "If you can use the page (browser tools: click,
  fill, read), try the flow you changed in it, then read the console. Take one screenshot of the result."
- `fleet-debug` (2. Reproduce it): add: "For a bug in a page, reproduce it in the page with your browser tools when
  you have them, and read the console and the failed requests."
- `fleet-design`, `fleet-explain`, `fleet-mockups`: no change. "Take one `fleet_browser_screenshot`" stays right: it
  wants a fresh look at desktop and phone size, which the plugin's screenshot doesn't do.
- Tool descriptions: `fleet_browser_screenshot` gains "It loads the page fresh, so it doesn't show what you clicked or
  typed: to see that, use your browser tools." (OpenCode 2's `index.js` and OpenCode's `fleet-canvas.ts`.)

## 5. Other harnesses

OpenCode, Claude Code and Pi have no such plugin. Fleet can supply the same core itself, because the hard part (a
browser driven over CDP, tab bookkeeping, refs, screenshots) is Fleet's either way:

- **One Fleet agent-browser service** (C#, in Infrastructure), harness-neutral. It is what the spike's attachment
  already is, minus the V2 wire.
- **OpenCode 2:** exposed as the plugin's attachment. No new Fleet tools; Code Mode scripts many steps in one call.
- **OpenCode, Claude Code, Pi:** three regular Fleet tools, `fleet_browser_read` (snapshot/find/console),
  `fleet_browser_act` (click, fill, select, check, press, scroll, navigate) and the existing screenshot. Estimated
  ~600–900 tokens per request, measured against today's nine `fleet_*` tools at 9,366 characters (~2,340 tokens).
  Each step is a model round trip, unlike Code Mode.
- Not "adopt the plugin instead": the plugin only gives the tool surface. Fleet still writes all the CDP work.

## 6. Security and safety

- **No permission asks.** At Fleet's "Ask" level, a shell call asked (`q6-shell-ask-same-session.json`), and all
  seven browser calls ran without asking (`q6-permission-ask.json` is empty). The plugin's README says per-URL and
  server-file checks belong to a later layer (#46530).
- `evaluate` runs any script in the page: it can read the page's storage and call any address the page can reach,
  including other local services.
- `files.upload` reads **any server file** V2 can read (`files.ts` resolves the path against the folder but allows
  absolute paths), up to 5 MiB, and Fleet only sees the file name, not the path. Fleet should refuse uploads in v1.
- Network capture returns request and response headers and bodies, sign-in cookies included.
- `tabs.open`/`navigate` take any http(s) address. Fleet runs every command, so Fleet can enforce a policy: v1 allows
  only the session's own canvas pages and loopback apps, and turns `evaluate` into a setting.
- Memory: Fleet's headless Chrome with one agent tab was 11 processes and **242 MB** (PSS). It is the same browser the
  screenshots use, which quits after 2 minutes idle today. The V2 server was 140 MB and Fleet 188 MB.

## Kit

`kit/` holds the live kit: `v2alone.sh` (V2 alone, scratch HOME, port 5452), `fakellm.py` (scripted model; "run code
<name>" runs `kit/scripts/<name>.js` through `execute`, "call tool <name> {json}" calls a tool), `v2.py`,
`proto/attach.ts`, `fleet.sh` (scratch Fleet on 5451), `browser_live.py`, `shot-user.mjs`. Chrome needs a short
`TMPDIR` (its socket path is limited to ~107 characters).
