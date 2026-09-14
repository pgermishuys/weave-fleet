/**
 * Vite plugin to mock API responses for standalone frontend development.
 * 
 * Intercepts requests to `/api/*` and `/healthz` and returns canned JSON fixtures.
 * Only activates in dev/preview mode when MOCK_API env var is set or FLEET_API_URL is not set.
 * 
 * Usage:
 *   - Set MOCK_API=true to force mock mode
 *   - Set FLEET_API_URL to bypass mocking and use real backend
 */

import type { Plugin, ViteDevServer, PreviewServer } from "vite";
import type { IncomingMessage } from "http";
import { readFileSync } from "fs";
import { resolve } from "path";
import { startMockPreview, type MockPreview } from "./mock-preview";

interface MockRoute {
  pattern: RegExp;
  handler: (url: URL, req: Request) => Response | Promise<Response>;
}

interface MockTerminal {
  id: string;
  title: string;
  status: "running" | "stopped";
  createdAt: string;
}

interface MockApiOptions {
  mode?: string;
}

// Example diffs and workspace files for mock sessions (right-panel canvases).
const MOCK_DIFFS = [
  {
    "file": "src/middleware/auth.ts",
    "status": "added",
    "additions": 18,
    "deletions": 0,
    "before": "",
    "after": "import jwt from 'jsonwebtoken';\nimport type { Request, Response, NextFunction } from 'express';\n\nexport function authenticate(req: Request, res: Response, next: NextFunction) {\n  const header = req.headers.authorization;\n  if (!header?.startsWith('Bearer ')) {\n    return res.status(401).json({ error: 'Missing token' });\n  }\n\n  try {\n    const token = header.slice('Bearer '.length);\n    req.user = jwt.verify(token, process.env.JWT_SECRET!, { algorithms: ['HS256'] });\n    next();\n  } catch {\n    res.status(401).json({ error: 'Invalid token' });\n  }\n}\n"
  },
  {
    "file": "src/middleware/index.ts",
    "status": "modified",
    "additions": 1,
    "deletions": 0,
    "before": "export { errorHandler } from './error-handler';\nexport { requestLogger } from './logger';\nexport { rateLimiter } from './rate-limiter';\n",
    "after": "export { authenticate } from './auth';\nexport { errorHandler } from './error-handler';\nexport { requestLogger } from './logger';\nexport { rateLimiter } from './rate-limiter';\n"
  },
  {
    "file": "docs/auth.md",
    "status": "added",
    "additions": 7,
    "deletions": 0,
    "before": "",
    "after": "# Authentication\n\nProtected routes use the `authenticate` middleware.\n\n- Send `Authorization: Bearer <token>`\n- Tokens are verified with HS256\n- Invalid or missing tokens get a 401\n"
  }
];

const MOCK_FILES: Record<string, string> = {
  "src/middleware/auth.ts": "import jwt from 'jsonwebtoken';\nimport type { Request, Response, NextFunction } from 'express';\n\nexport function authenticate(req: Request, res: Response, next: NextFunction) {\n  const header = req.headers.authorization;\n  if (!header?.startsWith('Bearer ')) {\n    return res.status(401).json({ error: 'Missing token' });\n  }\n\n  try {\n    const token = header.slice('Bearer '.length);\n    req.user = jwt.verify(token, process.env.JWT_SECRET!, { algorithms: ['HS256'] });\n    next();\n  } catch {\n    res.status(401).json({ error: 'Invalid token' });\n  }\n}\n",
  "src/middleware/index.ts": "export { authenticate } from './auth';\nexport { errorHandler } from './error-handler';\nexport { requestLogger } from './logger';\nexport { rateLimiter } from './rate-limiter';\n",
  "docs/auth.md": "# Authentication\n\nProtected routes use the `authenticate` middleware.\n\n- Send `Authorization: Bearer <token>`\n- Tokens are verified with HS256\n- Invalid or missing tokens get a 401\n",
  "src/middleware/error-handler.ts": "export function errorHandler(err, _req, res, _next) {\n  res.status(500).json({ error: err.message });\n}\n",
  "src/middleware/logger.ts": "export function requestLogger(req, _res, next) {\n  console.log(req.method, req.url);\n  next();\n}\n",
  "src/utils/token.ts": "export function readBearer(header?: string) {\n  return header?.startsWith('Bearer ') ? header.slice(7) : null;\n}\n",
  "package.json": "{\n  \"name\": \"auth-service\",\n  \"private\": true\n}\n",
  "README.md": "# auth-service\n\nExample project for Fleet's mock mode.\n"
};

// Server canvases (Fleet Canvas step 2): what the agent has drawn in a session.
// Every mock session starts with the same two; closes and revisions are kept
// in memory until Vite restarts.
interface MockCanvas {
  canvasId: string;
  kind: "diagram" | "sequence" | "browser";
  title: string;
  version: number;
  state: Record<string, unknown>;
  closed: boolean;
  revision: number;
}

const MOCK_DIAGRAM_NODES = [
  { id: "n1", label: "NuCode session", detail: "NuCode/Sessions" },
  { id: "n2", label: "EventBroadcaster", detail: "Infrastructure/Events" },
  { id: "n3", label: "SessionEventsHub", detail: "Api/Hubs" },
  { id: "n4", label: "useSessionStream", detail: "client/composables" },
  { id: "n5", label: "use-sessions.ts", detail: "client/composables" },
  { id: "n6", label: "GET /api/sessions", detail: "Api/Endpoints" },
];

const MOCK_DIAGRAM_EDGES = [
  { id: "e1", from: "n1", to: "n2", label: "publishes", style: "solid" },
  { id: "e2", from: "n2", to: "n3", label: "session:{id}", style: "solid" },
  { id: "e3", from: "n3", to: "n4", label: "Event", style: "solid" },
  { id: "e4", from: "n5", to: "n6", label: "polls every 15 s", style: "dashed" },
];

// Each revise applies the next step, the way an agent patches the same diagram.
const MOCK_DIAGRAM_REVISIONS: Array<{ summary: string; apply: (state: { nodes: Array<Record<string, unknown>>; edges: Array<Record<string, unknown>> }) => void }> = [
  {
    summary: "+1 edge",
    apply: (state) => {
      state.edges.push({ id: "e5", from: "n3", to: "n5", label: "SessionListChanged", style: "planned" });
    },
  },
  {
    summary: "−1 box, +1 box",
    apply: (state) => {
      state.nodes = state.nodes.filter((node) => node.id !== "n6");
      state.edges = state.edges.filter((edge) => edge.from !== "n6" && edge.to !== "n6");
      state.nodes.push({ id: "n7", label: "SessionListCache", detail: "client/stores" });
      state.edges.push({ id: "e6", from: "n5", to: "n7", label: "updates", style: "solid" });
    },
  },
  {
    summary: "1 box changed",
    apply: (state) => {
      const node = state.nodes.find((candidate) => candidate.id === "n5");
      if (node) node.label = "use-sessions.ts (push)";
    },
  },
];

const MOCK_SEQUENCE_SOURCE = [
  "sequenceDiagram",
  "  participant Agent",
  "  participant Plugin as fleet-canvas.ts",
  "  participant Fleet",
  "  participant Browser",
  "  Agent->>Plugin: fleet_canvas_open",
  "  Plugin->>Fleet: POST /api/bridge/opencode/canvas/open",
  "  Fleet-->>Browser: canvas.updated",
  "  Fleet-->>Browser: canvas.focused",
  "  Fleet-->>Plugin: Opened at v1",
].join("\n");

function seedMockCanvases(): MockCanvas[] {
  return [
    {
      canvasId: "cv_mock_diagram",
      kind: "diagram",
      title: "Session event flow",
      version: 1,
      state: { direction: "TB", nodes: structuredClone(MOCK_DIAGRAM_NODES), edges: structuredClone(MOCK_DIAGRAM_EDGES) },
      closed: false,
      revision: 0,
    },
    {
      canvasId: "cv_mock_sequence",
      kind: "sequence",
      title: "Canvas tool call",
      version: 1,
      state: { source: MOCK_SEQUENCE_SOURCE },
      closed: false,
      revision: 0,
    },
    {
      canvasId: "cv_mock_browser",
      kind: "browser",
      title: "Storefront",
      version: 2,
      state: { url: "http://localhost:5173/", appId: MOCK_APP_ID },
      closed: false,
      revision: 0,
    },
  ];
}

// Apps Fleet runs for a session (browser canvas, Task 6 of browser-canvas-v1). Every mock session starts
// with one running app, shown by the "Storefront" browser canvas; its page is mock-preview.ts.
// Script its states on 3099, with a mock session open:
//   curl -X POST localhost:3099/api/mock/sessions/<id>/apps/app_mock_storefront/<step>
// where <step> is starting, running, build-failed, exited, stopped, hot-update, reload, reloaded or log.
const MOCK_APP_ID = "app_mock_storefront";
const MOCK_APP_URL = "http://localhost:5173/";
const MOCK_THEMES = ["#c2410c", "#0f766e", "#7c3aed", "#be123c"];

interface MockApp {
  id: string;
  sessionId: string;
  command: string;
  status: "starting" | "running" | "build-failed" | "exited" | "stopped";
  exitCode: number | null;
  url: string | null;
  ports: number[];
  output: string[];
  startTimer?: ReturnType<typeof setTimeout>;
}

function seedMockApp(sessionId: string): MockApp {
  return {
    id: MOCK_APP_ID,
    sessionId,
    command: "npm run dev",
    status: "running",
    exitCode: null,
    url: MOCK_APP_URL,
    ports: [5173],
    output: [
      "$ npm run dev",
      "",
      "> storefront@0.1.0 dev",
      "> vite",
      "",
      "  VITE v8.0.3  ready in 412 ms",
      "",
      "  ➜  Local:   http://localhost:5173/",
      "  ➜  Network: use --host to expose",
    ],
  };
}

function toAppResponse(app: MockApp) {
  return {
    id: app.id,
    command: app.command,
    status: app.status,
    exitCode: app.exitCode,
    url: app.url,
    ports: app.ports,
    printedUrls: app.url ? [app.url] : [],
    startedAt: new Date().toISOString(),
    logs: app.output.slice(-200),
  };
}

/** The request as a fetch Request, with its body: mock handlers read JSON bodies with req.json(). */
async function toMockRequest(req: IncomingMessage, url: URL): Promise<Request> {
  const method = req.method ?? "GET";
  let body: string | undefined;
  if (method !== "GET" && method !== "HEAD") {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk as Buffer);
    body = Buffer.concat(chunks).toString("utf8") || undefined;
  }
  return new Request(url.toString(), { method, headers: req.headers as HeadersInit, body });
}

function toCanvasResponse(canvas: MockCanvas) {
  return { canvasId: canvas.canvasId, kind: canvas.kind, title: canvas.title, version: canvas.version, state: canvas.state };
}

// Example linked pull requests and issues (Context tab and header chips) for mock-session-1.
function mockSmartLink(
  id: string,
  kind: "pull" | "issues",
  number: number,
  title: string,
  relationship: string,
  status: string,
  metadata: Record<string, unknown>,
) {
  const checkedAt = new Date(Date.now() - 12_000).toISOString();
  return {
    id,
    sessionId: "mock-session-1",
    url: `https://github.com/example/weave-fleet/${kind}/${number}`,
    providerId: "github",
    resourceType: kind === "pull" ? "pull_request" : "issue",
    resourceId: `example/weave-fleet#${number}`,
    title: `example/weave-fleet #${number}: ${title}`,
    status,
    statusLabel: status[0].toUpperCase() + status.slice(1),
    metadataJson: JSON.stringify({ owner: "example", repo: "weave-fleet", number, ...metadata }),
    isDismissed: false,
    isTerminal: status === "closed" || status === "merged",
    createdAt: checkedAt,
    updatedAt: checkedAt,
    relationship,
    enrichmentStatus: "resolved",
    lastCheckedAt: checkedAt,
  };
}

const MOCK_SMART_LINKS: Record<string, unknown[]> = {
  "mock-session-1": [
    mockSmartLink("mock-link-origin", "issues", 42, "Add mock API support", "origin", "open", {
      labels: [{ name: "enhancement", color: "a2eeef" }],
    }),
    mockSmartLink("mock-link-pr", "pull", 187, "feat(client): mock API support for session detail", "own", "open", {
      labels: [{ name: "client", color: "1d76db" }, { name: "needs-review", color: "fbca04" }],
      headRef: "feat/mock-api",
      baseRef: "main",
      mergeable: false,
      ci: {
        headSha: "a1b2c3d4e5f6",
        ciStatus: "failure",
        checkRuns: [
          { id: 1, name: "client-tests", status: "completed", conclusion: "failure", htmlUrl: "https://github.com/example/weave-fleet/actions/runs/1", workflowName: "CI", startedAt: null, completedAt: null },
          { id: 2, name: "e2e", status: "in_progress", conclusion: null, htmlUrl: "https://github.com/example/weave-fleet/actions/runs/2", workflowName: "CI", startedAt: null, completedAt: null },
          { id: 3, name: "build (ubuntu)", status: "completed", conclusion: "success", htmlUrl: "https://github.com/example/weave-fleet/actions/runs/3", workflowName: "CI", startedAt: null, completedAt: null },
        ],
      },
      reviewThreads: {
        unresolvedCount: 2,
        threads: [
          { threadNodeId: "t1", isResolved: false, isOutdated: false, path: "client/vite-plugin-mock-api.ts", line: 278, comments: [{ id: "c1", databaseId: 1, body: "Can we keep origin null here?", authorLogin: "reviewer", createdAt: "", url: "https://github.com/example/weave-fleet/pull/187#discussion_r1" }] },
          { threadNodeId: "t2", isResolved: false, isOutdated: false, path: "client/src/mocks/sessions.json", line: 33, comments: [{ id: "c2", databaseId: 2, body: "providerId should be builtin.github", authorLogin: "reviewer", createdAt: "", url: "https://github.com/example/weave-fleet/pull/187#discussion_r2" }] },
        ],
      },
    }),
    mockSmartLink("mock-link-mention", "issues", 156, "SignalR hub in mock mode", "mentioned", "closed", {}),
  ],
};

// Settings → Skills / Tools: installs globally and into a repository, plus one tool an older
// Fleet left somewhere OpenCode never looked (no installed path).
const MOCK_SKILLS = [
  {
    name: "fleet-api", source: 1, repoUrl: "https://github.com/pgermishuys/weave-fleet", ref: "main",
    subPath: "opencode/skills/fleet-api", localPath: "/home/you/.weave/skills/fleet-api", targetHarnesses: ["opencode"],
    scope: "global", projectPath: null, installedPaths: ["/home/you/.config/opencode/skills/fleet-api"],
    installedAt: "2026-09-04T19:20:56Z", updatedAt: "2026-09-13T09:01:28Z",
  },
  {
    name: "fleet-api", source: 1, repoUrl: "https://github.com/pgermishuys/weave-fleet", ref: "main",
    subPath: "opencode/skills/fleet-api", localPath: "/home/you/.weave/skills/fleet-api", targetHarnesses: ["opencode"],
    scope: "project", projectPath: "/home/you/src/opencode", installedPaths: ["/home/you/src/opencode/.opencode/skills/fleet-api"],
    installedAt: "2026-09-13T09:30:00Z", updatedAt: "2026-09-13T09:30:00Z",
  },
];

const MOCK_SKILL_CATALOG = [
  {
    name: "fleet-api", displayName: "Fleet API", description: "Drive Weave Fleet's HTTP API from an agent: sessions, prompts, canvases.",
    source: 1, repoUrl: "https://github.com/pgermishuys/weave-fleet", ref: "main", subPath: "opencode/skills/fleet-api", localPath: null,
    targetHarnesses: ["opencode"], author: "pgermishuys", version: "1.0.0", tags: ["fleet"], createdAt: null, updatedAt: null,
  },
];

const MOCK_TOOLS = [
  {
    name: "visualize", toolType: "native", displayName: null, description: null, command: null, args: null, env: null,
    repoUrl: "https://github.com/pgermishuys/weave-fleet", localPath: null,
    scope: "global", projectPath: null, installedPath: null,
    installedAt: "2026-09-13T09:01:25Z", updatedAt: "2026-09-13T09:01:25Z",
  },
  {
    name: "filesystem", toolType: "mcp", displayName: null, description: null, command: "npx",
    args: ["-y", "@modelcontextprotocol/server-filesystem", "."], env: null, repoUrl: null, localPath: null,
    scope: "project", projectPath: "/home/you/src/weave-fleet", installedPath: "/home/you/src/weave-fleet/opencode.json",
    installedAt: "2026-09-13T09:40:00Z", updatedAt: "2026-09-13T09:40:00Z",
  },
];

const MOCK_TOOL_CATALOG = [
  {
    name: "visualize", toolType: "native", source: 1, displayName: "Visualize", description: "Render sequence diagrams and flow graphs inline in the conversation.",
    command: null, args: null, env: null, repoUrl: "https://github.com/pgermishuys/weave-fleet", ref: "main", subPath: "opencode/tools", localPath: null,
    author: "pgermishuys", version: "1.0.0", tags: ["diagrams"], createdAt: null, updatedAt: null,
  },
];

export function mockApiPlugin(options: MockApiOptions = {}): Plugin {
  const mockDir = resolve(__dirname, "src/mocks");
  
  // Load fixtures at plugin initialization
  const fixtures = {
    sessions: JSON.parse(readFileSync(resolve(mockDir, "sessions.json"), "utf-8")),
    fleetSummary: JSON.parse(readFileSync(resolve(mockDir, "fleet-summary.json"), "utf-8")),
    config: JSON.parse(readFileSync(resolve(mockDir, "config.json"), "utf-8")),
    projects: JSON.parse(readFileSync(resolve(mockDir, "projects.json"), "utf-8")),
  };

  let devServer: ViteDevServer | undefined;
  let preview: MockPreview | undefined;
  let themeIndex = 0;
  let nextAppNumber = 1;
  let nextCanvasNumber = 1;
  const canvasesBySession = new Map<string, MockCanvas[]>();
  const appsBySession = new Map<string, MockApp[]>();

  function sessionApps(sessionId: string): MockApp[] {
    let apps = appsBySession.get(sessionId);
    if (!apps) {
      apps = [seedMockApp(sessionId)];
      appsBySession.set(sessionId, apps);
    }
    return apps;
  }

  function findApp(sessionId: string, appId: string): MockApp | undefined {
    return sessionApps(sessionId).find((app) => app.id === appId);
  }

  function pushAppUpdated(app: MockApp, reason: string): void {
    pushHubEvent(app.sessionId, "app.updated", {
      sessionId: app.sessionId,
      appId: app.id,
      command: app.command,
      status: app.status,
      url: app.url,
      ports: app.ports,
      exitCode: app.exitCode,
      reason,
    });
  }

  function setApp(app: MockApp, status: MockApp["status"], reason: string, exitCode: number | null = null): void {
    clearTimeout(app.startTimer);
    app.status = status;
    app.exitCode = exitCode;
    if (status === "running") {
      app.url = MOCK_APP_URL;
      app.ports = [5173];
    } else if (status !== "build-failed") {
      app.ports = [];
    }
    pushAppUpdated(app, reason);
  }

  /** Starts (or restarts) the app; it serves its page after a moment unless `stay` keeps it starting. */
  function startApp(app: MockApp, reason: "started" | "restarted", stay = false): void {
    setApp(app, "starting", reason);
    // Like Fleet's runner: a line between runs, then the new run's output.
    if (app.output.length > 0) app.output.push("── restarted by Fleet ──");
    app.output.push(`$ ${app.command}`, "", "> vite", "");
    if (stay) return;
    app.startTimer = setTimeout(() => {
      app.output.push("  VITE v8.0.3  ready in 388 ms", "", `  ➜  Local:   ${MOCK_APP_URL}`);
      setApp(app, "running", "ready");
    }, 1500);
  }

  function openBrowserCanvas(sessionId: string, title: string, state: Record<string, unknown>): MockCanvas {
    const canvases = sessionCanvases(sessionId);
    let canvas = canvases.find((candidate) => candidate.title === title);
    if (canvas) {
      canvas.state = state;
      canvas.closed = false;
      canvas.version += 1;
    } else {
      canvas = { canvasId: `cv_mock_browser_${nextCanvasNumber++}`, kind: "browser", title, version: 1, state, closed: false, revision: 0 };
      canvases.push(canvas);
    }
    pushCanvasUpdated(sessionId, canvas, "opened");
    pushHubEvent(sessionId, "canvas.focused", { sessionId, canvasId: canvas.canvasId });
    return canvas;
  }
  const terminalsBySession = new Map<string, { list: MockTerminal[]; next: number }>();

  function sessionTerminals(sessionId: string): { list: MockTerminal[]; next: number } {
    let terminals = terminalsBySession.get(sessionId);
    if (!terminals) {
      terminals = { list: [], next: 0 };
      terminalsBySession.set(sessionId, terminals);
    }
    return terminals;
  }

  function sessionCanvases(sessionId: string): MockCanvas[] {
    let canvases = canvasesBySession.get(sessionId);
    if (!canvases) {
      canvases = seedMockCanvases();
      canvasesBySession.set(sessionId, canvases);
    }
    return canvases;
  }

  // Mock mode has no SignalR hub. The client listens for these on Vite's dev
  // socket and handles them like hub events (see use-signalr-socket.ts).
  function pushHubEvent(sessionId: string, type: string, properties: Record<string, unknown>): void {
    devServer?.ws.send({
      type: "custom",
      event: "fleet:mock-hub-event",
      data: { topic: `session:${sessionId}`, data: { type, eventId: null, properties } },
    });
  }

  function pushCanvasUpdated(sessionId: string, canvas: MockCanvas, summary: string): void {
    pushHubEvent(sessionId, "canvas.updated", { sessionId, ...toCanvasResponse(canvas), actor: "agent", summary });
  }

  function json(body: unknown, status = 200): Response {
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  }

  const MOCK_FOLDERS: Record<string, { isGitRepo: boolean }> = {
    "/home/you/src/weave-fleet": { isGitRepo: true },
    "/home/you/src/opencode": { isGitRepo: true },
    "/home/you/src/notes": { isGitRepo: false },
    "/home/you/work/agent-playbook": { isGitRepo: true },
    "/home/you/Downloads": { isGitRepo: false },
  };
  /** Folders added to the workspace roots from the new-session page. */
  const addedRoots = new Set<string>();

  function mockRepositories() {
    const added = [...addedRoots]
      .filter((path) => MOCK_FOLDERS[path]?.isGitRepo)
      .map((path) => ({ name: path.split("/").pop() ?? path, path, parentRoot: path }));
    return [
      { name: "weave-fleet", path: "/home/you/src/weave-fleet", parentRoot: "/home/you/src" },
      { name: "opencode", path: "/home/you/src/opencode", parentRoot: "/home/you/src" },
      ...added,
    ];
  }

  const routes: MockRoute[] = [
    // ─── Terminals ──────────────────────────────────────────────────────────────
    // The shell itself is pretend and runs in the browser (terminal-mock-shell.ts);
    // these routes only keep each session's list of tabs.
    {
      pattern: /^\/api\/sessions\/([^/]+)\/terminals$/,
      handler: (url, req) => {
        const id = decodeURIComponent(url.pathname.split("/")[3]);
        const terminals = sessionTerminals(id);
        console.log(`[mock-api] ${req.method} /api/sessions/${id}/terminals`);
        if (req.method === "GET") return json(terminals.list);
        if (req.method !== "POST") return json({ error: "Method not allowed" }, 405);

        if (terminals.list.length >= 8) return json({ error: "This session already has 8 terminals. Close one to open another." }, 409);
        terminals.next += 1;
        const taken = new Set(terminals.list.map((terminal) => terminal.title));
        let title = "zsh";
        for (let n = 2; taken.has(title); n++) title = `zsh ${n}`;
        const terminal: MockTerminal = { id: `${id}-term-${terminals.next}`, title, status: "running", createdAt: new Date().toISOString() };
        terminals.list.push(terminal);
        pushHubEvent(id, "terminal.opened", { sessionId: id, terminalId: terminal.id, title });
        return json(terminal, 201);
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/terminals\/([^/]+)$/,
      handler: (url, req) => {
        const [, , , rawId, , rawTerminalId] = url.pathname.split("/");
        const id = decodeURIComponent(rawId);
        const terminalId = decodeURIComponent(rawTerminalId);
        console.log(`[mock-api] ${req.method} /api/sessions/${id}/terminals/${terminalId}`);
        if (req.method !== "DELETE") return json({ error: "Method not allowed" }, 405);

        const terminals = sessionTerminals(id);
        const index = terminals.list.findIndex((terminal) => terminal.id === terminalId);
        if (index < 0) return json({ error: `Terminal ${terminalId} not found.` }, 404);
        const [closed] = terminals.list.splice(index, 1);
        pushHubEvent(id, "terminal.closed", { sessionId: id, terminalId, title: closed.title });
        return new Response(null, { status: 204 });
      },
    },

    // ─── Browser canvas: apps and previews ──────────────────────────────────────
    {
      pattern: /^\/api\/sessions\/([^/]+)\/browser\/proxy$/,
      handler: async (url, req) => {
        const body = (await req.json().catch(() => ({}))) as { url?: string };
        if (!preview) return json({ error: "The mock preview isn't running." }, 409);
        const port = Number(new URL(body.url ?? MOCK_APP_URL).port || 80);
        return json({ slug: "pmock", port, target: body.url ?? MOCK_APP_URL, origin: preview.origin });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/browser$/,
      handler: async (url, req) => {
        const id = decodeURIComponent(url.pathname.split("/")[3]);
        const body = (await req.json().catch(() => ({}))) as { url?: string };
        let page: URL;
        try {
          page = new URL(body.url ?? "");
        } catch {
          return json({ error: "\"url\" must be an http or https address on this machine, e.g. http://localhost:5173/." }, 422);
        }
        console.log(`[mock-api] POST /api/sessions/${id}/browser ${page}`);
        return json({ canvasId: openBrowserCanvas(id, page.host, { url: page.toString() }).canvasId });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/apps$/,
      handler: async (url, req) => {
        const id = decodeURIComponent(url.pathname.split("/")[3]);
        const apps = sessionApps(id);
        console.log(`[mock-api] ${req.method} /api/sessions/${id}/apps`);
        if (req.method === "GET") {
          return json({ apps: apps.map((app) => ({ ...toAppResponse(app), logs: [] })), previewCommand: "npm run dev" });
        }

        const command = (((await req.json().catch(() => ({}))) as { command?: string }).command ?? "").trim();
        if (!command) return json({ error: "\"command\" is required, e.g. \"npm run dev\"." }, 422);
        const live = apps.filter((app) => app.status === "starting" || app.status === "running" || app.status === "build-failed");

        let app = apps.find((candidate) => candidate.command === command);
        if (!app || !live.includes(app)) {
          if (live.length >= 3) {
            return json({ error: `This session already runs 3 apps, its limit: ${live.map((running) => `\`${running.command}\` (${running.id})`).join(", ")}.` }, 409);
          }
          if (!app) {
            app = { id: `app_mock_${nextAppNumber++}`, sessionId: id, command, status: "stopped", exitCode: null, url: null, ports: [], output: [] };
            apps.push(app);
          }
          startApp(app, "started");
        }

        const showing = sessionCanvases(id).find((canvas) => canvas.kind === "browser" && !canvas.closed && canvas.state.appId === app.id);
        const canvas = showing ?? openBrowserCanvas(id, command.slice(0, 120), { url: app.status === "running" ? app.url ?? "" : "", appId: app.id });
        if (showing) pushHubEvent(id, "canvas.focused", { sessionId: id, canvasId: showing.canvasId });
        return json({ app: toAppResponse(app), canvasId: canvas.canvasId });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/apps\/([^/]+)$/,
      handler: (url) => {
        const [, , , rawId, , rawAppId] = url.pathname.split("/");
        const app = findApp(decodeURIComponent(rawId), decodeURIComponent(rawAppId));
        return app ? json(toAppResponse(app)) : json({ error: `App ${rawAppId} not found.` }, 404);
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/apps\/([^/]+)\/output$/,
      handler: (url) => {
        const [, , , rawId, , rawAppId] = url.pathname.split("/");
        const app = findApp(decodeURIComponent(rawId), decodeURIComponent(rawAppId));
        if (!app) return json({ error: `App ${rawAppId} not found.` }, 404);
        const after = Math.max(0, Number(url.searchParams.get("after") ?? 0) || 0);
        return json({ lines: app.output.slice(after), next: app.output.length });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/apps\/([^/]+)\/(restart|stop)$/,
      handler: (url, req) => {
        const [, , , rawId, , rawAppId, action] = url.pathname.split("/");
        const app = findApp(decodeURIComponent(rawId), decodeURIComponent(rawAppId));
        console.log(`[mock-api] ${req.method} /api/sessions/${rawId}/apps/${rawAppId}/${action}`);
        if (!app) return json({ error: `App ${rawAppId} not found.` }, 404);
        if (action === "stop") {
          if (app.status !== "stopped" && app.status !== "exited") setApp(app, "stopped", "stopped");
          return new Response(null, { status: 204 });
        }
        const wasLive = app.status === "starting" || app.status === "running" || app.status === "build-failed";
        startApp(app, wasLive ? "restarted" : "started");
        return json(toAppResponse(app));
      },
    },
    {
      pattern: /^\/api\/mock\/sessions\/([^/]+)\/apps\/([^/]+)\/([a-z-]+)$/,
      handler: (url) => {
        const [, , , , rawId, , rawAppId, step] = url.pathname.split("/");
        const app = findApp(decodeURIComponent(rawId), decodeURIComponent(rawAppId));
        if (!app) return json({ error: `App ${rawAppId} not found.` }, 404);

        switch (step) {
          case "starting":
            startApp(app, "restarted", true);
            break;
          case "running":
            setApp(app, "running", "ready");
            break;
          case "build-failed":
            app.output.push(
              "[vite] Internal server error: /src/App.tsx: Unexpected token (14:2)",
              "  12 |   return (",
              "  13 |     <main>",
              "> 14 |   </div>",
              "     |   ^",
            );
            setApp(app, "build-failed", "build-failed");
            break;
          case "exited":
            app.output.push("node:internal/modules/cjs/loader:1228", "  throw err;", "Error: Cannot find module './server/routes'", "npm ERR! code 1");
            setApp(app, "exited", "exited", 1);
            break;
          case "stopped":
            setApp(app, "stopped", "stopped");
            break;
          case "hot-update": {
            themeIndex = (themeIndex + 1) % MOCK_THEMES.length;
            app.output.push(`[vite] hmr update /src/theme.css`);
            preview?.send({ type: "update", css: `:root { --brand: ${MOCK_THEMES[themeIndex]}; }` });
            break;
          }
          case "reload":
            app.output.push("[vite] page reload index.html");
            preview?.send({ type: "full-reload" });
            break;
          case "reloaded":
            pushAppUpdated(app, "reloaded");
            break;
          case "log":
            app.output.push(`[vite] ${new Date().toLocaleTimeString()} GET /cart 200`);
            break;
          default:
            return json({ error: `Unknown step ${step}` }, 400);
        }
        console.log(`[mock-api] ${app.id} in ${rawId}: ${step} (${app.status})`);
        return json(toAppResponse(app));
      },
    },

    // ─── Canvases ───────────────────────────────────────────────────────────────
    // Try on 3099, with a mock session open:
    //   curl -X POST localhost:3099/api/mock/sessions/<id>/canvases/cv_mock_diagram/revise
    //   curl -X POST localhost:3099/api/mock/sessions/<id>/canvases/reopen
    {
      pattern: /^\/api\/sessions\/([^/]+)\/canvases$/,
      handler: (url) => {
        const id = decodeURIComponent(url.pathname.split("/")[3]);
        console.log(`[mock-api] GET /api/sessions/${id}/canvases`);
        return json(sessionCanvases(id).filter((canvas) => !canvas.closed).map(toCanvasResponse));
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/canvases\/([^/]+)\/focus$/,
      handler: (url) => {
        const [, , , rawId, , rawCanvasId] = url.pathname.split("/");
        const id = decodeURIComponent(rawId);
        const canvas = sessionCanvases(id).find((candidate) => candidate.canvasId === decodeURIComponent(rawCanvasId));
        if (!canvas) return json({ error: "Canvas not found" }, 404);
        if (canvas.closed) {
          canvas.closed = false;
          pushCanvasUpdated(id, canvas, "reopened");
        }
        pushHubEvent(id, "canvas.focused", { sessionId: id, canvasId: canvas.canvasId });
        return json(toCanvasResponse(canvas));
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/canvases\/([^/]+)$/,
      handler: (url, req) => {
        const [, , , rawId, , rawCanvasId] = url.pathname.split("/");
        const id = decodeURIComponent(rawId);
        const canvasId = decodeURIComponent(rawCanvasId);
        console.log(`[mock-api] ${req.method} /api/sessions/${id}/canvases/${canvasId}`);
        if (req.method !== "DELETE") return json({ error: "Method not allowed" }, 405);

        const canvas = sessionCanvases(id).find((candidate) => candidate.canvasId === canvasId);
        if (!canvas) return json({ error: "Canvas not found" }, 404);
        if (!canvas.closed) {
          canvas.closed = true;
          pushHubEvent(id, "canvas.closed", { sessionId: id, canvasId });
        }
        return new Response(null, { status: 204 });
      },
    },
    {
      pattern: /^\/api\/mock\/sessions\/([^/]+)\/canvases\/([^/]+)\/revise$/,
      handler: (url) => {
        const [, , , , rawId, , rawCanvasId] = url.pathname.split("/");
        const id = decodeURIComponent(rawId);
        const canvasId = decodeURIComponent(rawCanvasId);
        const canvas = sessionCanvases(id).find((candidate) => candidate.canvasId === canvasId);
        if (!canvas || canvas.closed) return json({ error: "No open canvas with that id" }, 404);

        let summary: string;
        if (canvas.kind === "diagram") {
          const step = MOCK_DIAGRAM_REVISIONS[canvas.revision % MOCK_DIAGRAM_REVISIONS.length];
          if (canvas.revision > 0 && canvas.revision % MOCK_DIAGRAM_REVISIONS.length === 0) {
            canvas.state = { direction: "TB", nodes: structuredClone(MOCK_DIAGRAM_NODES), edges: structuredClone(MOCK_DIAGRAM_EDGES) };
          }
          step.apply(canvas.state as Parameters<typeof step.apply>[0]);
          summary = step.summary;
        } else {
          canvas.state = { source: `${canvas.state.source as string}\n  Browser->>Browser: redraw in place (v${canvas.version + 1})` };
          summary = `${(canvas.state.source as string).split("\n").length} lines`;
        }
        canvas.revision += 1;
        canvas.version += 1;
        console.log(`[mock-api] revised ${canvasId} in ${id} to v${canvas.version} (${summary})`);
        pushCanvasUpdated(id, canvas, summary);
        return json(toCanvasResponse(canvas));
      },
    },
    {
      pattern: /^\/api\/mock\/sessions\/([^/]+)\/canvases\/reopen$/,
      handler: (url) => {
        const id = decodeURIComponent(url.pathname.split("/")[4]);
        const reopened = sessionCanvases(id).filter((canvas) => canvas.closed);
        for (const canvas of reopened) {
          canvas.closed = false;
          pushCanvasUpdated(id, canvas, "reopened");
          pushHubEvent(id, "canvas.focused", { sessionId: id, canvasId: canvas.canvasId });
        }
        console.log(`[mock-api] reopened ${reopened.length} canvas(es) in ${id}`);
        return json(reopened.map(toCanvasResponse));
      },
    },
    {
      pattern: /^\/api\/fleet\/summary$/,
      handler: () => {
        console.log("[mock-api] GET /api/fleet/summary");
        return new Response(JSON.stringify(fixtures.fleetSummary), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/config$/,
      handler: () => {
        console.log("[mock-api] GET /api/config");
        return new Response(JSON.stringify(fixtures.config), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/config\/client$/,
      handler: () => {
        console.log("[mock-api] GET /api/config/client");
        return new Response(JSON.stringify({
          cloudMode: false,
          authEnabled: false,
          tokenAuthEnabled: false,
          availableHarnesses: ["opencode"],
          terminalEnabled: true,
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/user\/me$/,
      handler: () => {
        console.log("[mock-api] GET /api/user/me");
        return new Response(JSON.stringify({
          userId: "mock-user-001",
          email: "dev@weavefleet.local",
          displayName: "Mock Developer",
          onboardingCompleted: true,
          onboardingStatus: {
            completed: true,
            hasStoredCredentials: true,
            hasCreatedSession: true,
          },
          createdAt: "2025-01-15T10:00:00Z",
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/projects(\?.*)?$/,
      handler: () => {
        console.log("[mock-api] GET /api/projects");
        return new Response(JSON.stringify(fixtures.projects), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/healthz$/,
      handler: () => {
        console.log("[mock-api] GET /healthz");
        return new Response(JSON.stringify({ status: "ok" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/harnesses$/,
      handler: () => {
        console.log("[mock-api] GET /api/harnesses");
        return new Response(JSON.stringify([
          {
            type: "opencode",
            displayName: "OpenCode",
            available: true,
            userEnabled: true,
            capabilities: {
              requiresInitialPrompt: false,
              supportsAgents: true,
              supportsModelSelection: true,
              supportsCommands: true,
              supportsForking: true,
              supportsResume: true,
              supportsImageAttachments: true,
              supportsStreaming: true,
              supportsDelegation: true,
            },
          },
        ]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/workspace-roots$/,
      handler: async (_url, req) => {
        if (req.method === "POST") {
          const { path } = await req.json() as { path: string };
          console.log(`[mock-api] POST /api/workspace-roots ${path}`);
          if (!MOCK_FOLDERS[path]) return json({ error: `Path does not exist: ${path}` }, 400);
          addedRoots.add(path);
          return json({ id: `mock-root-${addedRoots.size + 1}`, path });
        }
        console.log("[mock-api] GET /api/workspace-roots");
        return json({
          roots: [
            { id: "mock-root-1", path: "/home/you/src", source: "user", exists: true },
            ...[...addedRoots].map((path, index) => ({ id: `mock-root-${index + 2}`, path, source: "user", exists: true })),
          ],
        });
      },
    },
    // Folders the new-session "Browse for a folder" view knows about. Anything under /home/you/src
    // is inside the workspace roots; the others are until someone adds them.
    {
      pattern: /^\/api\/directories\/inspect$/,
      handler: (url) => {
        const typed = (url.searchParams.get("path") ?? "").trim();
        const path = Object.keys(MOCK_FOLDERS).find((known) => known.toLowerCase() === typed.replaceAll("\\", "/").toLowerCase()) ?? typed;
        console.log(`[mock-api] GET /api/directories/inspect?path=${typed}`);
        const folder = MOCK_FOLDERS[path];
        return json({
          path,
          exists: Boolean(folder),
          isGitRepo: folder?.isGitRepo ?? false,
          isWithinRoots: Boolean(folder) && (path.startsWith("/home/you/src/") || addedRoots.has(path)),
        });
      },
    },
    {
      pattern: /^\/api\/repositories\/refresh$/,
      handler: () => {
        console.log("[mock-api] POST /api/repositories/refresh");
        return json({ repositories: mockRepositories(), scannedAt: Date.now() });
      },
    },
    {
      pattern: /^\/api\/preferences$/,
      handler: () => {
        console.log("[mock-api] GET /api/preferences");
        return new Response(JSON.stringify({}), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/available-tools$/,
      handler: () => {
        console.log("[mock-api] GET /api/available-tools");
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/plugins$/,
      handler: () => {
        console.log("[mock-api] GET /api/plugins");
        return new Response(JSON.stringify({ plugins: [], statuses: [] }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/repositories$/,
      handler: () => {
        console.log("[mock-api] GET /api/repositories");
        return json({ repositories: mockRepositories(), scannedAt: Date.now() });
      },
    },
    {
      pattern: /^\/api\/repositories\/worktrees$/,
      handler: (url) => {
        const path = url.searchParams.get("path") ?? "";
        console.log(`[mock-api] GET /api/repositories/worktrees?path=${path}`);
        const worktrees = path.endsWith("/weave-fleet")
          ? [
            { path: `${path}-worktrees/fleet-terminal-drawer`, branch: "fleet/terminal-drawer", commitHash: "a1b2c3d" },
            { path: `${path}-worktrees/fleet-session-status`, branch: "fleet/session-status", commitHash: "d4e5f6a" },
          ]
          : [];
        return json({ worktrees });
      },
    },
    {
      pattern: /^\/api\/repositories\/detail$/,
      handler: (url) => {
        const path = url.searchParams.get("path") ?? "";
        console.log(`[mock-api] GET /api/repositories/detail?path=${path}`);
        const current = path.endsWith("/weave-fleet") ? "feat/browser-canvas" : "main";
        const branch = (name: string, shortHash: string, message: string) => ({
          name,
          shortHash,
          message,
          author: "",
          authorEmail: "",
          date: "",
          isCurrent: name === current,
          isRemote: name.startsWith("origin/"),
        });
        return json({
          repository: {
            name: path.split("/").pop() ?? path,
            path,
            branch: current,
            uncommittedCount: 0,
            totalCommitCount: 0,
            firstCommitDate: null,
            lastCommitDate: null,
            branches: [
              branch(current, "a1b2c3d", "Work in progress"),
              branch("origin/main", "e4f5a6b", "Merge pull request #199"),
              ...(current === "main" ? [] : [branch("main", "c7d8e9f", "Merge pull request #197")]),
              branch("origin/release/2.0", "0a1b2c3", "Cut 2.0"),
            ],
            tags: [],
            recentCommits: [],
            remotes: [],
            readmeContent: null,
            readmeFilename: null,
            defaultBranch: "main",
            defaultBase: "origin/main",
          },
        });
      },
    },
    // ─── Skills & tools (read-only: mock requests carry no body) ───────────────
    {
      pattern: /^\/api\/skills$/,
      handler: () => json({ skills: MOCK_SKILLS }),
    },
    {
      pattern: /^\/api\/skills\/catalog$/,
      handler: () => json({ entries: MOCK_SKILL_CATALOG, isStale: false, cachedAt: null }),
    },
    {
      pattern: /^\/api\/tools$/,
      handler: () => json({ tools: MOCK_TOOLS }),
    },
    {
      pattern: /^\/api\/tools\/catalog$/,
      handler: () => json({ entries: MOCK_TOOL_CATALOG, isStale: false, cachedAt: null }),
    },
    // ─── Priority 1: Session Detail & Interaction ───────────────────────────────
    {
      pattern: /^\/api\/sessions\/([^/]+)$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET /api/sessions/${id}`);
        
        // Find the session from the fixture
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const sessionItem = fixtures.sessions.find((s: any) => s.session.id === id);
        if (!sessionItem) {
          return new Response(JSON.stringify({ error: "Session not found" }), {
            status: 404,
            headers: { "Content-Type": "application/json" },
          });
        }
        
        // Build the GetSessionResponse shape
        const response = {
          id: sessionItem.session.id,
          instanceId: sessionItem.instanceId,
          parentSessionId: sessionItem.parentSessionId,
          workspaceId: sessionItem.workspaceId,
          workspaceDirectory: sessionItem.workspaceDirectory,
          workspaceDisplayName: sessionItem.workspaceDisplayName,
          sourceDirectory: sessionItem.sourceDirectory,
          isolationStrategy: sessionItem.isolationStrategy,
          branch: sessionItem.branch,
          title: sessionItem.session.title,
          createdAt: new Date(sessionItem.session.time.created).toISOString(),
          stoppedAt: null,
          activityStatus: sessionItem.activityStatus,
          lifecycleStatus: sessionItem.lifecycleStatus,
          retentionStatus: sessionItem.retentionStatus,
          archivedAt: sessionItem.archivedAt,
          totalTokens: sessionItem.totalTokens,
          totalCost: sessionItem.totalCost,
          harnessType: sessionItem.harnessType,
          projectId: sessionItem.projectId,
          projectName: sessionItem.projectName ?? null,
          origin: sessionItem.origin || null,
          capabilities: {
            canPrompt: true,
            canAbort: sessionItem.activityStatus === "busy",
            canFork: true,
            canDelete: true,
            canRename: true,
            promptDisabledReason: null,
            abortDisabledReason: sessionItem.activityStatus !== "busy" ? "Session is not active" : null,
            forkDisabledReason: null,
            deleteDisabledReason: null,
            renameDisabledReason: null,
          },
        };
        
        return new Response(JSON.stringify(response), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions$/,
      handler: async (url, req) => {
        if (req.method === "POST") {
          console.log("[mock-api] POST /api/sessions");
          const newSessionId = `mock-session-${Date.now()}`;
          return new Response(JSON.stringify({
            instanceId: `mock-instance-${Date.now()}`,
            workspaceId: `mock-workspace-${Date.now()}`,
            session: {
              id: newSessionId,
              title: "New Session",
              isHidden: false,
              time: {
                created: Date.now(),
                updated: Date.now(),
              },
            },
          }), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          });
        }
        
        // GET /api/sessions
        console.log("[mock-api] GET /api/sessions");
        return new Response(JSON.stringify(fixtures.sessions), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/prompt$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] POST /api/sessions/${id}/prompt`);
        return new Response(JSON.stringify({ 
          eventId: 1001,
          correlationId: `corr-${Date.now()}`,
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/abort$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] POST /api/sessions/${id}/abort`);
        return new Response(JSON.stringify({}), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/messages$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET /api/sessions/${id}/messages`);
        
        const now = Date.now();
        const mockMessages = [
          // Message 1: User request
          {
            id: "msg-1",
            role: "user",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "Add JWT validation middleware that checks the Authorization header, validates the token signature, and extracts claims into the request context.",
              },
            ],
            timestamp: new Date(now - 600000).toISOString(),
            textContent: "Add JWT validation middleware that checks the Authorization header, validates the token signature, and extracts claims into the request context.",
          },
          // Message 2: Assistant reasoning
          {
            id: "msg-2",
            role: "assistant",
            parts: [
              {
                type: "reasoning",
                kind: 0,
                text: "I need to understand the existing middleware chain and authentication setup to determine where JWT validation should fit. First, I'll check if jsonwebtoken is already a dependency, then examine the current middleware structure to see what's already in place. This will help me design a middleware that integrates cleanly with the existing architecture.",
                summary: "Analyzing existing middleware and auth setup",
              },
            ],
            timestamp: new Date(now - 590000).toISOString(),
            textContent: "",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 3: Assistant with read/grep tools
          {
            id: "msg-3",
            role: "assistant",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "Let me check the existing dependencies and middleware structure.",
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-1",
                toolName: "read",
                arguments: { filePath: "package.json" },
                state: 2,
                metadata: {
                  output: '{\n  "name": "api-server",\n  "version": "1.0.0",\n  "dependencies": {\n    "express": "^4.18.2",\n    "jsonwebtoken": "^9.0.2",\n    "bcrypt": "^5.1.1"\n  }\n}',
                  summary: "Read 9 lines",
                },
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-2",
                toolName: "grep",
                arguments: { pattern: "jsonwebtoken", include: "*.ts" },
                state: 2,
                metadata: {
                  output: "src/utils/token.ts:1:import jwt from 'jsonwebtoken';\nsrc/utils/token.ts:5:export function generateToken(payload: object) {\nsrc/utils/token.ts:6:  return jwt.sign(payload, process.env.JWT_SECRET!, { expiresIn: '1h' });\nsrc/utils/token.ts:7:}",
                  summary: "Found 4 matches in 1 file",
                },
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-3",
                toolName: "read",
                arguments: { filePath: "src/middleware/index.ts" },
                state: 2,
                metadata: {
                  output: "export { errorHandler } from './error-handler';\nexport { requestLogger } from './logger';\nexport { rateLimiter } from './rate-limiter';",
                  summary: "Read 3 lines",
                },
              },
            ],
            timestamp: new Date(now - 580000).toISOString(),
            textContent: "Let me check the existing dependencies and middleware structure.",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 4: Assistant with write tool
          {
            id: "msg-4",
            role: "assistant",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "I'll create the JWT middleware.",
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-4",
                toolName: "write",
                arguments: { filePath: "src/middleware/auth.ts", content: "..." },
                state: 2,
                metadata: {
                  output: "File written successfully",
                  diffLines: [
                    { type: "add", content: "import { Request, Response, NextFunction } from 'express';", newLineNumber: 1 },
                    { type: "add", content: "import jwt from 'jsonwebtoken';", newLineNumber: 2 },
                    { type: "add", content: "import { UnauthorizedError } from '../errors';", newLineNumber: 3 },
                    { type: "add", content: "", newLineNumber: 4 },
                    { type: "add", content: "export interface AuthRequest extends Request {", newLineNumber: 5 },
                    { type: "add", content: "  user?: { id: string; email: string; role: string };", newLineNumber: 6 },
                    { type: "add", content: "}", newLineNumber: 7 },
                    { type: "add", content: "", newLineNumber: 8 },
                    { type: "add", content: "export function authenticate(req: AuthRequest, res: Response, next: NextFunction) {", newLineNumber: 9 },
                    { type: "add", content: "  const authHeader = req.headers.authorization;", newLineNumber: 10 },
                    { type: "add", content: "  if (!authHeader?.startsWith('Bearer ')) {", newLineNumber: 11 },
                    { type: "add", content: "    throw new UnauthorizedError('Missing or invalid Authorization header');", newLineNumber: 12 },
                    { type: "add", content: "  }", newLineNumber: 13 },
                    { type: "add", content: "", newLineNumber: 14 },
                    { type: "add", content: "  const token = authHeader.slice(7);", newLineNumber: 15 },
                    { type: "add", content: "  try {", newLineNumber: 16 },
                    { type: "add", content: "    const payload = jwt.verify(token, process.env.JWT_SECRET!) as any;", newLineNumber: 17 },
                    { type: "add", content: "    req.user = { id: payload.sub, email: payload.email, role: payload.role };", newLineNumber: 18 },
                    { type: "add", content: "    next();", newLineNumber: 19 },
                    { type: "add", content: "  } catch (err) {", newLineNumber: 20 },
                    { type: "add", content: "    throw new UnauthorizedError('Invalid or expired token');", newLineNumber: 21 },
                    { type: "add", content: "  }", newLineNumber: 22 },
                    { type: "add", content: "}", newLineNumber: 23 },
                  ],
                },
              },
            ],
            timestamp: new Date(now - 570000).toISOString(),
            textContent: "I'll create the JWT middleware.",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 5: Assistant with edit tool
          {
            id: "msg-5",
            role: "assistant",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "Now I'll register the middleware in the index.",
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-5",
                toolName: "edit",
                arguments: {
                  filePath: "src/middleware/index.ts",
                  oldString: "export { errorHandler } from './error-handler';\nexport { requestLogger } from './logger';\nexport { rateLimiter } from './rate-limiter';",
                  newString: "export { authenticate } from './auth';\nexport { errorHandler } from './error-handler';\nexport { requestLogger } from './logger';\nexport { rateLimiter } from './rate-limiter';",
                },
                state: 2,
                metadata: {
                  output: "Edit applied successfully",
                  diffLines: [
                    { type: "add", content: "export { authenticate } from './auth';", newLineNumber: 1 },
                    { type: "context", content: "export { errorHandler } from './error-handler';", newLineNumber: 2 },
                    { type: "context", content: "export { requestLogger } from './logger';", newLineNumber: 3 },
                    { type: "context", content: "export { rateLimiter } from './rate-limiter';", newLineNumber: 4 },
                  ],
                },
              },
            ],
            timestamp: new Date(now - 560000).toISOString(),
            textContent: "Now I'll register the middleware in the index.",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 6: Assistant with bash tool (running)
          {
            id: "msg-6",
            role: "assistant",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "Let me verify the tests pass.",
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-6",
                toolName: "bash",
                arguments: { command: "npm test -- --testPathPattern=middleware", description: "Run middleware tests" },
                state: 1,
              },
            ],
            timestamp: new Date(now - 550000).toISOString(),
            textContent: "Let me verify the tests pass.",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 7: Assistant with glob tool
          {
            id: "msg-7",
            role: "assistant",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "Searching for related test files.",
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-7",
                toolName: "glob",
                arguments: { pattern: "**/*.test.ts" },
                state: 2,
                metadata: {
                  output: "src/middleware/auth.test.ts\nsrc/middleware/error-handler.test.ts\nsrc/middleware/logger.test.ts\nsrc/middleware/rate-limiter.test.ts\nsrc/utils/token.test.ts",
                  summary: "Found 5 files",
                },
              },
            ],
            timestamp: new Date(now - 540000).toISOString(),
            textContent: "Searching for related test files.",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 8: Assistant with skill tool
          {
            id: "msg-8",
            role: "assistant",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "Loading the security review skill.",
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-8",
                toolName: "skill",
                arguments: { name: "security-review" },
                state: 2,
                metadata: {
                  output: "Skill loaded: security-review",
                  summary: "Loaded security-review skill",
                },
              },
            ],
            timestamp: new Date(now - 530000).toISOString(),
            textContent: "Loading the security review skill.",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 9: Assistant with task/delegation tool
          {
            id: "msg-9",
            role: "assistant",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "Delegating the security review to the warp agent.",
              },
              {
                type: "tool",
                kind: 0,
                toolCallId: "call-9",
                toolName: "task",
                arguments: {
                  agent: "warp",
                  task: "Review src/middleware/auth.ts for security vulnerabilities",
                },
                state: 2,
                metadata: {
                  output: "Security review completed. No critical issues found. Recommendations:\n1. Consider adding rate limiting for failed auth attempts\n2. Add logging for authentication failures\n3. Validate JWT algorithm to prevent 'none' algorithm attacks",
                  summary: "Security review completed",
                },
              },
            ],
            timestamp: new Date(now - 520000).toISOString(),
            textContent: "Delegating the security review to the warp agent.",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 9b: Assistant with visualize tool output (renders as canvases)
          {
            id: "msg-9b",
            role: "assistant",
            parts: [
              {
                "type": "text",
                "kind": 0,
                "text": "Here is how a request moves through the new middleware, and how token refresh will work."
              },
              {
                "type": "tool",
                "kind": 0,
                "toolCallId": "call-9b",
                "toolName": "visualize",
                "arguments": {
                  "type": "flow",
                  "title": "Auth request flow"
                },
                "state": 2,
                "metadata": {
                  "output": "{\"$type\": \"visual/flow\", \"title\": \"Auth request flow\", \"content\": {\"direction\": \"TB\", \"nodes\": [{\"id\": \"client\", \"label\": \"HTTP request\", \"type\": \"input\"}, {\"id\": \"auth\", \"label\": \"authenticate()\"}, {\"id\": \"verify\", \"label\": \"jsonwebtoken.verify\"}, {\"id\": \"ctx\", \"label\": \"req.user\"}, {\"id\": \"route\", \"label\": \"Protected route\", \"type\": \"output\"}, {\"id\": \"reject\", \"label\": \"401 Unauthorized\", \"type\": \"output\"}], \"edges\": [{\"id\": \"e1\", \"source\": \"client\", \"target\": \"auth\", \"label\": \"Bearer token\"}, {\"id\": \"e2\", \"source\": \"auth\", \"target\": \"verify\"}, {\"id\": \"e3\", \"source\": \"verify\", \"target\": \"ctx\", \"label\": \"valid\"}, {\"id\": \"e4\", \"source\": \"ctx\", \"target\": \"route\"}, {\"id\": \"e5\", \"source\": \"verify\", \"target\": \"reject\", \"label\": \"invalid\", \"animated\": true}]}}"
                }
              },
              {
                "type": "tool",
                "kind": 0,
                "toolCallId": "call-9c",
                "toolName": "visualize",
                "arguments": {
                  "type": "sequence",
                  "title": "Token refresh"
                },
                "state": 2,
                "metadata": {
                  "output": "{\"$type\": \"visual/sequence\", \"title\": \"Token refresh\", \"content\": \"sequenceDiagram\\n  Client->>API: request with expired token\\n  API-->>Client: 401 token expired\\n  Client->>Auth: POST /refresh\\n  Auth-->>Client: new access token\\n  Client->>API: retry request\"}"
                }
              }
            ],
            timestamp: new Date(now - 515000).toISOString(),
            textContent: "Here is how a request moves through the new middleware, and how token refresh will work.",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 10: Assistant summary
          {
            id: "msg-10",
            role: "assistant",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "I've successfully implemented JWT validation middleware with the following components:\n\n1. **Created `src/middleware/auth.ts`** with:\n   - `authenticate()` function that extracts and validates JWT tokens\n   - Bearer token parsing from Authorization header\n   - Token signature verification using `jsonwebtoken`\n   - Claims extraction into `req.user` context\n   - Proper error handling for missing/invalid tokens\n\n2. **Updated `src/middleware/index.ts`** to export the new middleware\n\n3. **Security review completed** with recommendations for:\n   - Rate limiting for failed auth attempts\n   - Logging for authentication failures\n   - JWT algorithm validation\n\nThe middleware is ready to use. You can apply it to protected routes like:\n```typescript\napp.get('/api/protected', authenticate, (req, res) => {\n  // req.user is now available\n});\n```",
              },
            ],
            timestamp: new Date(now - 510000).toISOString(),
            textContent: "I've successfully implemented JWT validation middleware with the following components:\n\n1. **Created `src/middleware/auth.ts`** with:\n   - `authenticate()` function that extracts and validates JWT tokens\n   - Bearer token parsing from Authorization header\n   - Token signature verification using `jsonwebtoken`\n   - Claims extraction into `req.user` context\n   - Proper error handling for missing/invalid tokens\n\n2. **Updated `src/middleware/index.ts`** to export the new middleware\n\n3. **Security review completed** with recommendations for:\n   - Rate limiting for failed auth attempts\n   - Logging for authentication failures\n   - JWT algorithm validation\n\nThe middleware is ready to use. You can apply it to protected routes like:\n```typescript\napp.get('/api/protected', authenticate, (req, res) => {\n  // req.user is now available\n});\n```",
            agent: "shuttle",
            modelId: "claude-sonnet-4-5",
          },
          // Message 11: User follow-up
          {
            id: "msg-11",
            role: "user",
            parts: [
              {
                type: "text",
                kind: 0,
                text: "Looks good. Can you also add token refresh support?",
              },
            ],
            timestamp: new Date(now - 500000).toISOString(),
            textContent: "Looks good. Can you also add token refresh support?",
          },
        ];
        
        return new Response(JSON.stringify({
          messages: mockMessages,
          pagination: {
            hasMore: false,
            oldestMessageId: "msg-1",
            totalCount: mockMessages.length,
          },
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/origin$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET /api/sessions/${id}/origin`);
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/delegations$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET /api/sessions/${id}/delegations`);
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/files\/browse$/,
      handler: (url) => {
        const path = url.searchParams.get("path") ?? "";
        console.log(`[mock-api] GET files/browse ${path || "/"}`);
        const prefix = path ? `${path}/` : "";
        const names = new Map<string, boolean>();
        for (const file of Object.keys(MOCK_FILES)) {
          if (!file.startsWith(prefix)) continue;
          const [name, ...rest] = file.slice(prefix.length).split("/");
          names.set(name, names.get(name) || rest.length > 0);
        }
        const entries = [...names.entries()]
          .sort(([a, aDir], [b, bDir]) => Number(bDir) - Number(aDir) || a.localeCompare(b))
          .map(([name, isDirectory]) => ({ name, relativePath: `${prefix}${name}`, isDirectory }));
        return new Response(JSON.stringify({ entries, currentPath: path }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      // Composer @ references: an empty q or one ending in "/" lists that folder; anything else searches.
      pattern: /^\/api\/sessions\/([^/]+)\/find\/files$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        const query = (url.searchParams.get("q") ?? "").trim();
        console.log(`[mock-api] GET find/files q=${query}`);
        const entries = new Set<string>();
        for (const file of Object.keys(MOCK_FILES)) {
          entries.add(file);
          for (let slash = file.indexOf("/"); slash !== -1; slash = file.indexOf("/", slash + 1)) {
            entries.add(file.slice(0, slash + 1));
          }
        }
        const isFolder = (entry: string) => entry.endsWith("/");
        const byFoldersThenName = (a: string, b: string) => Number(isFolder(b)) - Number(isFolder(a)) || a.localeCompare(b);
        const files = query === "" || query.endsWith("/")
          ? [...entries].filter((entry) => entry.startsWith(query) && entry !== query && !entry.slice(query.length).replace(/\/$/, "").includes("/")).sort(byFoldersThenName)
          : [...entries].filter((entry) => entry.toLowerCase().includes(query.toLowerCase())).sort(byFoldersThenName);
        return new Response(JSON.stringify({ sessionId: id, files }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/files\/content$/,
      handler: (url) => {
        const path = url.searchParams.get("path") ?? "";
        console.log(`[mock-api] GET files/content ${path}`);
        const content = MOCK_FILES[path];
        if (content === undefined) {
          return new Response(JSON.stringify({ error: "File not found" }), {
            status: 404,
            headers: { "Content-Type": "application/json" },
          });
        }
        return new Response(JSON.stringify({ path, content, isBinary: false, isTruncated: false }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/diffs$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET /api/sessions/${id}/diffs`);
        return new Response(JSON.stringify({ diffs: MOCK_DIFFS, available: true }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/smart-links(\/all)?$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET ${url.pathname}`);
        return new Response(JSON.stringify(MOCK_SMART_LINKS[id] ?? []), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/smart-links\/refresh$/,
      handler: () => new Response(null, { status: 202 }),
    },
    {
      pattern: /^\/api\/sessions\/([^/]+)\/smart-links\/([^/]+)\/(pin|unpin|dismiss)$/,
      handler: () => new Response(null, { status: 204 }),
    },
    // ─── Priority 2: Instance/Agent/Command/Models ──────────────────────────────
    {
      pattern: /^\/api\/(instances|sessions)\/([^/]+)\/agents$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET ${url.pathname}`);
        return new Response(JSON.stringify({
          instanceId: id,
          agents: [
            {
              name: "shuttle",
              description: "Domain specialist worker — executes delegated tasks completely",
              mode: "agent",
              hidden: false,
              model: null,
            },
            {
              name: "thread",
              description: "Codebase explorer — searches and analyzes code",
              mode: "agent",
              hidden: false,
              model: null,
            },
            {
              name: "planner",
              description: "Strategic planning agent — breaks down complex tasks",
              mode: "agent",
              hidden: false,
              model: null,
            },
          ],
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/(instances|sessions)\/([^/]+)\/commands$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET ${url.pathname}`);
        return new Response(JSON.stringify({
          instanceId: id,
          commands: [
            { name: "/plan", description: "Create a structured plan for a task" },
            { name: "/review", description: "Review code changes" },
            { name: "/test", description: "Run tests" },
            { name: "/commit", description: "Create a git commit" },
          ],
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/instances\/([^/]+)\/models$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET /api/instances/${id}/models`);
        return new Response(JSON.stringify([
          {
            id: "anthropic",
            name: "Anthropic",
            models: [
              { id: "claude-sonnet-4-5", name: "Claude Sonnet 4.5" },
              { id: "claude-opus-4", name: "Claude Opus 4" },
              { id: "claude-haiku-4", name: "Claude Haiku 4" },
            ],
          },
          {
            id: "openai",
            name: "OpenAI",
            models: [
              { id: "gpt-4o", name: "GPT-4o" },
              { id: "gpt-4o-mini", name: "GPT-4o Mini" },
            ],
          },
        ]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    // ─── Priority 3: Analytics ──────────────────────────────────────────────────
    {
      pattern: /^\/api\/analytics\/summary$/,
      handler: () => {
        console.log("[mock-api] GET /api/analytics/summary");
        return new Response(JSON.stringify({
          totalTokens: 125000,
          totalCost: 1.85,
          totalEstimatedCost: 1.92,
          sessionCount: 42,
          messageCount: 318,
          topModels: [
            { name: "claude-sonnet-4-5", tokens: 85000, cost: 1.25 },
            { name: "gpt-4o", tokens: 40000, cost: 0.60 },
          ],
          topProjects: [
            { name: "Fleet Core", tokens: 65000, cost: 0.95 },
            { name: "Documentation", tokens: 35000, cost: 0.52 },
          ],
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/analytics\/daily$/,
      handler: () => {
        console.log("[mock-api] GET /api/analytics/daily");
        const today = new Date();
        const dailyData = [];
        for (let i = 6; i >= 0; i--) {
          const date = new Date(today);
          date.setDate(date.getDate() - i);
          dailyData.push({
            date: date.toISOString().split("T")[0],
            tokens: 15000 + Math.floor(Math.random() * 10000),
            cost: 0.22 + Math.random() * 0.15,
            estimatedCost: 0.24 + Math.random() * 0.16,
            sessions: 5 + Math.floor(Math.random() * 5),
            messages: 35 + Math.floor(Math.random() * 30),
          });
        }
        return new Response(JSON.stringify(dailyData), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/analytics\/sessions$/,
      handler: () => {
        console.log("[mock-api] GET /api/analytics/sessions");
        return new Response(JSON.stringify([
          {
            sessionId: "mock-session-1",
            title: "Implement mock API plugin for frontend",
            projectId: "mock-project-1",
            projectName: "Fleet Core",
            tokens: 12500,
            cost: 0.15,
            estimatedCost: 0.16,
            models: ["claude-sonnet-4-5"],
            durationSeconds: 3600,
            createdAt: new Date(Date.now() - 86400000).toISOString(),
          },
          {
            sessionId: "mock-session-2",
            title: "Fix authentication flow",
            projectId: null,
            projectName: null,
            tokens: 8300,
            cost: 0.09,
            estimatedCost: 0.10,
            models: ["gpt-4o"],
            durationSeconds: 2400,
            createdAt: new Date(Date.now() - 172800000).toISOString(),
          },
          {
            sessionId: "mock-session-3",
            title: "Update documentation for v2.0",
            projectId: "mock-project-2",
            projectName: "Documentation",
            tokens: 5200,
            cost: 0.06,
            estimatedCost: 0.07,
            models: ["claude-haiku-4"],
            durationSeconds: 1800,
            createdAt: new Date(Date.now() - 259200000).toISOString(),
          },
        ]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/analytics\/models$/,
      handler: () => {
        console.log("[mock-api] GET /api/analytics/models");
        return new Response(JSON.stringify([
          {
            modelId: "claude-sonnet-4-5",
            providerId: "anthropic",
            tokens: 85000,
            cost: 1.25,
            estimatedCost: 1.30,
            messageCount: 185,
            avgCostPerMessage: 0.0068,
          },
          {
            modelId: "gpt-4o",
            providerId: "openai",
            tokens: 40000,
            cost: 0.60,
            estimatedCost: 0.62,
            messageCount: 133,
            avgCostPerMessage: 0.0045,
          },
        ]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    // ─── Priority 4: Other commonly hit endpoints ───────────────────────────────
    {
      pattern: /^\/api\/directories$/,
      handler: () => {
        console.log("[mock-api] GET /api/directories");
        return new Response(JSON.stringify({
          entries: [
            {
              name: "weave-fleet",
              path: "C:\\source\\weave-fleet",
              isGitRepo: true,
            },
            {
              name: "my-app",
              path: "C:\\Users\\demo\\projects\\my-app",
              isGitRepo: true,
            },
          ],
          currentPath: null,
          parentPath: null,
          roots: ["C:\\source", "C:\\Users\\demo\\projects"],
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/credentials$/,
      handler: () => {
        console.log("[mock-api] GET /api/credentials");
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/integrations\/github\/auth\/status$/,
      handler: () => {
        console.log("[mock-api] GET /api/integrations/github/auth/status");
        return new Response(JSON.stringify({ authenticated: false }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/boards$/,
      handler: () => {
        console.log("[mock-api] GET /api/boards");
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/key-files$/,
      handler: () => {
        console.log("[mock-api] GET /api/key-files");
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
    {
      pattern: /^\/api\/.*$/,
      handler: (url) => {
        console.warn(`[mock-api] Unhandled API route: ${url.pathname}`);
        return new Response(JSON.stringify({ error: "Not implemented in mock" }), {
          status: 501,
          headers: { "Content-Type": "application/json" },
        });
      },
    },
  ];

  function shouldMock(): boolean {
    // Force mock mode if MOCK_API=true or --mode mock
    if (process.env.MOCK_API === "true" || options.mode === "mock") {
      return true;
    }
    
    // Default: disabled until mock data shapes match the real API contract
    return false;
  }

  return {
    name: "vite-plugin-mock-api",
    
    config(config, { command }) {
      if (!shouldMock()) {
        console.log("[mock-api] Disabled (FLEET_API_URL is set or MOCK_API=false)");
        return;
      }

      if (command === "serve") {
        // Clear VITE_API_BASE_URL so the browser makes relative requests
        // that flow through Vite's middleware (where we intercept them).
        // Without this, .env.development.local sends requests directly
        // to localhost:5001, bypassing the mock entirely.
        console.log("[mock-api] Clearing VITE_API_BASE_URL and proxy (mock mode active)");
        return {
          define: {
            "import.meta.env.VITE_API_BASE_URL": JSON.stringify(""),
          },
          server: {
            ...config.server,
            proxy: undefined,
          },
        };
      }
    },
    
    configureServer(server: ViteDevServer) {
      if (!shouldMock()) {
        return;
      }

      console.log("[mock-api] Enabled - intercepting /api/* and /healthz requests");
      devServer = server;

      void startMockPreview(resolve(__dirname, "../src/WeaveFleet.Api/Browser/preview-bridge.js"))
        .then((started) => {
          preview = started;
          console.log(`[mock-api] Mock preview app at ${started.origin}`);
        })
        .catch((error) => console.warn("[mock-api] Mock preview app didn't start:", error));
      server.httpServer?.once("close", () => preview?.close());
      
      server.middlewares.use((req, res, next) => {
        const url = new URL(req.url || "/", `http://${req.headers.host}`);
        
        // Find matching route
        for (const route of routes) {
          if (route.pattern.test(url.pathname)) {
            toMockRequest(req, url)
              .then((mockRequest) => route.handler(url, mockRequest))
              .then((response) => {
                res.statusCode = response.status;
                response.headers.forEach((value, key) => {
                  res.setHeader(key, value);
                });
                return response.text();
              })
              .then((body) => {
                res.end(body);
              })
              .catch((error) => {
                console.error("[mock-api] Handler error:", error);
                res.statusCode = 500;
                res.setHeader("Content-Type", "application/json");
                res.end(JSON.stringify({ error: "Mock handler error" }));
              });
            
            return; // Don't call next()
          }
        }
        
        next();
      });
    },
    
    configurePreviewServer(server: PreviewServer) {
      // Reuse the same middleware for preview mode
      if (!shouldMock()) {
        return;
      }

      console.log("[mock-api] Enabled in preview mode - intercepting /api/* and /healthz requests");
      
      server.middlewares.use((req, res, next) => {
        const url = new URL(req.url || "/", `http://${req.headers.host}`);
        
        // Find matching route
        for (const route of routes) {
          if (route.pattern.test(url.pathname)) {
            toMockRequest(req, url)
              .then((mockRequest) => route.handler(url, mockRequest))
              .then((response) => {
                res.statusCode = response.status;
                response.headers.forEach((value, key) => {
                  res.setHeader(key, value);
                });
                return response.text();
              })
              .then((body) => {
                res.end(body);
              })
              .catch((error) => {
                console.error("[mock-api] Handler error:", error);
                res.statusCode = 500;
                res.setHeader("Content-Type", "application/json");
                res.end(JSON.stringify({ error: "Mock handler error" }));
              });
            
            return; // Don't call next()
          }
        }
        
        next();
      });
    },
  };
}
