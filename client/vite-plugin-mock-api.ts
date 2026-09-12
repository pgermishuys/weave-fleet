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
import { readFileSync } from "fs";
import { resolve } from "path";

interface MockRoute {
  pattern: RegExp;
  handler: (url: URL, req: Request) => Response | Promise<Response>;
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
  kind: "diagram" | "sequence";
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
  ];
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
  const canvasesBySession = new Map<string, MockCanvas[]>();

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

  const routes: MockRoute[] = [
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
      handler: () => {
        console.log("[mock-api] GET /api/workspace-roots");
        return new Response(JSON.stringify({
          roots: [
            {
              id: "mock-root-1",
              path: "C:\\Users\\demo\\projects",
              source: "user",
              exists: true,
            },
          ],
        }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
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
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
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
          origin: sessionItem.origin || null,
          capabilities: {
            canPrompt: true,
            canAbort: sessionItem.activityStatus === "busy",
            canResume: sessionItem.lifecycleStatus === "stopped",
            canStop: sessionItem.lifecycleStatus === "running",
            canFork: true,
            canDelete: true,
            canRename: true,
            promptDisabledReason: null,
            abortDisabledReason: sessionItem.activityStatus !== "busy" ? "Session is not active" : null,
            resumeDisabledReason: sessionItem.lifecycleStatus !== "stopped" ? "Session is not stopped" : null,
            stopDisabledReason: sessionItem.lifecycleStatus !== "running" ? "Session is not running" : null,
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
      pattern: /^\/api\/sessions\/([^/]+)\/resume$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] POST /api/sessions/${id}/resume`);
        return new Response(JSON.stringify({
          instanceId: `mock-instance-${Date.now()}`,
          session: {
            id,
            title: "Resumed Session",
            isHidden: false,
            time: {
              created: Date.now() - 3600000,
              updated: Date.now(),
            },
          },
        }), {
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
      pattern: /^\/api\/instances\/([^/]+)\/agents$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET /api/instances/${id}/agents`);
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
      pattern: /^\/api\/instances\/([^/]+)\/commands$/,
      handler: (url) => {
        const id = url.pathname.split("/")[3];
        console.log(`[mock-api] GET /api/instances/${id}/commands`);
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
      
      server.middlewares.use((req, res, next) => {
        const url = new URL(req.url || "/", `http://${req.headers.host}`);
        
        // Find matching route
        for (const route of routes) {
          if (route.pattern.test(url.pathname)) {
            const mockRequest = new Request(url.toString(), {
              method: req.method,
              headers: req.headers as HeadersInit,
            });
            
            Promise.resolve(route.handler(url, mockRequest))
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
            const mockRequest = new Request(url.toString(), {
              method: req.method,
              headers: req.headers as HeadersInit,
            });
            
            Promise.resolve(route.handler(url, mockRequest))
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
