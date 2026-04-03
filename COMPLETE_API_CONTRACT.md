# Complete API Contract - Weave Agent Fleet Frontend to Backend

**Generated:** 2025-04-03  
**Version:** v0.14.0  
**Framework:** Next.js 16 + React 19  
**Backend Interface:** REST API + SSE + WebSocket (Topic-Based Pub/Sub)

---

## Executive Summary

This document specifies **EXACTLY** what the Weave Agent Fleet frontend expects from a backend server. It covers:

- **48 REST API endpoints** (sessions, instances, config, repos, integrations, skills)
- **2 real-time streaming protocols**: SSE (Server-Sent Events) + WebSocket (topic-based)
- **URL configuration** via `NEXT_PUBLIC_API_BASE_URL` environment variable
- **Complete request/response shapes** with TypeScript interfaces
- **Detailed message flow** for session events, activity stream, and gap-fill reconnection

This is the **single source of truth** for building a .NET (or any) backend that implements this contract.

---

## Part 1: API Client Layer & URL Configuration

### Environment-Based URL Resolution

**File:** `src/lib/api-client.ts`

The frontend determines the backend URL via a **build-time environment variable**:

```typescript
const API_BASE = (process.env.NEXT_PUBLIC_API_BASE_URL ?? "").replace(/\/$/, "");
```

#### Mode 1: Standalone (Default)
- `NEXT_PUBLIC_API_BASE_URL` is unset or empty
- All API paths are **relative URLs**
- Example: `/api/sessions` → `/api/sessions`
- Frontend and backend share the same origin
- **Use case:** Single process (Node.js or Go binary) serving both frontend and API

#### Mode 2: Split Development
- `NEXT_PUBLIC_API_BASE_URL=http://localhost:3000` (or any backend URL)
- All API paths are **prefixed** with the base URL
- Example: `/api/sessions` → `http://localhost:3000/api/sessions`
- Enables cross-origin CORS requests
- **Use case:** Frontend on port 3001, backend on port 3000

### Helper Functions

```typescript
export function apiUrl(path: string): string
// Returns: API_BASE ? `${API_BASE}${path}` : path

export const sseUrl = apiUrl
// Identical to apiUrl; named distinctly for EventSource clarity

export function wsUrl(path: string): string
// Converts HTTP(S) base URL to WebSocket URL:
//   http://localhost:3000  → ws://localhost:3000
//   https://example.com    → wss://example.com
//   (relative path)        → derived from window.location at runtime (SSR-safe)

export function apiFetch(path: string, init?: RequestInit): Promise<Response>
// Thin fetch() wrapper: fetch(apiUrl(path), init)
```

---

## Part 2: Core REST Endpoints (48 Total)

### Sessions (10 Endpoints)

#### `POST /api/sessions` - Create Session

**Request Body:**
```typescript
{
  directory: string;                          // required: absolute path
  title?: string;                             // optional: session title
  isolationStrategy?: "existing" | "worktree" | "clone";  // default: "existing"
  branch?: string;                            // git branch for worktree/clone
  context?: ContextSource;                    // optional: integration context
  initialPrompt?: string;                     // pre-formatted prompt (overrides context)
  harnessType?: string;                       // harness type, defaults to "opencode"
  onComplete?: {
    notifySessionId: string;                  // conductor session to notify
    notifyInstanceId: string;                 // conductor instance ID
  };
}
```

**Response (200):**
```typescript
{
  instanceId: string;
  workspaceId: string;
  session: {
    id: string;
    title: string;
    time: { created: number; updated: number };
  };
}
```

**Errors:**
- 400: Invalid directory or missing required fields
- 500: SDK failure, workspace creation failed

---

#### `GET /api/sessions` - List Sessions with Pagination

**Query Parameters:**
- `limit?` (default: 100) — max results per page
- `offset?` (default: 0) — pagination offset
- `status?` (comma-separated) — filter by lifecycle status
  - Valid values: `active,idle,stopped,completed,disconnected,error,waiting_input`

**Response Headers:**
```
X-Total-Count: 350        (total sessions across all pages)
X-Limit: 100              (limit value used)
X-Offset: 0               (offset value used)
```

**Response (200):** `SessionListItem[]`

```typescript
[
  {
    instanceId: string;
    workspaceId: string;
    workspaceDirectory: string;
    workspaceDisplayName: string | null;
    isolationStrategy: string;
    sourceDirectory: string | null;         // original repo path for worktree/clone
    branch: string | null;                  // git branch for worktree/clone
    sessionStatus: "active" | "idle" | "stopped" | "completed" | "disconnected" | "error" | "waiting_input";
    session: {
      id: string;
      title: string;
      time: { created: number; updated: number };
    };
    instanceStatus: "running" | "dead";
    dbId?: string;                          // internal Fleet DB ID
    parentSessionId?: string | null;        // parent session for task delegation
    activityStatus: "busy" | "idle" | "waiting_input" | null;
    lifecycleStatus: "running" | "completed" | "stopped" | "error" | "disconnected";
    typedInstanceStatus: "running" | "stopped";
    totalTokens?: number;
    totalCost?: number;
  }
]
```

---

#### `PATCH /api/sessions/[id]` - Rename Session

**Request Body:**
```typescript
{
  title: string;                              // required: non-empty string
}
```

**Response (200):**
```typescript
{
  id: string;
  title: string;
}
```

**Errors:**
- 400: title is empty or not a string
- 404: session not found
- 500: DB update failed

---

#### `GET /api/sessions/[id]` - Get Session Detail with Messages

**Query Parameters:**
- `instanceId` (required) — instance ID for the session
- `parentSessionId?` — hint for task delegation (used if session not in DB)

**Response (200):**
```typescript
{
  session: {
    // SDKSession from OpenCode SDK
    id: string;
    title: string;
    // ... other SDK session fields
  };
  messages: {
    // SDKMessage[] from SDK
    info: { id: string; role: string; createdAt: string };
    parts: { type: string; text?: string; tool?: string; /* ... */ }[];
  }[];
  workspaceId: string | null;
  workspaceDirectory: string | null;
  isolationStrategy: string | null;
  branch: string | null;
  ancestors: {
    dbId: string;
    instanceId: string;
    harnessSessionId: string;
    title: string;
  }[];                                        // parent chain (root-first)
  dbTitle: string | null;                     // DB title (may differ from SDK title)
}
```

**Errors:**
- 400: missing instanceId query param
- 404: instance not found, or session doesn't exist
- 500: SDK call failed

---

#### `DELETE /api/sessions/[id]` - Terminate or Permanently Delete Session

**Query Parameters:**
- `instanceId` (required) — instance ID
- `cleanupWorkspace?` (default: false) — cleanup worktree/clone on non-permanent delete
- `permanent?` (default: false) — permanently delete from DB

**Behavior:**

**Non-permanent delete** (`permanent=false`):
1. Calls `session.abort()` on the SDK
2. Destroys the instance if no other sessions exist
3. Updates session status in DB to `"completed"` or `"stopped"`
4. Optionally cleans up worktree/clone

**Permanent delete** (`permanent=true`):
1. Skips abort/destroy if session already in terminal state
2. Deletes from DB (session row + related callbacks)
3. Cleans up worktree/clone if isolation strategy is not "existing"

**Response (200):**
```typescript
{
  message: "Session terminated" | "Session permanently deleted";
  sessionId: string;
  instanceId: string;
}
```

**Errors:**
- 400: missing instanceId
- 500: SDK call or DB operation failed

---

#### `POST /api/sessions/[id]/prompt` - Send Prompt (Fire-and-Forget)

**Request Body:**
```typescript
{
  instanceId: string;                         // required
  text?: string;                              // text prompt (optional if attachments)
  agent?: string;                             // optional: override agent
  model?: {
    providerID: string;
    modelID: string;
  };
  attachments?: {
    mime: string;                             // e.g., "image/png"
    filename?: string;
    data: string;                             // base64 data (NOT data URI)
  }[];
}
```

**Response (204 No Content)**

Results are streamed asynchronously via:
- SSE: `GET /api/sessions/[id]/events`
- WebSocket: topic `session:${sessionId}`

**Errors:**
- 400: missing instanceId, or no text + no attachments
- 404: instance not found
- 500: SDK call failed

---

#### `POST /api/sessions/[id]/command` - Execute Slash Command (Fire-and-Forget)

**Request Body:**
```typescript
{
  instanceId: string;                         // required
  command: string;                            // command name (required, non-empty)
  args?: string;                              // command arguments
  agent?: string;                             // optional: override agent
  model?: {
    providerID: string;
    modelID: string;
  };
}
```

**Response (200):**
```typescript
{
  success: boolean;
  sessionId: string;
}
```

**Note:** `session.command()` is blocking on the server (awaits full LLM response), so the endpoint returns immediately without awaiting. Results stream via SSE/WebSocket.

**Errors:**
- 400: missing instanceId or command
- 404: instance not found
- 500: SDK call failed

---

#### `POST /api/sessions/[id]/fork` - Fork Session (Create New in Same Workspace)

**Request Body:**
```typescript
{
  title?: string;                             // optional: title for forked session
}
```

**Response (200):**
```typescript
{
  instanceId: string;
  workspaceId: string;
  session: {
    id: string;
    title: string;
    time: { created: number; updated: number };
  };
  forkedFromSessionId: string;
}
```

**Behavior:**
- Creates a new workspace using "existing" strategy on the same directory
- Spawns/reuses OpenCode instance for that directory
- Creates a new session (not a child/parent relationship)
- Persists to DB

**Errors:**
- 404: source session not found
- 500: workspace creation or SDK call failed

---

#### `POST /api/sessions/[id]/resume` - Resume Disconnected/Stopped Session

**Request Body:** (empty)

**Response (200):**
```typescript
{
  instanceId: string;
  session: {
    id: string;
    title: string;
    time: { created: number; updated: number };
  };
}
```

**Behavior:**
- Validates session is in resumable state: `"disconnected"`, `"stopped"`, or `"completed"`
- Spawns/reuses instance for the workspace directory
- Verifies session still exists in OpenCode
- Updates DB to point to new instance

**Errors:**
- 404: session not found, or workspace doesn't exist
- 409: session is already active (not resumable)
- 500: instance spawn or DB update failed

---

#### `POST /api/sessions/[id]/abort` - Abort Running Session

**Query Parameters:**
- `instanceId` (required)

**Request Body:** (empty)

**Response (200):**
```typescript
{
  message: "Session aborted";
  sessionId: string;
  instanceId: string;
}
```

**Behavior:**
- Calls `session.abort()` to cancel stuck tool calls
- Updates DB session status to "idle" if found
- Does not destroy the instance

**Errors:**
- 400: missing instanceId
- 404: instance not found
- 500: SDK call or DB update failed

---

### Session Event Streaming (4 Endpoints)

#### `GET /api/sessions/[id]/events` - SSE Event Stream

**Query Parameters:**
- `instanceId` (required)

**Response Headers:**
```
Content-Type: text/event-stream
Cache-Control: no-cache, no-transform
Connection: keep-alive
X-Accel-Buffering: no
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, OPTIONS
Access-Control-Allow-Headers: Content-Type, Authorization
```

**Event Format:**
```
data: {"type":"message.updated","properties":{...SDK event data...}}

: keepalive
(every 15 seconds)
```

**Event Types Received:**
- `message.updated` — new or updated message
- `message.part.updated` — part property change
- `message.part.delta` — streaming text delta
- `session.status` — session status change
- `session.idle` — session entered idle state
- (and more SDK event types)

**Errors:**
- 400: missing instanceId
- 404: instance not found or dead

---

#### `GET /api/sessions/[id]/messages` - Fetch Message Batch

**Query Parameters:**
- `instanceId` (required)
- `limit?` (default: 50, max: 200) — messages per batch
- `before?` — cursor: fetch messages before this ID (inclusive for older messages)
- `after?` — cursor: fetch messages after this ID (for gap-fill on reconnect)

**Response (200):**
```typescript
{
  messages: {
    info: { id: string; role: string; createdAt: string };
    parts: { type: string; text?: string; /* ... */ }[];
  }[];
  pagination: {
    hasMore: boolean;
    oldestMessageId: string | null;
    totalCount: number;
  };
}
```

**Pagination Logic:**

- **`before=<id>`** (standard pagination backwards in time):
  - Returns messages before the cursor (older messages)
  - Cursor is exclusive
  - Result includes most recent `limit` messages before cursor

- **`after=<id>`** (gap-fill on reconnect):
  - Returns messages after the cursor (newer messages)
  - If cursor is stale/not found, returns ALL messages (client reconciles)
  - No limit applied to ensure no messages are missed

**Errors:**
- 400: missing instanceId
- 404: instance not found
- 500: SDK call failed

---

#### `GET /api/sessions/[id]/diffs` - Get File Diffs

**Query Parameters:**
- `instanceId` (required)

**Response (200):** `FileDiffItem[]`
```typescript
[
  {
    file: string;                             // file path
    before: string;                           // original content
    after: string;                            // modified content
    additions: number;                        // lines added
    deletions: number;                        // lines deleted
    status: "added" | "deleted" | "modified";
  }
]
```

**Errors:**
- 400: missing instanceId
- 404: instance not found
- 500: SDK call failed

---

#### `GET /api/sessions/[id]/status` - Get Live Session Status

**Query Parameters:**
- `instanceId` (required)

**Response (200):**
```typescript
{
  status: "idle" | "busy";
}
```

**Behavior:**
- Calls `session.status()` to get live state
- Maps SDK status values: "busy" or "retry" → "busy"; absent → "idle"

**Errors:**
- 400: missing instanceId
- 404: instance not found
- 500: SDK call failed

---

### Instance Endpoints (4)

#### `GET /api/instances/[id]/agents` - List Available Agents

**Response (200):**
```typescript
[
  {
    name: string;
    description?: string;
    mode: string;
    color?: string;
    model?: { modelID: string; providerID: string };
    hidden?: boolean;
  }
]
```

**Errors:**
- 400: missing instanceId
- 404: instance not found
- 500: SDK call failed

---

#### `GET /api/instances/[id]/commands` - List Available Slash Commands

**Response (200):**
```typescript
[
  {
    name: string;
    description?: string;
  }
]
```

**Errors:**
- 400: missing instanceId
- 404: instance not found
- 500: SDK call failed

---

#### `GET /api/instances/[id]/models` - List Connected Provider Models

**Response (200):**
```typescript
[
  {
    id: string;                               // provider ID (e.g., "anthropic")
    name: string;                             // provider name
    models: {
      id: string;                             // model ID (e.g., "claude-sonnet-4-5")
      name: string;                           // model name
    }[];
  }
]
```

**Note:** Only returns providers that are currently connected.

**Errors:**
- 400: missing instanceId
- 404: instance not found
- 500: SDK call failed

---

#### `GET /api/instances/[id]/find/files` - Fuzzy Search Files

**Query Parameters:**
- `query` (required, max 256 chars)

**Response (200):** `string[]` (file paths)

```typescript
[
  "/path/to/src/index.ts",
  "/path/to/src/utils.ts",
  // ... max 20 results
]
```

**Errors:**
- 400: missing or empty query, or query > 256 chars
- 404: instance not found
- 500: SDK call failed

---

### Configuration & Metadata (5)

#### `GET /api/config` - Get User Configuration

**Response (200):**
```typescript
{
  userConfig: {
    agents: {
      [agentName: string]: {
        // agent-specific config
        [key: string]: unknown;
      };
    };
  };
  installedSkills: {
    name: string;
    description: string;
    agents: string[];
  }[];
  paths: {
    configDir: string;
    // ... other paths
  };
  connectedProviders: {
    id: string;                               // provider ID
    name: string;                             // provider name
    connected: boolean;
    authType: string | null;
    models: {
      id: string;
      name: string;
    }[];
  }[];
}
```

**Errors:**
- 500: config read failed

---

#### `PUT /api/config` - Update User Configuration

**Request Body:**
```typescript
{
  agents?: {
    [agentName: string]: {
      [key: string]: unknown;
    };
  };
}
```

**Response (200):** `{ ok: true }`

**Errors:**
- 400: invalid request body
- 500: config write failed

---

#### `GET /api/version` - Check Version

**Query Parameters:**
- `channel?` (default: "stable") — "stable" or "dev"

**Response (200):**
```typescript
{
  version: string;                            // e.g., "0.14.0"
  latest: string;                             // latest available version
  updateAvailable: boolean;
  checkedAt: string | null;                   // ISO timestamp
  channel: "stable" | "dev";
}
```

**Errors:**
- 500: version check failed

---

#### `GET /api/profile` - Get Active Profile

**Response (200):**
```typescript
{
  name: string;                               // profile name
  isDefault: boolean;
}
```

**Note:** This endpoint is **dynamic** at runtime. The `NEXT_PUBLIC_WEAVE_PROFILE` environment variable is only a build-time fallback. Always call this endpoint to get the authoritative profile.

**Errors:**
- 500: profile lookup failed

---

#### `GET /api/fleet/summary` - Fleet Aggregates

**Response (200):**
```typescript
{
  activeSessions: number;
  idleSessions: number;
  totalTokens: number;
  totalCost: number;
  queuedTasks: number;                        // always 0 in v2
}
```

**Errors:**
- 500: DB query failed

---

### Directories & Repositories (6)

#### `GET /api/directories` - Browse Directories

**Query Parameters:**
- `path?` (optional) — absolute path to list (omit for workspace roots)
- `search?` (optional) — case-insensitive substring filter

**Response (200):**
```typescript
{
  entries: {
    name: string;                             // directory name
    path: string;                             // absolute path
    isGitRepo: boolean;
  }[];
  currentPath: string;                        // resolved absolute path
  parentPath: string | null;                  // parent directory (null if at root)
  roots: string[];                            // allowed workspace roots
}
```

**Max Entries:** 100 (to prevent huge payloads)

**Security:**
- Validates paths against allowed workspace roots
- Prevents symlink escapes (resolves symlinks and re-validates)
- Filters noise directories (.git, node_modules, .next, __pycache__, etc.)
- Filters hidden directories (starting with .)

**Errors:**
- 400: path is outside allowed roots
- 403: permission denied
- 404: directory not found
- 500: listing failed

---

#### `GET /api/repositories` - Scan Git Repositories

**Response (200):**
```typescript
{
  repositories: {
    name: string;                             // directory name
    path: string;                             // absolute path
    parentRoot: string;                       // workspace root containing it
  }[];
  scannedAt: number;                          // unix timestamp (milliseconds)
}
```

**Behavior:**
- Scans all workspace roots for git repositories (cached)
- Returns all `.git` subdirectories

**Errors:**
- 500: scan failed

---

#### `GET /api/repositories/info` - Get Repository Info

**Query Parameters:**
- `path` (required, must be absolute)

**Response (200):**
```typescript
{
  repository: {
    name: string;
    path: string;
    branch: string | null;
    lastCommit: {
      hash: string;
      message: string;
      author: string;
      date: string;                          // ISO timestamp
    } | null;
    remotes: {
      name: string;                          // e.g., "origin"
      url: string;
    }[];
  };
}
```

**Errors:**
- 400: path is not absolute or outside allowed roots
- 404: path doesn't exist or is not a git repository
- 500: git operation failed

---

#### `GET /api/repositories/detail` - Get Detailed Repository Info

**Query Parameters:**
- `path` (required, must be absolute)

**Response (200):**
```typescript
{
  repository: {
    name: string;
    path: string;
    branch: string | null;
    uncommittedCount: number;                 // from git status
    totalCommitCount: number;
    firstCommitDate: string | null;           // ISO
    lastCommitDate: string | null;            // ISO
    branches: {
      name: string;
      shortHash: string;
      message: string;
      author: string;
      authorEmail: string;
      date: string;                           // ISO
      isCurrent: boolean;
      isRemote: boolean;
    }[];
    tags: {
      name: string;
      shortHash: string;
      date: string;                           // ISO
      tagger: string;
      taggerEmail: string;
    }[];
    recentCommits: {
      hash: string;
      shortHash: string;
      message: string;
      author: string;
      authorEmail: string;
      date: string;                           // ISO
    }[];                                      // last 10 commits
    remotes: {
      name: string;
      url: string;
      github: {
        owner: string;
        repo: string;
        repoUrl: string;
        issuesUrl: string;
        pullsUrl: string;
      } | null;
    }[];
    readmeContent: string | null;
    readmeFilename: string | null;
  };
}
```

**Errors:**
- 400: path is not absolute or outside allowed roots
- 404: path doesn't exist or is not a git repository
- 500: git operation failed

---

#### `GET /api/repositories/refresh` - Refresh Repository Scan

**Response (200):** Same as `GET /api/repositories`

**Behavior:**
- Invalidates cache and re-scans workspace roots

---

#### `POST /api/open-directory` - Open Directory in File Explorer

**Request Body:**
```typescript
{
  path: string;                               // absolute path
}
```

**Response (200):** `{ success: boolean }`

**Platform-Specific:**
- macOS: `open <path>`
- Linux: `xdg-open <path>`
- Windows: `explorer <path>`

**Errors:**
- 400: invalid path
- 500: command execution failed

---

### Global Activity Stream (SSE)

#### `GET /api/activity-stream` - Global Activity Stream

**Response Headers:**
```
Content-Type: text/event-stream
Cache-Control: no-cache, no-transform
Connection: keep-alive
X-Accel-Buffering: no
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, OPTIONS
Access-Control-Allow-Headers: Content-Type, Authorization
```

**Event Format:**
```
data: {"type":"activity_status","payload":{...}}

: keepalive
(every 15 seconds)
```

**Event Types:**
- `activity_status` — session busy/idle state change
- `token_update` — token usage aggregation

---

### Integrations

#### `GET /api/integrations` - List Connected Integrations

**Response (200):**
```typescript
{
  integrations: {
    id: string;                               // "github", etc.
    name: string;                             // "GitHub", etc.
    status: "connected" | "disconnected";
    connectedAt?: string;                     // ISO timestamp
  }[];
}
```

**Errors:**
- 500: list failed

---

#### `POST /api/integrations` - Connect Integration

**Request Body:**
```typescript
{
  id: string;                                 // integration ID
  config: Record<string, unknown>;            // integration-specific config
}
```

**Response (200):** `{ success: true }`

**Errors:**
- 400: invalid id or config
- 500: save failed

---

#### `DELETE /api/integrations` - Disconnect Integration

**Query Parameters:**
- `id` (required) — integration ID

**Response (200):** `{ success: true }`

**Errors:**
- 400: missing id
- 500: removal failed

---

### GitHub OAuth (RFC 8628 Device Authorization Flow)

#### `POST /api/integrations/github/auth/device-code` - Initiate Device Authorization

**Request Body:** (empty)

**Response (200):**
```typescript
{
  userCode: string;                           // e.g., "WDJB-MJHT"
  verificationUri: string;                    // https://github.com/login/device
  deviceCode: string;                         // opaque code for polling
  expiresIn: number;                          // typically 900 seconds
  interval: number;                           // polling interval in seconds
}
```

**Behavior:**
- Calls GitHub device code endpoint with `client_id` and scopes
- Returns user-facing code and verification URL
- The `deviceCode` is forwarded to client for polling (safe: useless without server-side `client_id`)

**Errors:**
- 502: GitHub API error

---

#### `POST /api/integrations/github/auth/poll` - Poll for Authorization

**Request Body:**
```typescript
{
  deviceCode: string;                         // from device-code response
}
```

**Pending Response (200):**
```typescript
{
  status: "pending";
  interval?: number;                          // updated interval if slow_down
}
```

**Complete Response (200):**
```typescript
{
  status: "complete";
}
```

**Error Response (200):**
```typescript
{
  status: "expired" | "denied" | "error";
  message?: string;
}
```

**Behavior:**
- Calls GitHub token endpoint with `device_code`
- Stores access token on `status: "complete"`
- Implements RFC 8628 §3.5 error states:
  - `authorization_pending` → `status: "pending"`
  - `slow_down` → `status: "pending", interval: <updated>`
  - `expired_token` → `status: "expired"`
  - `access_denied` → `status: "denied"`

**Errors:**
- 400: invalid deviceCode
- 502: GitHub API error

---

### GitHub API Proxy Endpoints (15)

All GitHub endpoints require authentication and proxy GitHub API v3 calls.

#### `GET /api/integrations/github/repos` - List User Repositories

**Query Parameters:**
- `page?` (default: "1")
- `per_page?` (default: "30")
- `sort?` (default: "updated")
- `direction?` (optional: "asc" or "desc")

**Response:** GitHub repo objects

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/issues` - List Issues

**Query Parameters:**
- `state?` (default: "open")
- `page?` (default: "1")
- `per_page?` (default: "30")
- `sort?` (default: "updated")
- `direction?` (optional)
- `labels?` (optional)
- `milestone?` (optional)
- `assignee?` (optional)
- `creator?` (optional, maps to "author" in filter state)
- `type?` (optional)

**Response:** GitHub issue objects

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/issues/[number]` - Get Issue

**Response:** Single GitHub issue object

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/issues/[number]/comments` - Get Issue Comments

**Response:** GitHub comment objects

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/issues/search` - Search Issues

**Query Parameters:**
- `q` (required) — search query

**Response:** GitHub issue objects

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/pulls` - List Pull Requests

**Query Parameters:** Same as issues

**Response:** GitHub pull request objects

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/pulls/[number]` - Get Pull Request

**Response:** Single GitHub PR object

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/pulls/[number]/comments` - Get PR Comments

**Response:** GitHub comment objects

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/pulls/[number]/status` - Get PR Status

**Response:**
```typescript
{
  status: string;
  state: string;
}
```

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/labels` - List Labels

**Response:** GitHub label objects

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/milestones` - List Milestones

**Response:** GitHub milestone objects

---

#### `GET /api/integrations/github/repos/[owner]/[repo]/assignees` - List Assignees

**Response:** GitHub user objects

---

#### `GET /api/integrations/github/bookmarks` - List Bookmarks

**Response:** GitHub bookmark objects

---

### Skills

#### `GET /api/skills` - List Installed Skills

**Response (200):**
```typescript
{
  skills: {
    name: string;
    description: string;
    agents: string[];
  }[];
}
```

**Errors:**
- 500: list failed

---

#### `POST /api/skills` - Install Skill

**Request Body:**
```typescript
{
  url?: string;                               // fetch skill from URL
  content?: string;                           // or provide raw content
  agents?: string[];                          // agents to assign
}
```

**Response (201):**
```typescript
{
  ok: true;
  skill: {
    name: string;
    description: string;
    agents: string[];
  };
}
```

**Errors:**
- 400: neither url nor content provided
- 500: installation failed

---

### Workspace Roots

#### `GET /api/workspace-roots` - List Workspace Roots

**Response (200):**
```typescript
{
  roots: {
    id: string | null;                        // null for env-var roots
    path: string;
    source: "env" | "user";
    exists: boolean;
  }[];
}
```

---

#### `POST /api/workspace-roots` - Add Workspace Root

**Request Body:**
```typescript
{
  path: string;
}
```

**Response (201):**
```typescript
{
  id: string;
  path: string;
}
```

**Errors:**
- 400: invalid path
- 500: save failed

---

#### `DELETE /api/workspace-roots/[id]` - Remove Workspace Root

**Response (200):** `{ success: boolean }`

**Errors:**
- 404: root not found
- 500: deletion failed

---

## Part 3: Real-Time Streaming - WebSocket (Primary)

### Endpoint

```
WS /ws
WSS /ws (if HTTPS)
```

### URL Derivation

From `NEXT_PUBLIC_API_BASE_URL`:

| Base URL | → | WebSocket URL |
|----------|---|---------------|
| `http://localhost:3000` | → | `ws://localhost:3000/ws` |
| `https://example.com` | → | `wss://example.com/ws` |
| `(relative)` | → | `ws://window.location.host/ws` (derived at runtime) |

### Protocol (Topic-Based Pub/Sub)

#### Client → Server Messages

**Subscribe:**
```json
{
  "type": "subscribe",
  "topics": ["session:abc123", "activity"]
}
```

**Unsubscribe:**
```json
{
  "type": "unsubscribe",
  "topics": ["session:abc123"]
}
```

#### Server → Client Messages

**Event:**
```json
{
  "type": "event",
  "topic": "session:abc123",
  "data": { "type": "message.updated", "properties": { ... } }
}
```

**Subscription Confirmation:**
```json
{
  "type": "subscribed",
  "topics": ["session:abc123", "activity"]
}
```

### Topics

#### Session-Specific: `session:<sessionId>`

Receives all events for that session.

**Event Types:** (from OpenCode SDK)
- `message.updated`
- `message.part.updated`
- `message.part.delta`
- `session.status`
- `session.idle`
- (and more SDK events)

**Example Payload:**
```json
{
  "type": "event",
  "topic": "session:abc123",
  "data": {
    "type": "message.updated",
    "properties": {
      "info": { "id": "msg_xyz", "role": "assistant" },
      "parts": [ ... ]
    }
  }
}
```

#### Global: `activity`

Receives global activity events.

**Event Types:**
- `activity_status` — session busy/idle transition
- `token_update` — token usage aggregation

**Example Payload:**
```json
{
  "type": "event",
  "topic": "activity",
  "data": {
    "type": "activity_status",
    "payload": {
      "sessionId": "...",
      "status": "busy"
    }
  }
}
```

### Reconnection Behavior

**Backoff Strategy:**
1. Initial delay: 1 second
2. Exponential backoff: delay × 2 each retry
3. Max delay: 30 seconds
4. Jitter: + random(0, 500ms)

**On Reconnect:**
1. Client automatically re-subscribes to all topics
2. Fires reconnect callbacks (used by `useSessionEvents` for gap-fill)
3. `useSessionEvents` gap-fills: `GET /api/sessions/[id]/messages?after=<lastMessageId>`

**Lifecycle:**

```
1. First hook mount (useWeaveSocket)
   → increments subscriber ref-count
   → if count == 1, calls connect()
   
2. connect()
   → new WebSocket(wsUrl("/ws"))
   → onopen: resubscribe to all topics, fire reconnect callbacks
   → onmessage: dispatch events to topic listeners
   → onclose: schedule reconnect if subscribers > 0
   
3. Hook unmount
   → decrements subscriber ref-count
   → if count == 0, disconnect()
```

---

## Part 4: React Hooks for Streaming

### `useSessionEvents` - Session Event Subscription

**File:** `src/hooks/use-session-events.ts`

Core hook for real-time session updates via WebSocket.

```typescript
const {
  messages: AccumulatedMessage[];              // accumulated messages
  status: "connecting" | "connected" | "recovering" | "disconnected" | "error" | "abandoned";
  sessionStatus: "idle" | "busy";              // session activity status
  error?: string;
  forceIdle: () => void;                       // imperatively transition to idle
  reconnect: () => void;                       // manual reconnect trigger
  reconnectAttempt: number;                    // number of reconnection attempts
  hasMoreMessages: boolean;
  isLoadingOlder: boolean;
  loadOlderMessages: () => Promise<void>;
  totalMessageCount: number | null;
  loadOlderError: string | null;
  cacheHit: boolean;                           // hydrated from sessionCache?
  initialScrollPosition: { scrollTop: number; scrollHeight: number } | null;
  scrollPositionRef: React.MutableRefObject;   // ref to save scroll position
} = useSessionEvents(
  sessionId: string,
  instanceId: string,
  onAgentSwitch?: (agent: string) => void,
  suppressAutoScrollRef?: React.MutableRefObject<boolean>
);
```

**Features:**

1. **Auto-Load on Mount:**
   - Checks `sessionCache.get(sessionId, instanceId)` for cached messages
   - If cache hit: hydrates messages, then gap-fills with `loadMessagesSince()`
   - If cache miss: loads initial paginated batch with `paginationLoadInitial()`

2. **WebSocket Subscription:**
   - Subscribes to topic `session:${sessionId}`
   - Receives and applies SSE events in real-time
   - Updates messages, status, and session status

3. **Gap-Fill on Reconnect:**
   - `onReconnect()` callback fires when WebSocket reconnects
   - Calls `loadMessagesSince(lastMessageIdRef.current)` to fill gaps
   - Preserves scroll position via `initialScrollPosition`

4. **Caching:**
   - On unmount: saves messages, scroll position, status, pagination state to `sessionCache`
   - Prevents scroll jumps and UI flicker on remount
   - Cache expires on full message reload (gap-fill failure)

5. **Event Handling:**
   - Pure function `handleEvent()` processes SSE events
   - Handles: `server.connected`, `error`, `session.status`, `message.updated`, `message.part.updated`, `message.part.delta`
   - Special handling for tool types: `plan_enter` → agent="plan", `plan_exit` → agent="build"

6. **Pagination:**
   - `loadOlderMessages()` fetches older batches
   - Supports `before=<messageId>` cursor pagination
   - Prevents duplicate messages by checking messageId set

**Message Limits:**
- MAX_MESSAGES = 500 (evicts oldest messages when exceeded)

---

### `useActivityStream` - Global Activity Stream

**File:** `src/hooks/use-activity-stream.ts`

Shared global activity stream via WebSocket (drop-in replacement for SSE).

```typescript
const stream = useActivityStream();

stream.on("activity_status", (payload) => {
  // { sessionId, status: "busy" | "idle" }
});

stream.off("activity_status", handler);
```

**Features:**
- Subscribes to topic `activity`
- Maintains module-level event-type listener registry
- Ref-counted via `useWeaveSocket` (first hook mount → connect, last unmount → disconnect)
- Stable API object (same reference across renders)

---

### `useWeaveSocket` - Low-Level WebSocket Management

**File:** `src/hooks/use-weave-socket.ts`

Singleton WebSocket with topic-based subscriptions.

```typescript
const { subscribe } = useWeaveSocket();

useEffect(() => {
  return subscribe(
    ["session:abc123", "activity"],
    (topic: string, data: unknown) => {
      // Handle event
    }
  );
}, [subscribe]);

// Helper: register callback for WebSocket reconnect
const unsubReconnect = onReconnect(() => {
  // Gap-fill logic here
});
```

**Features:**
- Singleton pattern: one WebSocket per browser tab
- Ref-counted lifecycle: first subscriber → connect, last unsubscriber → disconnect
- Module-level topic listener registry: survives React re-renders
- Automatic reconnection with exponential backoff
- `onReconnect()` callbacks fire on successful reconnection

**Lifecycle:**
```typescript
incrementSubscribers()     // on useWeaveSocket mount
  → if (count === 1) connect()

connect()
  → new WebSocket(wsUrl("/ws"))
  → resubscribeAll() on open
  → fire onReconnect callbacks
  → dispatch events to topic listeners

decrementSubscribers()     // on useWeaveSocket unmount
  → if (count === 0) disconnect()
```

---

## Part 5: Message Types & Data Structures

### AccumulatedMessage

```typescript
{
  messageId: string;
  sessionId: string;
  role: "user" | "assistant";
  parts: AccumulatedPart[];
  cost?: number;                              // USD
  tokens?: {
    input: number;
    output: number;
    reasoning: number;
  };
  createdAt?: number;                         // unix timestamp
  agent?: string;
  modelID?: string;
  completedAt?: number;
  parentID?: string;
}
```

### AccumulatedPart

```typescript
type AccumulatedPart =
  | {
      partId: string;
      type: "text";
      text: string;
    }
  | {
      partId: string;
      type: "tool";
      tool: string;                           // tool name
      callId: string;
      state: unknown;                         // tool state object
    }
  | {
      partId: string;
      type: "file";
      mime: string;
      filename?: string;
      url: string;                            // data URI or full URL
    };
```

### SessionListItem

```typescript
{
  instanceId: string;
  workspaceId: string;
  workspaceDirectory: string;
  workspaceDisplayName: string | null;
  isolationStrategy: string;
  sourceDirectory: string | null;
  branch: string | null;
  sessionStatus: "active" | "idle" | "stopped" | "completed" | "disconnected" | "error" | "waiting_input";
  session: {
    id: string;
    title: string;
    time: {
      created: number;                        // unix timestamp
      updated: number;                        // unix timestamp
    };
  };
  instanceStatus: "running" | "dead";
  dbId?: string;
  parentSessionId?: string | null;
  activityStatus: "busy" | "idle" | "waiting_input" | null;
  lifecycleStatus: "running" | "completed" | "stopped" | "error" | "disconnected";
  typedInstanceStatus: "running" | "stopped";
  totalTokens?: number;
  totalCost?: number;
}
```

### FileDiffItem

```typescript
{
  file: string;
  before: string;                             // original content
  after: string;                              // modified content
  additions: number;
  deletions: number;
  status: "added" | "deleted" | "modified";
}
```

---

## Part 6: Environment Variables

### Build-Time (injected via Next.js `env`)

```bash
NEXT_PUBLIC_API_BASE_URL
  # Backend URL
  # Empty (default) → relative URLs (standalone mode)
  # Set → full URL prefix (split mode, cross-origin)
  # Example: http://localhost:3000

NEXT_PUBLIC_WEAVE_PROFILE
  # Active profile name (fallback)
  # Default: "default"
  # Note: /api/profile endpoint provides authoritative value at runtime

NEXT_PUBLIC_APP_VERSION
  # App version
  # From package.json version or APP_VERSION CI env var
  # Example: "0.14.0"

NEXT_PUBLIC_COMMIT_SHA
  # Git commit SHA
  # From: git rev-parse --short HEAD
  # Example: "a1b2c3d"
```

### Runtime

```bash
WEAVE_PROFILE
  # Active profile name
  # Used by /api/profile endpoint
  # Default: "default"
```

---

## Part 7: Error Handling

### HTTP Status Codes

| Code | Meaning | Common Examples |
|------|---------|-----------------|
| 200 | Success | GET, POST, PUT, PATCH with body |
| 204 | Success (no body) | POST /api/sessions/[id]/prompt |
| 400 | Bad request | Missing required param, validation error |
| 401 | Unauthorized | GitHub not connected |
| 404 | Not found | Session, instance, path doesn't exist |
| 409 | Conflict | Resume active session (not resumable) |
| 500 | Server error | SDK failure, DB error, crash |
| 502 | Bad gateway | GitHub API error, upstream service down |

### Error Response Format

```json
{
  "error": "Human-readable message"
}
```

---

## Part 8: CORS & Headers

### SSE Endpoints (activity-stream, session events)

```
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, OPTIONS
Access-Control-Allow-Headers: Content-Type, Authorization
Cache-Control: no-cache, no-transform
Connection: keep-alive
X-Accel-Buffering: no
```

### WebSocket

- Standard WebSocket upgrade (browser enforces same-origin policy)
- Can be proxied cross-origin via `NEXT_PUBLIC_API_BASE_URL` HTTP base URL
- No special headers needed

---

## Part 9: Key Implementation Notes for .NET Backend

### 1. Session Management

- Spawn/manage OpenCode instances per workspace
- Track `instanceId`, `workspaceId`, workspace directory
- Persist sessions in DB with parent-child relationships (for task delegation)
- Track lifecycle status: `running` | `completed` | `stopped` | `error` | `disconnected`
- Track activity status: `busy` | `idle` | `waiting_input`
- Update DB status in real-time based on SSE events from SDK

### 2. OpenCode SDK Integration

- Proxy SDK method calls over HTTP:
  - `session.create()`
  - `session.get()`
  - `session.list()`
  - `session.prompt()`
  - `session.command()`
  - `session.abort()`
  - `session.messages()`
  - `session.diff()`
  - `session.status()`
  - `app.agents()`
  - `command.list()`
  - `provider.list()`
  - `find.files()`
- Subscribe to SDK SSE event stream
- Forward events to clients via `/api/sessions/[id]/events` (SSE) and WebSocket

### 3. Real-Time Streaming

- Support SSE endpoints for backward compatibility
- Support WebSocket endpoint with topic-based pub/sub
- Keepalive: send `: keepalive\n\n` every 15 seconds
- CORS headers: `Access-Control-Allow-Origin: *` for cross-origin
- Message format: `data: {...}\n\n` for SSE, JSON for WebSocket

### 4. Message Pagination

- Support cursor-based pagination (`before=<id>`, `after=<id>`)
- Return `hasMore`, `oldestMessageId`, `totalCount`
- Limit max results: 200 per request
- Gap-fill: `after=<id>` returns messages AFTER that ID
- Stale cursor: if `after` ID not found, return ALL messages (client reconciles)

### 5. Workspace Isolation

- Support three strategies:
  - `"existing"` — use directory as-is
  - `"worktree"` — create git worktree from source repo
  - `"clone"` — clone source repo to new directory
- Track `source_directory` and `branch` for non-"existing"
- Clean up worktrees/clones on session delete

### 6. Database Requirements

**Sessions table:**
- `id` (UUID)
- `workspace_id` (FK)
- `instance_id` (FK)
- `opencode_session_id` (SDK session ID)
- `title`
- `directory`
- `parent_session_id` (nullable, FK for task delegation)
- `status` (enum: active, idle, stopped, completed, disconnected, error)
- `created_at`
- `stopped_at` (nullable)
- `total_tokens` (nullable)
- `total_cost` (nullable)

**Workspaces table:**
- `id` (UUID)
- `directory`
- `source_directory` (nullable)
- `isolation_strategy` (enum)
- `branch` (nullable)
- `display_name` (nullable)
- `created_at`

**Instances table:**
- `id` (UUID)
- `directory`
- `status` (enum: running, dead)
- `pid` (process ID)
- `port` (listening port)
- `created_at`

**Support pagination with filters:**
- `LIMIT` and `OFFSET`
- `WHERE status IN (...)`

### 7. GitHub Integration

- Implement RFC 8628 device authorization flow
- Store access tokens securely
- Proxy GitHub API v3 calls with `Authorization: token <token>` header
- Parse GitHub URLs to extract `owner` and `repo`
- Handle pagination for GitHub API (use `page` and `per_page` params)

### 8. Configuration

- Accept `NEXT_PUBLIC_API_BASE_URL` from environment
- Support all environment variables from `next.config.ts`
- Implement `/api/profile` endpoint (dynamic, not baked at build time)

### 9. Deployment Modes

- **Standalone:** Backend and frontend in same process
- **Split:** Backend and frontend separate, connected via full URL base
- Both modes serve from same API endpoints

### 10. Testing Strategy

- Unit tests for OpenCode SDK integration
- Integration tests for session CRUD operations
- E2E tests for SSE/WebSocket streaming
- Load tests for concurrent sessions and events

---

## Part 10: Complete Endpoint Summary

### Core Sessions (14)

```
POST   /api/sessions
GET    /api/sessions
PATCH  /api/sessions/[id]
GET    /api/sessions/[id]
DELETE /api/sessions/[id]
POST   /api/sessions/[id]/prompt
POST   /api/sessions/[id]/command
POST   /api/sessions/[id]/fork
POST   /api/sessions/[id]/resume
POST   /api/sessions/[id]/abort
GET    /api/sessions/[id]/events      (SSE)
GET    /api/sessions/[id]/messages
GET    /api/sessions/[id]/diffs
GET    /api/sessions/[id]/status
```

### Instances (4)

```
GET    /api/instances/[id]/agents
GET    /api/instances/[id]/commands
GET    /api/instances/[id]/models
GET    /api/instances/[id]/find/files
```

### Configuration (5)

```
GET    /api/config
PUT    /api/config
GET    /api/version
GET    /api/profile
GET    /api/fleet/summary
```

### Directories & Repositories (6)

```
GET    /api/directories
GET    /api/repositories
GET    /api/repositories/info
GET    /api/repositories/detail
GET    /api/repositories/refresh
POST   /api/open-directory
```

### Real-Time Streams (2)

```
GET    /api/activity-stream        (SSE)
WS     /ws                          (WebSocket, topic-based)
```

### Integrations (3 + GitHub Auth)

```
GET    /api/integrations
POST   /api/integrations
DELETE /api/integrations
POST   /api/integrations/github/auth/device-code
POST   /api/integrations/github/auth/poll
```

### GitHub API Proxies (13)

```
GET    /api/integrations/github/repos
GET    /api/integrations/github/repos/[owner]/[repo]/issues
GET    /api/integrations/github/repos/[owner]/[repo]/issues/[number]
GET    /api/integrations/github/repos/[owner]/[repo]/issues/[number]/comments
GET    /api/integrations/github/repos/[owner]/[repo]/issues/search
GET    /api/integrations/github/repos/[owner]/[repo]/pulls
GET    /api/integrations/github/repos/[owner]/[repo]/pulls/[number]
GET    /api/integrations/github/repos/[owner]/[repo]/pulls/[number]/comments
GET    /api/integrations/github/repos/[owner]/[repo]/pulls/[number]/status
GET    /api/integrations/github/repos/[owner]/[repo]/labels
GET    /api/integrations/github/repos/[owner]/[repo]/milestones
GET    /api/integrations/github/repos/[owner]/[repo]/assignees
GET    /api/integrations/github/bookmarks
```

### Skills (2)

```
GET    /api/skills
POST   /api/skills
```

### Workspace Roots (3)

```
GET    /api/workspace-roots
POST   /api/workspace-roots
DELETE /api/workspace-roots/[id]
```

**Total: 48+ Endpoints**

---

## Appendix: Key Source Files

### Frontend Client Configuration
- `src/lib/api-client.ts` — URL building & fetch wrapper
- `src/lib/api-types.ts` — request/response TypeScript types
- `next.config.ts` — environment variables & build config

### React Hooks for Real-Time
- `src/hooks/use-session-events.ts` — session event subscription
- `src/hooks/use-activity-stream.ts` — global activity stream
- `src/hooks/use-weave-socket.ts` — WebSocket management

### Backend Routes (48 files)
- `src/app/api/sessions/` — session CRUD & streaming
- `src/app/api/instances/` — agent/command/model listing
- `src/app/api/integrations/` — GitHub OAuth & API proxy
- `src/app/api/repositories/` — git repo scanning
- `src/app/api/directories/` — directory browsing
- `src/app/api/config/` — user configuration
- `src/app/api/fleet/` — fleet aggregates
- `src/app/api/skills/` — skill management
- `src/app/api/workspace-roots/` — workspace root management

---

**End of Complete API Contract**

This specification is production-ready and can be used directly to implement a .NET backend (or backend in any language) that serves this frontend with 100% compatibility.
