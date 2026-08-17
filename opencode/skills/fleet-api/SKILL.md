---
name: fleet-api
description: Weave Fleet API for managing Fleet sessions (not OpenCode sessions), automations, GitHub integration, and session sources. Use when an agent needs to create, query, or control Weave Fleet sessions, manage automations, or interact with GitHub repositories through the Fleet API.
---

# fleet-api

**Fleet API for sessions, automations, GitHub integration, and session sources.**

Use this skill when an agent needs to:
- Create sessions with rich context from GitHub PRs, issues, or other sources
- Manage automations that trigger on session events
- Query session status, messages, diffs, or origin
- Interact with GitHub repositories, pull requests, and issues
- Discover available session source providers
- Add context to existing sessions programmatically

## When to Use

- **Creating context-aware sessions**: Spawn a session with full PR/issue context automatically injected
- **Building automations**: Create event-driven workflows that trigger on session lifecycle events
- **Querying Fleet state**: List sessions, check status, retrieve messages or diffs
- **GitHub workflows**: Poll for PRs/issues, create sessions from them, let automations handle the work
- **Session orchestration**: Fork sessions, send prompts, abort/stop/resume sessions

## Context

- **Default API base URL**: `http://localhost:5001` (port depends on configuration, commonly 2113 or 5001)
- **Override via environment**: `export FLEET_API_BASE_URL=http://custom-host:port`
- **Script location**: `~/.config/opencode/skills/fleet-api/scripts/fleet-api.sh`
- **Dependencies**: `curl`, `jq` (standard on macOS)

### Authentication

**Localhost requests require no authentication.** When the Fleet API is running in local mode (default) and the request originates from localhost (127.0.0.1 or ::1), authentication is automatically bypassed. This means the script works out of the box with no token configuration needed.

**For remote access** (non-localhost), a Bearer token is required:
- The server reads the `WEAVE_FLEET_AUTH_TOKEN` environment variable at startup
- If set (and ≥16 characters), it uses that token; otherwise it auto-generates one
- The token is printed at startup: `http://localhost:{port}/login?token={token}`
- Pass via CLI flag: `--token your-token` (or `-t`)
- Or set via environment: `export FLEET_API_TOKEN=your-token`

```bash
# Localhost — no token needed
fleet-api.sh list-sessions

# Remote access — token required
fleet-api.sh list-sessions --base-url http://remote-host:5001 --token "your-token"
```

## Available Actions

### Health & Discovery
- `health` — Check Fleet API health (GET /api/fleet/summary)
- `source-catalog` — List available session source providers (GET /api/session-sources/catalog)
- `get-event-catalog` — List available automation event types (GET /api/automations/event-catalog)

### Sessions
- `list-sessions` — List sessions (optional: `--workspace-id`, `--status`, `--tags`)
- `get-session` — Get session details (requires `--id`)
- `get-session-origin` — Get session origin/provenance (requires `--id`)
- `get-session-messages` — Get session messages (requires `--id`, optional: `--limit`, `--before`)
- `get-session-diffs` — Get git diffs for session (requires `--id`)
- `get-session-status` — Get session status (requires `--id`)
- `create-session` — Create session (requires `--directory`, `--title`, optional: `--isolation-strategy`, `--branch`, `--harness-type`, `--initial-prompt`, `--tags`, `--project-id`)
- `delete-session` — Delete session (requires `--id`)
- `update-session-tags` — Update session tags (requires `--id`, `--tags`)
- `prompt-session` — Send prompt to session (requires `--id`, `--prompt`)
- `stop-session` — Stop session (requires `--id`)
- `abort-session` — Abort session (requires `--id`)
- `resume-session` — Resume session (requires `--id`)
- `fork-session` — Fork session (requires `--id`)
- `get-session-delegations` — Get session delegations (requires `--id`, optional: `--limit`, `--before`)
- `browse-session-files` — Browse session files (requires `--id`, optional: `--path`)
- `get-session-file-content` — Get session file content (requires `--id`, `--path`)
- `search-session-files` — Search session files (requires `--id`, `--query`)
- `get-session-models` — Get available models for session (requires `--id`)
- `get-session-agents` — Get available agents for session (requires `--id`)
- `add-session-source` — Add source to existing session (requires `--id`, `--provider-id`, `--source-type`, `--action-id`, `--input-json`)
- `preview-session-source` — Preview source before adding (requires `--provider-id`, `--source-type`, `--action-id`, `--input-json`)

### GitHub Session Convenience Actions
- `create-session-from-github-pr` — Create session from GitHub PR with full context
  - Required: `--owner`, `--repo`, `--number`, `--repository-path`
  - Optional: `--title`, `--initial-prompt`, `--isolation-strategy`, `--branch`, `--tags`, `--project-id`, `--harness-type`
- `create-session-from-github-issue` — Create session from GitHub issue with full context
  - Required: `--owner`, `--repo`, `--number`, `--repository-path`
  - Optional: `--title`, `--initial-prompt`, `--isolation-strategy`, `--branch`, `--tags`, `--project-id`, `--harness-type`

### Automations
- `list-automations` — List automations (optional: `--workspace-id`)
- `get-automation` — Get automation (requires `--id`)
- `create-automation` — Create automation (requires `--name`, `--prompt`, `--trigger-type`, `--trigger-config`, optional: `--max-concurrent-runs`, `--max-runs-per-hour`, `--timeout-minutes`, `--workspace-id`, `--model`, `--agent`, `--target-tags`, `--target-type`)
- `update-automation` — Update automation (requires `--id`, plus any fields to update)
- `delete-automation` — Delete automation (requires `--id`)
- `enable-automation` — Enable automation (requires `--id`)
- `disable-automation` — Disable automation (requires `--id`)
- `run-automation` — Manually trigger automation (requires `--id`)

### GitHub Integration
- `list-github-repos` — List GitHub repositories
- `list-github-pulls` — List pull requests (requires `--owner`, `--repo`, optional: `--state`)
- `get-github-pr` — Get pull request detail (requires `--owner`, `--repo`, `--number`)
- `list-github-pr-comments` — List PR comments (requires `--owner`, `--repo`, `--number`)
- `list-github-issues` — List issues (requires `--owner`, `--repo`, optional: `--state`)
- `get-github-issue` — Get issue detail (requires `--owner`, `--repo`, `--number`)
- `list-github-issue-comments` — List issue comments (requires `--owner`, `--repo`, `--number`)
- `search-github-issues` — Search issues (requires `--owner`, `--repo`, optional: `--query`)

## Key Workflow: Automated PR Review

This is the primary workflow the Fleet API enables:

### 1. Poll GitHub for PRs Needing Review

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh list-github-pulls \
  --owner myorg \
  --repo myrepo \
  --state open
```

Returns JSON array of PRs. Filter for those with `review_requested` or specific labels.

### 2. Create Session from PR with Full Context

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh create-session-from-github-pr \
  --owner myorg \
  --repo myrepo \
  --number 42 \
  --repository-path /Users/pgermishuys/source/myrepo \
  --tags "github-pr,review-requested" \
  --initial-prompt "Review this pull request for code quality, security, and test coverage"
```

This:
- Creates a session with `isolationStrategy: worktree` (default)
- Attaches a `github-pull-request` source with `owner`, `repo`, `number`
- The `GitHubSessionSourceProvider` fetches PR title, body, comments, and injects them into the session
- Tags the session with `github-pr` and `review-requested`
- Sends the initial prompt to start the review

### 3. Automation Triggers on Session Creation

Create an automation that watches for tagged sessions:

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh create-automation \
  --name "Auto-review PRs" \
  --prompt "Review this pull request" \
  --trigger-type event \
  --trigger-config '{"eventType":"session.created"}' \
  --target-type tagged_session \
  --target-tags "github-pr,review-requested" \
  --max-concurrent-runs 2 \
  --max-runs-per-hour 20 \
  --timeout-minutes 30
```

When a session is created with tags `github-pr` and `review-requested`, this automation fires and sends the prompt to the session.

### 4. Agent Has Full PR Context

The session now contains:
- PR title, body, and description
- All comments and review threads
- The repository checked out at the PR branch (via worktree)
- The initial prompt to guide the review

The agent can:
- Read the code changes
- Run tests
- Analyze security issues
- Post comments back to the PR (via GitHub API)

## Session Sources: Adding Context

Session sources are the mechanism for injecting rich context into sessions.

### Discover Available Providers

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh source-catalog
```

Returns:
```json
{
  "providers": [
    {
      "id": "builtin.local",
      "name": "Local Directory",
      "sourceTypes": ["directory"]
    },
    {
      "id": "builtin.repository",
      "name": "Git Repository",
      "sourceTypes": ["repository"]
    },
    {
      "id": "builtin.github",
      "name": "GitHub",
      "sourceTypes": ["github-issue", "github-pull-request"]
    },
    {
      "id": "builtin.automation",
      "name": "Automation",
      "sourceTypes": ["automation"]
    }
  ]
}
```

### Add Source to Existing Session

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh add-session-source \
  --id abc123 \
  --provider-id builtin.github \
  --source-type github-issue \
  --action-id add-to-session \
  --input-json '{"owner":"myorg","repo":"myrepo","number":99}'
```

This adds a GitHub issue as context to an existing session.

### Preview Source Before Adding

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh preview-session-source \
  --provider-id builtin.github \
  --source-type github-pull-request \
  --action-id start-session \
  --input-json '{"owner":"myorg","repo":"myrepo","number":42}'
```

Returns a preview of what context will be injected (title, body, comments, etc.) without actually creating a session.

## Automation Event Types

Available event types (from `get-event-catalog`):

- `session.created` — Fires when a new session is created
- `session.idle` — Fires when a session becomes idle
- `session.status` — Fires on session status change
- `session.deleted` — Fires when a session is deleted
- `message.created` — Fires when a message is created
- `message.updated` — Fires when a message is updated
- `delegation.created` — Fires when a delegation is created
- `delegation.updated` — Fires when a delegation is updated
- `delegation.completed` — Fires when a delegation completes

## Usage Examples

### Check Fleet Health

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh health
```

### List All Sessions

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh list-sessions
```

### List Sessions by Tag

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh list-sessions --tags "github-pr,review-requested"
```

### Get Session Details

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh get-session --id abc123
```

### Get Session Origin (Provenance)

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh get-session-origin --id abc123
```

Returns the source that created the session (GitHub PR, issue, automation, etc.).

### Send Prompt to Session

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh prompt-session \
  --id abc123 \
  --prompt "Now check for security vulnerabilities"
```

### Create Session from GitHub Issue

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh create-session-from-github-issue \
  --owner myorg \
  --repo myrepo \
  --number 99 \
  --repository-path /Users/pgermishuys/source/myrepo \
  --tags "github-issue,bug" \
  --initial-prompt "Investigate and fix this bug"
```

### List GitHub PRs

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh list-github-pulls \
  --owner myorg \
  --repo myrepo \
  --state open
```

### Get GitHub PR Detail

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh get-github-issue \
  --owner myorg \
  --repo myrepo \
  --number 42
```

### Create Automation

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh create-automation \
  --name "Auto-review PRs" \
  --prompt "Review this pull request" \
  --trigger-type event \
  --trigger-config '{"eventType":"session.created"}' \
  --target-type tagged_session \
  --target-tags "github-pr,review-requested" \
  --max-concurrent-runs 2
```

### Enable/Disable Automation

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh enable-automation --id auto123
.opencode/skills/fleet-api/scripts/fleet-api.sh disable-automation --id auto123
```

### Manually Trigger Automation

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh run-automation --id auto123
```

## Important Notes

### Mutating Operations

- **Session creation**: Creates a new session and may perform git operations (worktree, clone)
- **Prompt sending**: Sends a prompt to the session, which may trigger agent execution
- **Automation creation/update**: Modifies automation configuration
- **Automation enable/disable**: Changes automation state
- **Automation run**: Manually triggers automation execution

### Error Handling

The script outputs JSON on success and JSON error objects on failure, exiting with code 1:

```json
{
  "error": "Session not found",
  "statusCode": 404
}
```

### Tags

Tags are comma-separated strings:
- `--tags "github-pr,review-requested,urgent"`
- Used for filtering sessions and targeting automations

### Isolation Strategies

- `existing` — Use existing working directory (default for most cases)
- `worktree` — Create a git worktree (default for GitHub PR sessions)
- `clone` — Clone the repository to a new location

### Harness Types

- `opencode` — OpenCode harness (default)
- `claude-code` — Claude Code harness

## Script Reference

```bash
.opencode/skills/fleet-api/scripts/fleet-api.sh <action> [options]
```

### Global Options

- `-b, --base-url <url>` — Override API base URL (default: http://localhost:5001)
- `-t, --token <token>` — Bearer token for remote access (not needed for localhost)
- `-h, --help` — Show help

### Common Options

- `--id <id>` — Session or automation ID
- `--owner <owner>` — GitHub repository owner
- `--repo <repo>` — GitHub repository name
- `--number <number>` — GitHub issue/PR number
- `--tags <tags>` — Comma-separated tags
- `--prompt <prompt>` — Prompt text
- `--title <title>` — Session title
- `--directory <path>` — Working directory path
- `--repository-path <path>` — Repository path for GitHub sources
- `--isolation-strategy <strategy>` — Isolation strategy (existing, worktree, clone)
- `--branch <branch>` — Git branch name
- `--harness-type <type>` — Harness type (opencode, claude-code)
- `--initial-prompt <prompt>` — Initial prompt for new session
- `--project-id <id>` — Project ID
- `--workspace-id <id>` — Workspace ID
- `--provider-id <id>` — Session source provider ID
- `--source-type <type>` — Session source type
- `--action-id <id>` — Session source action ID
- `--input-json <json>` — Session source input JSON

Run with `--help` for full usage.

## Integration with Automations

The Fleet API is designed for automation-driven workflows:

1. **Event-driven**: Automations trigger on session lifecycle events
2. **Tag-based targeting**: Automations can target sessions by tags
3. **Context injection**: Session sources provide rich context automatically
4. **Parallel execution**: Control concurrency with `maxConcurrentRuns`
5. **Rate limiting**: Control execution rate with `maxRunsPerHour`

Example workflow:
- GitHub webhook triggers a script
- Script calls `create-session-from-github-pr`
- Session is tagged with `github-pr` and `review-requested`
- Automation with `targetTags: ["github-pr", "review-requested"]` fires
- Agent reviews the PR with full context
- Agent posts results back to GitHub

This enables fully automated PR review, issue triage, and other workflows.
