---
name: fleet-api
description: Weave Fleet API for managing Fleet sessions (not OpenCode sessions), automations, GitHub integration, and session sources. Use when an agent needs to create, query, or control Weave Fleet sessions, manage automations, or interact with GitHub repositories through the Fleet API.
---

# fleet-api

**Fleet API for sessions, boards, projects, automations, and integrations.**

Use this skill when an agent needs to:
- Create and manage sessions with rich context from GitHub PRs, issues, or other sources
- Manage boards, lanes, cards, and board sources (GitHub issue tracking)
- Organize sessions into projects
- Create automations that trigger on session events
- Query session status, messages, diffs, origin, or delegations
- Interact with GitHub repositories, pull requests, and issues
- Discover available session source providers
- Add context to existing sessions programmatically
- Manage credentials, preferences, and workspace configuration

## API Reference

- **Default API base URL**: `http://localhost:2113`
- **OpenAPI specification**: `http://localhost:2113/openapi/v1.json`
- **Script location**: `C:\Users\piete\.config\opencode\skills\fleet-api\scripts\fleet-api.sh` (bash)

### Authentication

**Localhost requests require no authentication.** When the Fleet API is running in local mode (default) and the request originates from localhost (127.0.0.1 or ::1), authentication is automatically bypassed.

**For remote access** (non-localhost), a Bearer token is required:
- The server reads the `WEAVE_FLEET_AUTH_TOKEN` environment variable at startup
- If set (and ≥16 characters), it uses that token; otherwise it auto-generates one
- The token is printed at startup: `http://localhost:2113/login?token={token}`
- Pass via `Authorization: Bearer {token}` header

## REST API Endpoints

### Auth

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/auth/status` | Get authentication status |
| GET | `/auth/login` | Initiate login flow (query: `returnUrl`) |
| POST | `/auth/token-login` | Login with token (body: `TokenLoginRequest`) |
| POST | `/auth/logout` | Logout (query: `returnUrl`) |

### Sessions

| Method | Path | Description | Key Parameters |
|--------|------|-------------|----------------|
| GET | `/api/sessions` | List sessions | `limit`, `offset`, `status`, `retentionStatus`, `projectId`, `tags` |
| POST | `/api/sessions` | Create session | Body: `CreateSessionApiRequest` |
| GET | `/api/sessions/{id}` | Get session details | - |
| DELETE | `/api/sessions/{id}` | Delete session | - |
| PATCH | `/api/sessions/{id}` | Update session title | Body: `UpdateSessionTitleRequest` |
| GET | `/api/sessions/{id}/origin` | Get session origin/provenance | - |
| GET | `/api/sessions/{id}/delegations` | Get session delegations | - |
| GET | `/api/sessions/{id}/messages` | Get session messages | `limit`, `before` |
| GET | `/api/sessions/{id}/diffs` | Get git diffs | - |
| GET | `/api/sessions/{id}/status` | Get session status | - |
| GET | `/api/sessions/{id}/events` | Get session events | - |
| POST | `/api/sessions/{id}/prompt` | Send prompt | Body: `SendPromptApiRequest` |
| POST | `/api/sessions/{id}/abort` | Abort session | - |
| POST | `/api/sessions/{id}/stop` | Stop session | - |
| POST | `/api/sessions/{id}/resume` | Resume session | - |
| POST | `/api/sessions/{id}/fork` | Fork session | Body: `ForkSessionApiRequest` |
| POST | `/api/sessions/{id}/source-preview` | Preview session source | Body: `PreviewSessionSourceApiRequest` |
| POST | `/api/sessions/{id}/sources` | Add session source | Body: `AddSessionSourceApiRequest` |
| POST | `/api/sessions/{id}/command` | Send command | Body: `SendCommandApiRequest` |
| GET | `/api/sessions/{id}/commands` | Get available commands | - |
| PATCH | `/api/sessions/{id}/retention` | Update retention status | Body: `UpdateSessionRetentionRequest` |
| PATCH | `/api/sessions/{id}/project` | Move session to project | Body: `MoveSessionRequest` |
| PATCH | `/api/sessions/{id}/tags` | Update session tags | Body: `UpdateSessionTagsRequest` |
| GET | `/api/sessions/{id}/models` | Get available models | - |
| GET | `/api/sessions/{id}/agents` | Get available agents | - |
| GET | `/api/sessions/{id}/find/files` | Find files (search) | `q` (query) |
| GET | `/api/sessions/{id}/files/browse` | Browse directory | `path` |
| GET | `/api/sessions/{id}/files/content` | Read file content | `path` |
| POST | `/api/sessions/{id}/questions/{requestId}/answer` | Answer question | Body: `QuestionAnswerApiRequest` |
| POST | `/api/sessions/{id}/questions/{requestId}/reject` | Reject question | - |

### Boards

| Method | Path | Description | Key Parameters |
|--------|------|-------------|----------------|
| GET | `/api/boards` | List boards | - |
| POST | `/api/boards` | Create board | Body: `CreateBoardRequest` |
| PATCH | `/api/boards/{boardId}` | Update board | Body: `UpdateBoardRequest` |
| DELETE | `/api/boards/{boardId}` | Delete board | - |
| POST | `/api/boards/{boardId}/sync` | Sync board with sources | - |
| GET | `/api/boards/{boardId}/sources` | List board sources | - |
| POST | `/api/boards/{boardId}/sources` | Create board source | Body: `CreateBoardSourceRequest` |
| PATCH | `/api/boards/{boardId}/sources/{sourceId}` | Update board source | Body: `UpdateBoardSourceRequest` |
| DELETE | `/api/boards/{boardId}/sources/{sourceId}` | Delete board source | - |
| GET | `/api/boards/{boardId}/lanes` | List board lanes | - |
| POST | `/api/boards/{boardId}/lanes` | Create board lane | Body: `CreateBoardLaneRequest` |
| PATCH | `/api/boards/{boardId}/lanes/{laneId}` | Update board lane | Body: `UpdateBoardLaneRequest` |
| DELETE | `/api/boards/{boardId}/lanes/{laneId}` | Delete board lane | - |
| PATCH | `/api/boards/{boardId}/lanes/reorder` | Reorder board lanes | Body: `ReorderBoardLanesRequest` |
| GET | `/api/boards/{boardId}/cards` | List board cards | - |
| POST | `/api/boards/{boardId}/cards` | Create board card | Body: `CreateBoardCardRequest` |
| PATCH | `/api/boards/{boardId}/cards/{cardId}` | Update board card | Body: `UpdateBoardCardRequest` |
| DELETE | `/api/boards/{boardId}/cards/{cardId}` | Delete board card | - |
| POST | `/api/boards/{boardId}/cards/{cardId}/archive` | Archive board card | - |
| POST | `/api/boards/{boardId}/cards/{cardId}/move` | Move board card | Body: `MoveBoardCardRequest` |

### Projects

| Method | Path | Description | Key Parameters |
|--------|------|-------------|----------------|
| GET | `/api/projects` | List projects | - |
| POST | `/api/projects` | Create project | Body: `CreateProjectRequest` |
| GET | `/api/projects/{id}` | Get project | - |
| PATCH | `/api/projects/{id}` | Update project | Body: `UpdateProjectRequest` |
| DELETE | `/api/projects/{id}` | Delete project | `mode` (query, required) |
| PATCH | `/api/projects/{id}/reorder` | Reorder project | Body: `ReorderProjectRequest` |

### Automations

| Method | Path | Description | Key Parameters |
|--------|------|-------------|----------------|
| GET | `/api/automations` | List automations | `workspaceId` |
| POST | `/api/automations` | Create automation | Body: `CreateAutomationRequest` |
| GET | `/api/automations/{id}` | Get automation | - |
| PUT | `/api/automations/{id}` | Update automation | Body: `UpdateAutomationRequest` |
| DELETE | `/api/automations/{id}` | Delete automation | - |
| POST | `/api/automations/{id}/enable` | Enable automation | - |
| POST | `/api/automations/{id}/disable` | Disable automation | - |
| POST | `/api/automations/{id}/run` | Manually trigger automation | - |
| GET | `/api/automations/event-catalog` | List available event types | - |

### Session Sources

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/session-sources/catalog` | Get session source catalog (providers, types, actions) |

### Plugins

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/plugins` | List installed plugins |

### Fleet

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/fleet/summary` | Get fleet summary (active/idle sessions, tokens, cost) |
| GET | `/api/version` | Get API version |
| GET | `/api/profile` | Get user profile |
| GET | `/api/repositories` | List repositories |
| GET | `/api/repositories/info` | Get repository info (query: `path`) |
| GET | `/api/repositories/worktrees` | Get repository worktrees (query: `path`) |
| GET | `/api/repositories/detail` | Get repository detail (query: `path`) |
| POST | `/api/repositories/refresh` | Refresh repositories |
| GET | `/api/integrations` | List integrations |
| GET | `/api/available-tools` | List available tools |
| GET | `/api/activity-stream` | Get activity stream |

### Workspaces

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/workspaces` | List workspaces |
| GET | `/api/workspaces/{id}` | Get workspace |
| PATCH | `/api/workspaces/{id}` | Rename workspace (body: `RenameWorkspaceRequest`) |
| GET | `/api/workspace-roots` | List workspace roots |
| POST | `/api/workspace-roots` | Add workspace root (body: `AddWorkspaceRootRequest`) |
| DELETE | `/api/workspace-roots/{id}` | Delete workspace root |

### Analytics

| Method | Path | Description | Key Parameters |
|--------|------|-------------|----------------|
| GET | `/api/analytics/summary` | Get analytics summary | `from`, `to`, `projectId` |
| GET | `/api/analytics/daily` | Get daily analytics | `from`, `to`, `projectId` |
| GET | `/api/analytics/sessions` | Get session analytics | `from`, `to`, `projectId`, `limit` |
| GET | `/api/analytics/models` | Get model analytics | `from`, `to` |
| GET | `/api/analytics/export` | Export token events | `from`, `to`, `projectId`, `format` |

### User

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/user/me` | Get current user |
| POST | `/api/user/me/complete-onboarding` | Complete onboarding |

### Credentials

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/credentials` | List credentials |
| PUT | `/api/credentials` | Store credential (body: `StoreCredentialRequest`) |
| PUT | `/api/credentials/{id}` | Update credential (body: `UpdateCredentialRequest`) |
| DELETE | `/api/credentials/{id}` | Delete credential |

### Preferences

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/preferences` | Get all preferences |
| PUT | `/api/preferences/{key}` | Set preference (body: `SetPreferenceRequest`) |

### Config

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/config` | Get config (query: `directory`) |
| PUT | `/api/config` | Update config (body: JSON object) |
| GET | `/api/config/paths` | Get config paths |
| GET | `/api/config/client` | Get client config |

### Directories

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/directories` | List directories (query: `path`, `unconstrained`) |
| POST | `/api/open-directory` | Open directory (body: `OpenDirectoryRequest`) |
| POST | `/api/open-file` | Open file (body: `OpenFileRequest`) |

### Key Files

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/key-files` | Get key files (query: `directory`) |

### Skills

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/skills` | List installed skills |
| GET | `/api/skills/catalog` | Get skill catalog |
| POST | `/api/skills/install` | Install skill (body: `InstallSkillRequest`) |
| POST | `/api/skills/{name}/update` | Update skill |
| DELETE | `/api/skills/{name}` | Delete skill |
| GET | `/api/skills/{name}/update-check` | Check for skill update |
| GET | `/api/skills/manifest` | Get skill manifest |

### Instances

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/instances/{id}/models` | Get instance models |
| GET | `/api/instances/{id}/commands` | Get instance commands |
| POST | `/api/instances/{id}/command` | Send instance command (body: `SendCommandApiRequest`) |
| GET | `/api/instances/{id}/agents` | Get instance agents |
| GET | `/api/instances/{id}/find/files` | Find instance files (query: `q`) |

### Harnesses

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/harnesses` | List harnesses |
| POST | `/api/harnesses/opencode/warmup` | Warmup OpenCode harness |

### NuCode (AI Provider Management)

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/nucode/test-connection` | Test NuCode connection |
| GET | `/api/nucode/providers` | List NuCode providers |
| GET | `/api/nucode/providers/{id}` | Get NuCode provider |
| PUT | `/api/nucode/providers/{id}/credentials` | Store provider credentials (body: `NuCodeStoreCredentialsRequest`) |
| DELETE | `/api/nucode/providers/{id}/credentials` | Disconnect provider |
| POST | `/api/nucode/providers/{id}/test` | Test provider connection |
| POST | `/api/nucode/providers/{id}/auth/device-code` | Initiate device flow |
| POST | `/api/nucode/providers/{id}/auth/poll` | Poll device flow (body: `NuCodeDevicePollRequest`) |
| PUT | `/api/nucode/providers/{id}/config` | Configure provider (body: `NuCodeProviderConfigRequest`) |

### Smart Links

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/sessions/{sessionId}/smart-links` | Get smart links |
| POST | `/api/sessions/{sessionId}/smart-links` | Upsert smart link (body: `UpsertSmartLinkRequest`) |
| GET | `/api/sessions/{sessionId}/smart-links/all` | Get all smart links |
| POST | `/api/sessions/{sessionId}/smart-links/bulk` | Bulk upsert smart links (body: array of `UpsertSmartLinkRequest`) |
| PATCH | `/api/sessions/{sessionId}/smart-links/{linkId}/dismiss` | Dismiss smart link |

### Update

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/update/status` | Get update status |
| POST | `/api/update/check` | Trigger update check |
| POST | `/api/update/download` | Trigger update download |

### Telemetry

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/telemetry/actions` | Log UI action (body: `UiActionRequest`) |

### Admin

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/admin/import-legacy-sessions` | Import legacy sessions |
| GET | `/api/admin/opencode/pool` | Get OpenCode pool health |

## Key Request Schemas

### CreateSessionApiRequest

```json
{
  "directory": "string (required)",
  "title": "string (required)",
  "isolationStrategy": "existing | worktree | clone (optional)",
  "branch": "string (optional)",
  "harnessType": "opencode | claude-code (optional)",
  "initialPrompt": "string (optional)",
  "source": {
    "providerId": "string",
    "sourceType": "string",
    "actionId": "string",
    "input": {}
  },
  "onComplete": {
    "notifySessionId": "string",
    "notifyInstanceId": "string"
  },
  "projectId": "string (optional)",
  "tags": ["string"] (optional)
}
```

### CreateBoardRequest

```json
{
  "name": "string (required)"
}
```

### CreateBoardSourceRequest

```json
{
  "providerType": "github (required)",
  "config": "JSON string with provider-specific config (required)"
}
```

Example GitHub board source config:
```json
{
  "owner": "myorg",
  "repo": "myrepo",
  "labels": ["bug", "enhancement"]
}
```

### CreateBoardLaneRequest

```json
{
  "name": "string (required)",
  "position": "integer (required)"
}
```

### CreateBoardCardRequest

```json
{
  "laneId": "string (required)",
  "title": "string (required)",
  "position": "integer (required)"
}
```

### CreateProjectRequest

```json
{
  "name": "string (required)",
  "description": "string (optional)"
}
```

### CreateAutomationRequest

```json
{
  "name": "string (required)",
  "prompt": "string (required)",
  "triggerType": "event | schedule (required)",
  "triggerConfig": "JSON string (required)",
  "maxConcurrentRuns": "integer (default: 1)",
  "maxRunsPerHour": "integer (default: 10)",
  "timeoutMinutes": "integer (default: 30)",
  "workspaceId": "string (optional)",
  "model": "string (optional)",
  "agent": "string (optional)",
  "targetTags": ["string"] (optional),
  "targetType": "tagged_session | all (optional)"
}
```

Example event trigger config:
```json
{
  "eventType": "session.created"
}
```

### SendPromptApiRequest

```json
{
  "text": "string (required)",
  "agent": "string (optional)",
  "model": {
    "providerId": "string",
    "modelId": "string"
  },
  "attachments": [
    {
      "mime": "string",
      "filename": "string",
      "data": "base64 string"
    }
  ],
  "userMessageId": "string (optional)",
  "correlationId": "string (optional)"
}
```

### AddSessionSourceApiRequest

```json
{
  "source": {
    "providerId": "builtin.github",
    "sourceType": "github-issue | github-pull-request",
    "actionId": "add-to-session",
    "input": {
      "owner": "string",
      "repo": "string",
      "number": "integer"
    }
  },
  "confirm": true
}
```

## Common Workflows

### 1. Create Session from GitHub PR

```powershell
$body = @{
  directory = "C:\source\myrepo"
  title = "Review PR #42"
  isolationStrategy = "worktree"
  harnessType = "opencode"
  initialPrompt = "Review this pull request for code quality and security"
  source = @{
    providerId = "builtin.github"
    sourceType = "github-pull-request"
    actionId = "start-session"
    input = @{
      owner = "myorg"
      repo = "myrepo"
      number = 42
    }
  }
  tags = @("github-pr", "review-requested")
} | ConvertTo-Json -Depth 10

Invoke-RestMethod -Uri "http://localhost:2113/api/sessions" -Method POST -Body $body -ContentType "application/json"
```

### 2. Create Automation for PR Review

```powershell
$body = @{
  name = "Auto-review PRs"
  prompt = "Review this pull request"
  triggerType = "event"
  triggerConfig = '{"eventType":"session.created"}'
  targetType = "tagged_session"
  targetTags = @("github-pr", "review-requested")
  maxConcurrentRuns = 2
  maxRunsPerHour = 20
  timeoutMinutes = 30
} | ConvertTo-Json -Depth 10

Invoke-RestMethod -Uri "http://localhost:2113/api/automations" -Method POST -Body $body -ContentType "application/json"
```

### 3. Create Board with GitHub Source

```powershell
# Create board
$board = Invoke-RestMethod -Uri "http://localhost:2113/api/boards" -Method POST -Body '{"name":"My Issues"}' -ContentType "application/json"

# Add GitHub source
$sourceBody = @{
  providerType = "github"
  config = @{
    owner = "myorg"
    repo = "myrepo"
    labels = @("bug", "enhancement")
  } | ConvertTo-Json
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:2113/api/boards/$($board.id)/sources" -Method POST -Body $sourceBody -ContentType "application/json"

# Sync board
Invoke-RestMethod -Uri "http://localhost:2113/api/boards/$($board.id)/sync" -Method POST
```

### 4. List Sessions with Filters

```powershell
# List all active sessions
Invoke-RestMethod -Uri "http://localhost:2113/api/sessions?status=active&limit=50"

# List sessions by tag
Invoke-RestMethod -Uri "http://localhost:2113/api/sessions?tags=github-pr,review-requested"

# List sessions by project
Invoke-RestMethod -Uri "http://localhost:2113/api/sessions?projectId=proj-123"
```

### 5. Get Session Source Catalog

```powershell
Invoke-RestMethod -Uri "http://localhost:2113/api/session-sources/catalog"
```

Returns:
```json
{
  "providers": [
    {
      "id": "builtin.github",
      "name": "GitHub",
      "sourceTypes": ["github-issue", "github-pull-request"]
    },
    {
      "id": "builtin.local",
      "name": "Local Directory",
      "sourceTypes": ["directory"]
    }
  ]
}
```

### 6. Send Prompt to Session

```powershell
$body = @{
  text = "Now check for security vulnerabilities"
  agent = $null
  model = $null
  attachments = $null
  userMessageId = $null
  correlationId = $null
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:2113/api/sessions/{sessionId}/prompt" -Method POST -Body $body -ContentType "application/json"
```

## Automation Event Types

Available event types (from `/api/automations/event-catalog`):

- `session.created` — Fires when a new session is created
- `session.idle` — Fires when a session becomes idle
- `session.status` — Fires on session status change
- `session.deleted` — Fires when a session is deleted
- `message.created` — Fires when a message is created
- `message.updated` — Fires when a message is updated
- `delegation.created` — Fires when a delegation is created
- `delegation.updated` — Fires when a delegation is updated
- `delegation.completed` — Fires when a delegation completes

## Isolation Strategies

- `existing` — Use existing working directory (default for most cases)
- `worktree` — Create a git worktree (default for GitHub PR sessions)
- `clone` — Clone the repository to a new location

## Harness Types

- `opencode` — OpenCode harness (default)
- `claude-code` — Claude Code harness

## Using the Bash Script

The skill includes a bash script at `C:\Users\piete\.config\opencode\skills\fleet-api\scripts\fleet-api.sh` that provides a command-line interface to the Fleet API. On Windows, you can run it via Git Bash, WSL, or similar.

Example:
```bash
./fleet-api.sh list-sessions --tags "github-pr"
./fleet-api.sh create-session-from-github-pr --owner myorg --repo myrepo --number 42 --repository-path /c/source/myrepo
```

For direct REST API access from PowerShell, use `Invoke-RestMethod` as shown in the workflow examples above.

## Important Notes

### Mutating Operations

- **Session creation**: Creates a new session and may perform git operations (worktree, clone)
- **Prompt sending**: Sends a prompt to the session, which may trigger agent execution
- **Automation creation/update**: Modifies automation configuration
- **Board sync**: Fetches issues from GitHub and creates/updates cards
- **Project operations**: Affects session organization

### Error Handling

The API returns standard HTTP status codes:
- `200` — Success
- `201` — Created
- `204` — No Content (success, no body)
- `400` — Bad Request
- `401` — Unauthorized
- `404` — Not Found
- `409` — Conflict

Error responses include JSON with `error` and `statusCode` fields.

### Tags

Tags are arrays of strings:
- Used for filtering sessions
- Used for targeting automations
- Example: `["github-pr", "review-requested", "urgent"]`

## Integration with Automations

The Fleet API is designed for automation-driven workflows:

1. **Event-driven**: Automations trigger on session lifecycle events
2. **Tag-based targeting**: Automations can target sessions by tags
3. **Context injection**: Session sources provide rich context automatically
4. **Parallel execution**: Control concurrency with `maxConcurrentRuns`
5. **Rate limiting**: Control execution rate with `maxRunsPerHour`

Example workflow:
- GitHub webhook triggers a script
- Script calls `POST /api/sessions` with GitHub PR source
- Session is tagged with `github-pr` and `review-requested`
- Automation with `targetTags: ["github-pr", "review-requested"]` fires
- Agent reviews the PR with full context
- Agent posts results back to GitHub

This enables fully automated PR review, issue triage, and other workflows.
