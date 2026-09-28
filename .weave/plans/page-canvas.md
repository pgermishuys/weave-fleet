# page-canvas

## TL;DR

Agents show the user HTML they wrote (mockups, prototypes, before-and-afters, explainers) by handing Fleet the file.
A new tool, `fleet_page_show(path, title)`, has Fleet copy the page and serve it itself at `/pages/{pageId}/…`, and a
new **Page** canvas shows it beside the chat. No process, no port, no Python. Showing the file again updates the same
tab, and the tab reloads by itself.

The same change fixes the tool routing problem: Fleet's own skills teach agents to run `python3 -m http.server`
through `fleet_app_start`, which that tool's own description forbids. After this plan each tool takes a different
kind of input, every description names the others, Fleet refuses the known misuses, and a routing eval shows the
agent picks the right tool with skills on and off.

## Context

How mockups reach the user today (`opencode/built-in-skills/fleet-mockups/SKILL.md` §2, `fleet-explain/SKILL.md:105`):

1. The agent calls `fleet_app_start` with `python3 -m http.server $PORT --directory …`.
2. Fleet runs it, scans the process tree for its port and waits for it to answer.
3. The preview gateway (`PreviewGateway.cs`) gives it a `pN.localhost` origin, with cookie and redirect rewriting.
4. The Browser canvas frames that origin.

Every step can fail on its own: Python is often missing on Windows, and the port scan and the gateway's cookie rules
were built for real apps (#310). The Browser canvas also reloads its page when you switch tabs. None of that is needed
to show a file.

Claude Code does this with an Artifact tool: it uploads the file to claude.ai, which hosts it at a stable URL, and
publishing again updates the same URL. Fleet's own server is always running, so it can play claude.ai's part.

**The routing problem, as it stands:**

- `fleet_app_start`: "Use it only when the user asks to run, host, serve, preview or see the app".
- `fleet-mockups` and `fleet-explain`: "Serve the folder with `fleet_app_start` (`python3 -m http.server …`)".
- Built-in skills are opt-in, so routing text that lives only in a skill may never load. The tool descriptions always
  load. The routing has to live there, backed by refusals Fleet enforces.

## Decisions

1. **Route by input, not purpose.** Each tool takes a different kind of input, so the agent chooses by what it has:

   | The agent has… | Tool | Input |
   |---|---|---|
   | Boxes and arrows, or a sequence | `fleet_canvas_open` | diagram JSON (`kind` enum: `diagram`, `sequence`) |
   | An HTML file it wrote | `fleet_page_show` (new) | a file path |
   | A command that serves the project | `fleet_app_start` | a command |
   | A page already running on this machine | `fleet_browser_open` | a URL |

2. **No new skill.** `fleet-mockups` and `fleet-explain` switch to `fleet_page_show`. `fleet-run` stays the only
   skill that uses `fleet_app_start`.
3. **"Show", not "publish".** Nothing leaves the machine. "Publish" suggests it does.
4. **Fleet copies the page.** It doesn't serve the agent's folder in place. The copy can't expose files that were added
   later, survives the agent deleting its temp folder, and each show is one version the tab reloads to.
5. **Pages run in a sandbox with an opaque origin.** Every response carries
   `Content-Security-Policy: sandbox allow-scripts allow-forms allow-popups allow-modals allow-downloads` (no
   `allow-same-origin`). Scripts run, but the page can't read Fleet's cookies or call Fleet's API as the user, even
   when opened in a new tab. The cost: `localStorage` and `sessionStorage` throw inside a page. The skill says so.
6. **The page URL is the credential** (open question A). `{pageId}` is 128 random bits and the route is anonymous.
   It has to be: a sandboxed page's requests for its own CSS and images are cross-site, so Fleet's `SameSite=Lax`
   sign-in cookie (`Program.cs:244`, `:317`) isn't sent, and assets would fail whenever sign-in is on. This matches
   how previews already work (browser-canvas plan Decision 12). `Referrer-Policy: no-referrer` keeps the URL out of
   CDN requests.
7. **Fleet refuses the known misuses** (open question B):
   - `fleet_app_start` with a static file server (`python -m http.server`, `python3 -m http.server`,
     `py -m http.server`, `npx`/`bunx`/`pnpm dlx` `serve`, `http-server`, `live-server`) is refused with a pointer to
     `fleet_page_show`.
   - `fleet_page_show` on a project's page is refused with a pointer to `fleet_app_start`. A project page is one whose
     folder has a project file (`package.json`, `*.csproj`, `*.fsproj`, `*.sln`, `Cargo.toml`, `go.mod`,
     `pyproject.toml`, `Gemfile`, `composer.json`, `deno.json`) or that loads `.ts`, `.tsx`, `.jsx`, `.vue` or
     `.svelte` sources.
8. **Pages live as long as their session** (open question C). They're deleted with the session, like screenshots.
   Closing the tab keeps the page, so showing it again reopens it.

## Scope

In:

- The page store, the `/pages` route, the `page` canvas kind and the `fleet_page_show` bridge call.
- `fleet_browser_screenshot` and `fleet_canvas_read` working on page canvases.
- The Page canvas and its tool card in the client.
- The tool description rewrites for all four tools, and the two refusals.
- Rewrites of `fleet-mockups`, `fleet-explain` and `fleet-run`.
- The routing eval, run before (baseline) and after.

Out:

- **Claude Code and Pi.** They don't get Fleet's tool plugin today; only OpenCode and OpenCode 2 do.
- **The File canvas's HTML preview.** `HtmlRenderer.vue` frames with `sandbox="allow-same-origin"` and no
  `allow-scripts`, so scripts don't run there. That's a separate follow-up; it could reuse the `/pages` route.
- **Moving the Browser canvas off the gateway.** It stays for running real apps.

## Design

### Server

**Page store** (`IPageStore` in Application, `PageStore` in Infrastructure next to `SessionScreenshotStore`):

- Files at `<db dir>/pages/{sessionId}/{pageId}/`.
- `ShowAsync(sessionId, sourceFile)` checks the source, copies it into a temp folder beside the target, then swaps
  it in, so a reader never sees half a copy.
- The page's identity is its session plus the source file's full path: showing the same file again reuses the
  `pageId` and the canvas.
- **What gets copied:** the file's own folder, recursively. Only web files (`html htm css js mjs json svg png jpg jpeg
  gif webp avif ico woff woff2 ttf otf mp4 webm mp3 wav txt csv`). Dot entries and `node_modules` are skipped, and
  symlinks are never followed.
- **Caps:** 25 MB and 500 files. Over a cap, Fleet refuses and asks for the page in a folder of its own.
- **Warnings, not refusals:** root-absolute references in the entry file (`src="/…"`, `href="/…"`) won't resolve
  under `/pages/{pageId}/`, so the result lists them and asks for relative paths. The same goes for references that
  leave the folder (`../`).
- **Clean-up:** `DeleteSessionAsync(sessionId)` is called from `SessionOrchestrator` where screenshots are deleted. A
  startup sweep removes page folders that have no canvas row.

**Route** (`PageEndpoints.cs`): `GET /pages/{pageId}/{**path}`, anonymous, mapped before `MapFallbackToFile`. An empty
path serves the entry file. Headers on every response:

- the sandbox CSP from Decision 5;
- `X-Content-Type-Options: nosniff`;
- `Referrer-Policy: no-referrer`;
- `Cache-Control: no-cache`.

The content type comes from the extension. Anything outside the page folder, and any unknown `pageId`, gets the same
404. The route has to be excluded from Fleet's auth fallback policy and CSRF checks.

**Canvas kind `page`** (`CanvasKinds.Page`):

- State: `{ pageId, entry, source, files, bytes, shownAt, warnings }`.
- Only the agent sets it, through `fleet_page_show`, never through `fleet_canvas_open` or patch.
- Each show bumps `version`, and the existing canvas events carry that to the client.
- No migration: `canvases.kind` is free text (`028_add_canvases.sql`).

**Bridge** (`PageBridge`, resolving the caller like `BrowserBridge`): `POST /api/bridge/canvas/page-show` with
`{ harnessSessionId, path, title }`.

- The path must be absolute and name an existing `.html` or `.htm` file.
- The result names the canvas, what was copied (file count, size), and any warnings.
- It ends with: "Show the same file again after an edit; the user's tab reloads."

**Screenshots:** `BrowserBridge.ScreenshotAsync` accepts page canvases. The URL is Fleet's own loopback address (the
one `FLEET_URL` is built from) plus `/pages/{pageId}/{entry}`; `path` resolves inside the page.

**Read:** `fleet_canvas_read` on a page canvas returns the source path, entry, file count, when it was shown, and any
warnings.

**Static-server refusal:** `BrowserBridge.AppStartAsync` checks the command before starting anything. A match returns
`Invalid`, with the text in the tool section below.

### Client

- `CanvasKind` gains `page`. `PageCanvas.vue` frames `/pages/{pageId}/{entry}` with the same `sandbox` attribute as
  the header and `color-scheme: light` (as #307 did for the Browser canvas).
- A version bump reloads the frame. The toolbar has Reload and Open in new tab.
- The registry entry is labelled "Page" (lucide `AppWindow`). It isn't pickable from the + menu.
- Tool card: "Showed page · {title}", with the warnings when there are any. A click focuses the canvas.
- Mock mode: a fixture page and a canvas event, for the screenshots.

### Tool descriptions (word for word)

**`fleet_page_show`** (new):

> Show the user an HTML page you wrote (a mockup, prototype, before-and-after or explainer) in a page canvas beside
> the chat. Use it when the user asks to see something, or when a page is clearly the best way to answer them. Don't
> open one for your own notes, or when you're working on a task delegated by another agent.
> Pass the .html file's absolute path. Fleet copies the file and the web files in its folder (CSS, scripts, images,
> fonts) and serves the copy itself, so there's no server to start. Showing the same file again updates the same tab,
> and the user's tab reloads, so show it again after every edit you want them to see.
> Use relative links, and keep the page's files in its folder: paths starting with / or ../ don't load. Pages run
> sandboxed: localStorage isn't available.
> Not for the project's own app, or anything that needs a build or a dev server: use fleet_app_start. Not for a page
> already running at a localhost address: use fleet_browser_open. Not for diagrams of boxes and arrows or sequences:
> use fleet_canvas_open.

Args:

- `path`: "Absolute path of the .html file, e.g. \"/tmp/mockups/settings/options.html\"."
- `title`: "Short title for the canvas tab, e.g. \"Settings options\"."

**`fleet_canvas_open`:** add after its first sentence:

> For a mockup or any HTML page, use fleet_page_show.

**`fleet_app_start`:** add after "Use it only when…":

> Not for HTML files you wrote: show those with fleet_page_show. Fleet refuses plain file servers (python -m
> http.server, serve, http-server).

**`fleet_browser_open`:** add:

> For an HTML file you wrote, use fleet_page_show.

**`fleet_browser_screenshot`:** `canvasId` now says "a browser or page canvas".

**Static-server refusal text:**

> `{command}` only serves files, and Fleet serves files itself. For a page you wrote, call fleet_page_show with the
> .html file. To run the project's app, start its dev server (e.g. npm run dev).

**Project-page refusal text:**

> {file} belongs to a project ({marker}): its page needs the project's server. Use fleet_app_start with the dev
> command (read package.json or the README first).

## Tasks

- [ ] 0. **Routing baseline.** Build the eval kit (see "Routing eval") and run it on current `main`, to record how
  often agents take the wrong tool today. This is the evidence for the before-and-after.
- [x] 1. **Page store and route.**
  - `IPageStore`/`PageStore`, the `/pages` route and its headers, session-delete clean-up, startup sweep.
  - Tests: copy rules, caps, the atomic swap, path traversal, an unknown id's 404, headers.
  - An Api test showing the route answers anonymously with sign-in on and never sends Fleet's cookie back.
- [x] 2. **Bridge and canvas kind.**
  - `CanvasKinds.Page`, `PageBridge`, the `page-show` endpoint, project-page refusal, warnings.
  - Page canvases in `fleet_canvas_read` and `fleet_browser_screenshot`.
  - Application tests for each refusal and warning, and for same-file-same-canvas.
- [x] 3. **Static-server refusal** in `AppStartAsync`, with tests for each pattern and for commands that must still
  pass (`npm run dev`, `dotnet watch`, `bun --hot`, `python manage.py runserver`, `uvicorn`).
- [x] 4. **Plugin tool and descriptions.**
  - `fleet_page_show` in `opencode/fleet/fleet-canvas.ts`, and the four description edits.
  - Extend `FleetCanvasPluginLiveTests` (real OpenCode, scripted model) to call `fleet_page_show` on OpenCode and
    OpenCode 2.
- [x] 5. **Client.** `PageCanvas.vue`, registry, store, tool card, mock-mode fixture; vitest on Node 22; mock-mode
  screenshots in light and dark, desktop and phone.
- [x] 6. **Skills.**
  - `fleet-mockups` §2 becomes "write the file, call `fleet_page_show`, show it again after edits", plus a
    no-`localStorage` note.
  - `fleet-explain` switches from `fleet_app_start` to `fleet_page_show`.
  - `fleet-run` gets one line: "HTML you wrote isn't an app: use `fleet_page_show`."
- [x] 7. **Live check** on a scratch Fleet (scratch HOME, never the real one):
  - an agent builds a mockup and shows it, edits it and shows it again (the tab reloads by itself), and screenshots
    it;
  - a page with CDN scripts and an image;
  - the same page from another device with sign-in on;
  - Open in new tab, confirming in devtools that the page can't read Fleet's cookies.
  - PR screenshots.
- [ ] 8. **Routing eval after.** Rerun the kit and meet the acceptance bar. Change wording, never add a skill, until
  it passes.

## Routing eval

- **Kit:** `~/.cache/fleet-page-routing` with a scratch HOME, a scratch Fleet and a fixture folder holding a Vite app
  (with `package.json`), a plain `site/` folder with an `index.html`, and nothing running on port 4000 until
  prompt 7 starts it.
- **Model:** the one the user normally runs, through their existing login (ask before copying any auth file into the
  scratch HOME).
- **What's recorded:** the first Fleet tool each turn calls, read from the session's history.

| # | Prompt | Right tool |
|---|---|---|
| 1 | Mock up two options for the settings page | `fleet_page_show` |
| 2 | Show me a before-and-after of the header change | `fleet_page_show` |
| 3 | Make me a pricing page prototype I can click through | `fleet_page_show` |
| 4 | Build a small landing page in site/ and show it to me | `fleet_page_show` |
| 5 | Serve this folder with python so I can see it (in `site/`) | `fleet_page_show` (after the refusal, or directly) |
| 6 | Explain how the queue works, visually | `fleet_page_show` or `fleet_canvas_open` |
| 7 | Show me what's running on localhost:4000 | `fleet_browser_open` |
| 8 | Run the app | `fleet_app_start` |
| 9 | Show me the app with your change | `fleet_app_start` |
| 10 | Draw how a prompt flows from the composer to the harness | `fleet_canvas_open` |
| 11 | Show me the sequence of calls when a session starts | `fleet_canvas_open` (`sequence`) |
| 12 | Show me the architecture of this repo | `fleet_canvas_open` |
| 13 | Fix the typo in the Vite app's index.html | none of the four |
| 14 | What does the README say about setup? | none of the four |
| 15 | Show me the Vite app's index.html as a page | `fleet_app_start` (after the project-page refusal, or directly) |

- **Runs:** each prompt in a new session, on OpenCode 2 and OpenCode, with the built-in skills off and then on (all
  of them). One run per cell; any miss is rerun three times to tell a flake from a pattern.
- **Acceptance:** no wrong tool in any cell. A refusal followed by the right tool counts as right; it's the guard
  doing its job. Report the refusal count separately. If it stays high, the descriptions need work.

## Open questions for the user

- **A. Anonymous page URLs.** Recommended: the unguessable URL is the credential, like previews. The alternative,
  requiring sign-in, breaks every page's own CSS and images when sign-in is on (Decision 6).
- **B. Refuse static file servers outright.** Recommended. It also refuses a user who explicitly asks for
  `python -m http.server`, but `fleet_page_show` gives them what they asked to see. The alternative is a warning in
  the result, which agents tend to ignore.
- **C. How long pages live.** Recommended: as long as the session. The alternative is the screenshots' 7 days.

## Risks

- **Absolute paths.** Agent-written pages sometimes use `/styles.css`. The warning in the result should get the agent
  to fix it. The eval shows whether it does.
- **Pages that need storage.** Some prototypes use `localStorage`, which throws in the sandbox. The skill and tool
  text say so. If it bites often, a page can get its own origin later through the gateway's `*.localhost` naming,
  without changing the tool.
- **Refusal false positives.** A project whose real dev command is `npx serve` is refused. The refusal names the dev
  server as the way out; watch for it in the eval.
- **Tab reload on switch.** Page tabs reload when you switch back, like Browser tabs, but a static page reloads in
  milliseconds.
