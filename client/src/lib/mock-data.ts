import {
  Session,
  SessionEvent,
  Pipeline,
  TaskTemplate,
  QueueItem,
  FleetSummary,
} from "./types";

// === Sessions ===

export const mockSessions: Session[] = [
  {
    id: "sess-001",
    workspaceId: "ws-001",
    name: "proj-auth",
    status: "active",
    currentAgent: "tapestry",
    initialPrompt: "Implement JWT authentication module with refresh tokens",
    source: { type: "pipeline", pipelineId: "pipe-001", stageIndex: 1 },
    tokens: { input: 24200, output: 9800, reasoning: 4200, cache: 12400 },
    cost: 1.2,
    contextUsage: 0.34,
    planRef: ".weave/plans/auth-module.md",
    planProgress: { done: 4, total: 7 },
    pipelineStageId: "stage-002",
    tags: ["auth", "backend"],
    modifiedFiles: [
      { path: "src/auth/auth.model.ts", type: "added" },
      { path: "src/auth/auth.service.ts", type: "added" },
      { path: "src/auth/auth.middleware.ts", type: "added" },
      { path: "prisma/schema.prisma", type: "modified" },
    ],
    createdAt: new Date("2026-02-27T10:30:00"),
  },
  {
    id: "sess-002",
    workspaceId: "ws-002",
    name: "proj-payments",
    status: "active",
    currentAgent: "loom",
    initialPrompt: "Analyze the payment processing flow and identify bottlenecks",
    source: { type: "manual" },
    tokens: { input: 8400, output: 3600, reasoning: 1800, cache: 4200 },
    cost: 0.45,
    contextUsage: 0.12,
    tags: ["payments", "analysis"],
    modifiedFiles: [],
    createdAt: new Date("2026-02-27T10:42:00"),
  },
  {
    id: "sess-003",
    workspaceId: "ws-003",
    name: "proj-api",
    status: "active",
    currentAgent: "shuttle",
    initialPrompt: "Build REST API endpoints for user management",
    source: { type: "template", templateId: "tpl-001" },
    tokens: { input: 5600, output: 2400, reasoning: 800, cache: 2800 },
    cost: 0.3,
    contextUsage: 0.08,
    planRef: ".weave/plans/api-endpoints.md",
    planProgress: { done: 1, total: 5 },
    tags: ["api", "backend"],
    modifiedFiles: [
      { path: "src/api/users.controller.ts", type: "added" },
    ],
    createdAt: new Date("2026-02-27T10:45:00"),
  },
  {
    id: "sess-004",
    workspaceId: "ws-004",
    name: "proj-docs",
    status: "idle",
    currentAgent: "loom",
    initialPrompt: "Generate API documentation from the codebase",
    source: { type: "batch", batchId: "batch-001" },
    tokens: { input: 38000, output: 14000, reasoning: 6000, cache: 18000 },
    cost: 1.85,
    contextUsage: 0.52,
    tags: ["docs"],
    modifiedFiles: [
      { path: "docs/api-reference.md", type: "added" },
      { path: "docs/getting-started.md", type: "added" },
    ],
    createdAt: new Date("2026-02-27T09:15:00"),
  },
  {
    id: "sess-005",
    workspaceId: "ws-005",
    name: "proj-tests",
    status: "active",
    currentAgent: "tapestry",
    initialPrompt: "Write integration tests for the payment module",
    source: { type: "github", issueUrl: "https://github.com/org/repo/issues/42", issueNumber: 42 },
    tokens: { input: 12800, output: 5200, reasoning: 2600, cache: 6400 },
    cost: 0.68,
    contextUsage: 0.18,
    planRef: ".weave/plans/payment-tests.md",
    planProgress: { done: 2, total: 4 },
    tags: ["tests", "payments"],
    modifiedFiles: [
      { path: "tests/payment.integration.test.ts", type: "added" },
      { path: "tests/fixtures/payment.fixture.ts", type: "added" },
    ],
    createdAt: new Date("2026-02-27T10:20:00"),
  },
  {
    id: "sess-006",
    workspaceId: "ws-006",
    name: "proj-refactor",
    status: "completed",
    currentAgent: "loom",
    initialPrompt: "Refactor the database connection pool to use singleton pattern",
    source: { type: "manual" },
    tokens: { input: 42000, output: 18000, reasoning: 8000, cache: 21000 },
    cost: 2.15,
    contextUsage: 0.0,
    tags: ["refactor", "database"],
    modifiedFiles: [
      { path: "src/db/connection.ts", type: "modified" },
      { path: "src/db/pool.ts", type: "modified" },
      { path: "src/db/index.ts", type: "modified" },
    ],
    createdAt: new Date("2026-02-27T08:00:00"),
    completedAt: new Date("2026-02-27T08:45:00"),
  },
  {
    id: "sess-007",
    workspaceId: "ws-007",
    name: "proj-ci",
    status: "error",
    currentAgent: "tapestry",
    initialPrompt: "Set up GitHub Actions CI/CD pipeline",
    source: { type: "manual" },
    tokens: { input: 15000, output: 6000, reasoning: 3000, cache: 7500 },
    cost: 0.78,
    contextUsage: 0.21,
    tags: ["ci", "devops"],
    modifiedFiles: [
      { path: ".github/workflows/ci.yml", type: "added" },
    ],
    createdAt: new Date("2026-02-27T09:30:00"),
  },
  {
    id: "sess-008",
    workspaceId: "ws-008",
    name: "proj-frontend",
    status: "waiting_input",
    currentAgent: "loom",
    initialPrompt: "Build the settings page with dark mode toggle",
    source: { type: "template", templateId: "tpl-002" },
    tokens: { input: 22000, output: 9000, reasoning: 4000, cache: 11000 },
    cost: 1.12,
    contextUsage: 0.31,
    planRef: ".weave/plans/settings-page.md",
    planProgress: { done: 3, total: 6 },
    tags: ["frontend", "ui"],
    modifiedFiles: [
      { path: "src/pages/settings.tsx", type: "added" },
      { path: "src/components/theme-toggle.tsx", type: "added" },
      { path: "src/styles/theme.css", type: "added" },
    ],
    createdAt: new Date("2026-02-27T10:00:00"),
  },
];

// === Session Events (for session detail view) ===

export const mockSessionEvents: SessionEvent[] = [
  {
    id: "evt-001",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:00"),
    type: "message",
    agent: "loom",
    data: {
      role: "user",
      text: "Implement JWT authentication module with refresh tokens",
    },
  },
  {
    id: "evt-002",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:05"),
    type: "message",
    agent: "loom",
    data: {
      role: "assistant",
      text: "I'll implement a JWT auth module. Let me first explore the existing codebase to understand the patterns in use.",
    },
  },
  {
    id: "evt-003",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:08"),
    type: "delegation_start",
    agent: "loom",
    data: {
      targetAgent: "thread",
      reason: "Explore existing auth patterns and database models",
    },
  },
  {
    id: "evt-004",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:10"),
    type: "tool_call",
    agent: "thread",
    data: {
      tool: "glob",
      args: { pattern: "src/**/*.model.ts" },
      status: "completed",
      result: "Found 3 files",
      duration: 120,
    },
  },
  {
    id: "evt-005",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:12"),
    type: "tool_call",
    agent: "thread",
    data: {
      tool: "read",
      args: { path: "src/models/user.model.ts" },
      status: "completed",
      result: "45 lines",
      duration: 80,
    },
  },
  {
    id: "evt-006",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:15"),
    type: "tool_call",
    agent: "thread",
    data: {
      tool: "read",
      args: { path: "src/models/role.model.ts" },
      status: "completed",
      result: "32 lines",
      duration: 65,
    },
  },
  {
    id: "evt-007",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:18"),
    type: "tool_call",
    agent: "thread",
    data: {
      tool: "grep",
      args: { pattern: "authenticate|authorize", include: "*.ts" },
      status: "completed",
      result: "4 matches",
      duration: 200,
    },
  },
  {
    id: "evt-008",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:20"),
    type: "delegation_end",
    agent: "thread",
    data: {
      result: "Found 3 existing models with Prisma ORM. No auth module exists yet. User model has email/name fields. Uses bcrypt in a utility file.",
      tokensUsed: 3200,
      duration: 12000,
    },
  },
  {
    id: "evt-009",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:22"),
    type: "message",
    agent: "loom",
    data: {
      role: "assistant",
      text: "Thread found 3 existing models using Prisma ORM. No auth module exists yet. I'll delegate to Pattern to create an implementation plan.",
    },
  },
  {
    id: "evt-010",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:30:25"),
    type: "delegation_start",
    agent: "loom",
    data: {
      targetAgent: "pattern",
      reason: "Create implementation plan for auth module",
    },
  },
  {
    id: "evt-011",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:31:00"),
    type: "tool_call",
    agent: "pattern",
    data: {
      tool: "write",
      args: { path: ".weave/plans/auth-module.md" },
      status: "completed",
      result: "Created plan with 7 tasks",
      duration: 1500,
    },
  },
  {
    id: "evt-012",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:31:02"),
    type: "delegation_end",
    agent: "pattern",
    data: {
      result: "Created auth-module.md plan with 7 tasks",
      tokensUsed: 4800,
      duration: 37000,
    },
  },
  {
    id: "evt-013",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:31:05"),
    type: "agent_switch",
    agent: "tapestry",
    data: {
      from: "loom",
      to: "tapestry",
      reason: "/start-work auth-module",
    },
  },
  {
    id: "evt-014",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:31:10"),
    type: "plan_progress",
    agent: "tapestry",
    data: { task: "Design database schema", index: 0, done: true, total: 7 },
  },
  {
    id: "evt-015",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:32:00"),
    type: "tool_call",
    agent: "tapestry",
    data: {
      tool: "write",
      args: { path: "src/auth/auth.model.ts" },
      status: "completed",
      result: "Created auth model (45 lines)",
      duration: 2000,
    },
  },
  {
    id: "evt-016",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:32:05"),
    type: "plan_progress",
    agent: "tapestry",
    data: { task: "Create auth models", index: 1, done: true, total: 7 },
  },
  {
    id: "evt-017",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:33:00"),
    type: "tool_call",
    agent: "tapestry",
    data: {
      tool: "write",
      args: { path: "src/auth/auth.service.ts" },
      status: "completed",
      result: "Created auth service (128 lines)",
      duration: 3500,
    },
  },
  {
    id: "evt-018",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:33:10"),
    type: "plan_progress",
    agent: "tapestry",
    data: { task: "Implement auth service", index: 2, done: true, total: 7 },
  },
  {
    id: "evt-019",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:34:00"),
    type: "tool_call",
    agent: "tapestry",
    data: {
      tool: "write",
      args: { path: "src/auth/auth.middleware.ts" },
      status: "completed",
      result: "Created auth middleware (67 lines)",
      duration: 1800,
    },
  },
  {
    id: "evt-020",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:34:05"),
    type: "plan_progress",
    agent: "tapestry",
    data: { task: "Add middleware", index: 3, done: true, total: 7 },
  },
  {
    id: "evt-021",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:34:30"),
    type: "delegation_start",
    agent: "tapestry",
    data: {
      targetAgent: "weft",
      reason: "Review auth implementation before continuing",
    },
  },
  {
    id: "evt-022",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:34:45"),
    type: "delegation_end",
    agent: "weft",
    data: {
      result: "APPROVED — Clean implementation, follows existing Prisma patterns",
      tokensUsed: 2800,
      duration: 15000,
    },
  },
  {
    id: "evt-023",
    sessionId: "sess-001",
    timestamp: new Date("2026-02-27T10:35:00"),
    type: "cost_update",
    agent: "tapestry",
    data: {
      sessionCost: 1.2,
      sessionTokens: 38200,
    },
  },
];

// === Pipelines ===

export const mockPipelines: Pipeline[] = [
  {
    id: "pipe-001",
    name: "Full-Stack Auth Feature",
    description: "End-to-end authentication: schema → backend → API → tests",
    status: "running",
    stages: [
      {
        id: "stage-001",
        index: 0,
        name: "Design Schema",
        workspaceDir: "/projects/app",
        prompt: "Design the database schema for user authentication with roles and permissions",
        dependsOn: [],
        status: "completed",
        tokens: 8400,
        cost: 0.42,
      },
      {
        id: "stage-002",
        index: 1,
        name: "Auth Module",
        workspaceDir: "/projects/app",
        prompt: "Implement JWT authentication module with refresh tokens",
        dependsOn: ["stage-001"],
        contextFrom: ["stage-001"],
        sessionId: "sess-001",
        status: "running",
        tokens: 38200,
        cost: 1.2,
      },
      {
        id: "stage-003",
        index: 2,
        name: "API Endpoints",
        workspaceDir: "/projects/app",
        prompt: "Build REST endpoints for auth: login, register, refresh, logout",
        dependsOn: ["stage-002"],
        contextFrom: ["stage-002"],
        status: "pending",
      },
      {
        id: "stage-004",
        index: 3,
        name: "Integration Tests",
        workspaceDir: "/projects/app",
        prompt: "Write integration tests for all auth endpoints",
        dependsOn: ["stage-003"],
        contextFrom: ["stage-002", "stage-003"],
        status: "pending",
      },
    ],
    createdAt: new Date("2026-02-27T10:00:00"),
  },
  {
    id: "pipe-002",
    name: "Documentation Sprint",
    description: "Generate docs for all modules in parallel",
    status: "draft",
    stages: [
      {
        id: "stage-005",
        index: 0,
        name: "API Docs",
        workspaceDir: "/projects/app",
        prompt: "Generate OpenAPI spec and markdown docs for all REST endpoints",
        dependsOn: [],
        status: "pending",
      },
      {
        id: "stage-006",
        index: 1,
        name: "Architecture Docs",
        workspaceDir: "/projects/app",
        prompt: "Document the system architecture with diagrams",
        dependsOn: [],
        status: "pending",
      },
      {
        id: "stage-007",
        index: 2,
        name: "Getting Started Guide",
        workspaceDir: "/projects/app",
        prompt: "Write a getting started guide for new developers",
        dependsOn: ["stage-005", "stage-006"],
        contextFrom: ["stage-005", "stage-006"],
        status: "pending",
      },
    ],
    createdAt: new Date("2026-02-27T09:00:00"),
  },
];

// === Templates ===

export const mockTemplates: TaskTemplate[] = [
  {
    id: "tpl-001",
    name: "REST API from Schema",
    description: "Generate CRUD API endpoints from a database schema",
    prompt: "Build REST API endpoints for the {{model}} model. Include GET (list + detail), POST, PUT, DELETE. Use the existing patterns in the codebase.",
    variables: [
      { name: "model", description: "The model name to generate endpoints for", required: true },
    ],
    defaultWorkspace: "/projects/app",
    tags: ["api", "backend", "crud"],
    usageCount: 12,
  },
  {
    id: "tpl-002",
    name: "React Page with Tests",
    description: "Create a new page component with unit tests",
    prompt: "Create a {{pageName}} page at {{route}}. Include: component, tests, route registration. Follow existing patterns.",
    variables: [
      { name: "pageName", description: "Name of the page", required: true },
      { name: "route", description: "URL route path", required: true },
    ],
    tags: ["frontend", "react", "tests"],
    usageCount: 8,
  },
  {
    id: "tpl-003",
    name: "Fix GitHub Issue",
    description: "Analyze and fix a GitHub issue",
    prompt: "Analyze GitHub issue #{{issueNumber}} in {{repo}}. Understand the bug, find the root cause, implement a fix, and write a test to prevent regression.",
    variables: [
      { name: "issueNumber", description: "GitHub issue number", required: true },
      { name: "repo", description: "Repository name", required: true, defaultValue: "org/app" },
    ],
    tags: ["bugfix", "github"],
    usageCount: 24,
  },
  {
    id: "tpl-004",
    name: "Write Tests for Module",
    description: "Generate comprehensive tests for an existing module",
    prompt: "Write comprehensive tests for the {{module}} module. Include unit tests, edge cases, and integration tests. Target 90%+ coverage.",
    variables: [
      { name: "module", description: "Module path or name", required: true },
    ],
    tags: ["tests", "quality"],
    usageCount: 16,
  },
];

// === Queue ===

export const mockQueueItems: QueueItem[] = [
  {
    id: "qi-001",
    prompt: "Fix issue #42: Payment webhook timeout",
    workspaceDir: "/projects/api",
    priority: 1,
    status: "running",
    sessionId: "sess-005",
    tokens: 12800,
    createdAt: new Date("2026-02-27T10:00:00"),
    startedAt: new Date("2026-02-27T10:20:00"),
  },
  {
    id: "qi-002",
    prompt: "Add dark mode support to settings page",
    workspaceDir: "/projects/frontend",
    priority: 2,
    status: "running",
    sessionId: "sess-008",
    templateId: "tpl-002",
    tokens: 22000,
    createdAt: new Date("2026-02-27T09:45:00"),
    startedAt: new Date("2026-02-27T10:00:00"),
  },
  {
    id: "qi-003",
    prompt: "Write API documentation for user endpoints",
    workspaceDir: "/projects/api",
    priority: 3,
    status: "queued",
    createdAt: new Date("2026-02-27T10:30:00"),
  },
  {
    id: "qi-004",
    prompt: "Refactor database models to use TypeScript strict mode",
    workspaceDir: "/projects/core",
    priority: 4,
    status: "queued",
    createdAt: new Date("2026-02-27T10:32:00"),
  },
  {
    id: "qi-005",
    templateId: "tpl-003",
    prompt: "Fix issue #38: Auth token not refreshing",
    workspaceDir: "/projects/auth",
    priority: 1,
    status: "queued",
    createdAt: new Date("2026-02-27T10:35:00"),
  },
  {
    id: "qi-006",
    prompt: "Add structured logging to all services",
    workspaceDir: "/projects/core",
    priority: 5,
    status: "queued",
    createdAt: new Date("2026-02-27T10:40:00"),
  },
  {
    id: "qi-007",
    prompt: "Update all npm dependencies to latest",
    workspaceDir: "/projects/core",
    priority: 3,
    status: "completed",
    cost: 0.08,
    tokens: 3200,
    duration: 63,
    createdAt: new Date("2026-02-27T08:00:00"),
    startedAt: new Date("2026-02-27T08:05:00"),
    completedAt: new Date("2026-02-27T08:06:03"),
  },
  {
    id: "qi-008",
    prompt: "Fix issue #45: Race condition in connection pool",
    workspaceDir: "/projects/api",
    priority: 1,
    status: "completed",
    cost: 0.32,
    tokens: 14200,
    duration: 252,
    createdAt: new Date("2026-02-27T08:30:00"),
    startedAt: new Date("2026-02-27T08:35:00"),
    completedAt: new Date("2026-02-27T08:39:12"),
  },
];

// === Fleet Summary ===

export const mockFleetSummary: FleetSummary = {
  activeSessions: 4,
  idleSessions: 1,
  totalTokens: 142000,
  totalCost: 4.23,
  queuedTasks: 4,
};

// === Helpers ===

export function getSessionById(id: string): Session | undefined {
  return mockSessions.find((s) => s.id === id);
}

export function getEventsForSession(sessionId: string): SessionEvent[] {
  return mockSessionEvents.filter((e) => e.sessionId === sessionId);
}

export function formatTokens(tokens: number): string {
  if (tokens >= 1000) return `${(tokens / 1000).toFixed(1)}k`;
  return tokens.toString();
}

export function formatCost(cost: number): string {
  return `$${cost.toFixed(2)}`;
}

export function formatDuration(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

export function getStatusColor(status: string): string {
  switch (status) {
    case "active":
    case "running":
      return "text-green-500";
    case "idle":
    case "paused":
      return "text-zinc-400";
    case "waiting_input":
      return "text-amber-500";
    case "completed":
    case "drained":
      return "text-blue-500";
    case "error":
    case "failed":
      return "text-red-500";
    case "pending":
    case "queued":
      return "text-zinc-500";
    case "draft":
      return "text-zinc-400";
    default:
      return "text-zinc-500";
  }
}

export function getStatusDot(status: string): string {
  switch (status) {
    case "active":
    case "running":
      return "bg-green-500";
    case "idle":
    case "paused":
      return "bg-zinc-400";
    case "waiting_input":
      return "bg-amber-500";
    case "completed":
    case "drained":
      return "bg-blue-500";
    case "error":
    case "failed":
      return "bg-red-500";
    case "pending":
    case "queued":
      return "bg-zinc-500";
    default:
      return "bg-zinc-500";
  }
}
