# API Contract Quick Reference

**Document:** `COMPLETE_API_CONTRACT.md` (2,032 lines)

## TL;DR - What You Need to Know

The Weave Agent Fleet frontend expects a backend that implements:

### 1. **48+ REST Endpoints**

| Category | Count | Key Endpoints |
|----------|-------|---------------|
| Sessions | 14 | POST /api/sessions, GET /api/sessions, PATCH/DELETE/GET [id], /prompt, /command, /fork, /resume, /abort, /events (SSE), /messages, /diffs, /status |
| Instances | 4 | /agents, /commands, /models, /find/files |
| Config | 5 | GET /api/config, PUT, GET /api/version, /api/profile, /api/fleet/summary |
| Repos | 6 | GET /api/directories, /repositories, /repositories/info, /detail, /refresh, POST /open-directory |
| Streams | 2 | GET /api/activity-stream (SSE), WS /ws (WebSocket) |
| Integration | 3 + auth | GET/POST/DELETE /api/integrations, GitHub device code + poll |
| GitHub Proxy | 13 | /repos, /issues, /pulls, /labels, /milestones, /assignees, /bookmarks |
| Skills | 2 | GET/POST /api/skills |
| Workspace | 3 | GET/POST/DELETE /api/workspace-roots |

### 2. **Two Real-Time Protocols**

| Protocol | Endpoint | Purpose |
|----------|----------|---------|
| **SSE** | GET /api/sessions/[id]/events | Session-specific events (backward compat) |
| **SSE** | GET /api/activity-stream | Global activity (backward compat) |
| **WebSocket** | WS /ws | Topic-based pub/sub (primary) |

**Topics:**
- `session:<sessionId>` — events for specific session
- `activity` — global activity_status and token_update events

### 3. **URL Configuration**

Frontend uses `NEXT_PUBLIC_API_BASE_URL` environment variable:

```javascript
// If unset (default): relative URLs
/api/sessions  →  /api/sessions          (standalone mode)

// If set: full URL prefix
/api/sessions  →  http://localhost:3000/api/sessions  (split mode)
```

WebSocket URL automatically converted:
```
http://localhost:3000    →  ws://localhost:3000/ws
https://example.com      →  wss://example.com/ws
(relative path)          →  ws://window.location.host/ws
```

### 4. **Key Request/Response Shapes**

#### Session Creation
```json
POST /api/sessions
{
  "directory": "/abs/path",
  "title": "optional",
  "isolationStrategy": "existing|worktree|clone",
  "context": { optional integration context },
  "initialPrompt": "optional",
  "onComplete": { "notifySessionId": "...", "notifyInstanceId": "..." }
}
→ 200: { instanceId, workspaceId, session }
```

#### Session Prompt (Fire-and-Forget)
```json
POST /api/sessions/[id]/prompt
{
  "instanceId": "...",
  "text": "prompt text",
  "attachments": [ { "mime": "image/png", "data": "base64..." } ]
}
→ 204 No Content
(Results via SSE/WebSocket)
```

#### Session Messages (Paginated)
```
GET /api/sessions/[id]/messages?instanceId=...&limit=50&before=<id>&after=<id>
→ 200: { messages[], pagination: { hasMore, oldestMessageId, totalCount } }
```

#### Session List (Paginated + Filtered)
```
GET /api/sessions?limit=100&offset=0&status=active,idle
→ 200: SessionListItem[]
Headers: X-Total-Count, X-Limit, X-Offset
```

### 5. **Message Types**

**AccumulatedMessage:**
```json
{
  "messageId": "msg_...",
  "sessionId": "ses_...",
  "role": "user|assistant",
  "parts": [ /* TextPart | ToolPart | FilePart */ ],
  "cost": 0.001,
  "tokens": { "input": 100, "output": 50, "reasoning": 20 },
  "createdAt": 1712000000000,
  "agent": "agent_name",
  "modelID": "claude-sonnet-4-5"
}
```

**Part Types:**
```json
{ "partId": "...", "type": "text", "text": "..." }
{ "partId": "...", "type": "tool", "tool": "...", "callId": "...", "state": {...} }
{ "partId": "...", "type": "file", "mime": "image/png", "filename": "...", "url": "data:..." }
```

### 6. **Real-Time Event Types (from OpenCode SDK)**

**Session Events:**
- `message.updated` — new/updated message
- `message.part.updated` — part change
- `message.part.delta` — streaming text
- `session.status` — status change
- `session.idle` — entered idle

**Global Activity:**
- `activity_status` — session busy/idle transition
- `token_update` — token usage aggregation

### 7. **WebSocket Protocol**

Client → Server:
```json
{ "type": "subscribe", "topics": ["session:abc", "activity"] }
{ "type": "unsubscribe", "topics": ["session:abc"] }
```

Server → Client:
```json
{ "type": "event", "topic": "session:abc", "data": {...} }
{ "type": "subscribed", "topics": ["session:abc", "activity"] }
```

### 8. **Error Handling**

```
200 ✓ Success
204 ✓ Success (no body)
400 ✗ Bad request (validation, missing params)
401 ✗ Unauthorized (GitHub not connected)
404 ✗ Not found (session/instance doesn't exist)
409 ✗ Conflict (resume active session)
500 ✗ Server error (SDK failure, crash)
502 ✗ Bad gateway (GitHub API error)
```

Error response: `{ "error": "message" }`

### 9. **CORS Headers (SSE & WebSocket)**

```
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, OPTIONS
Access-Control-Allow-Headers: Content-Type, Authorization
Cache-Control: no-cache, no-transform
Connection: keep-alive
X-Accel-Buffering: no
```

### 10. **React Hooks (Client-Side Streaming)**

**`useSessionEvents(sessionId, instanceId, onAgentSwitch?, suppressAutoScrollRef?)`**
- Returns: messages[], status, sessionStatus, error, forceIdle(), reconnect(), loadOlderMessages(), etc.
- Connects to WebSocket topic `session:${sessionId}`
- Auto-fetches messages on mount
- Gap-fills on reconnect (fetches messages since last ID)
- Local caching with scroll position

**`useActivityStream()`**
- Returns: { on(), off() } API
- Connects to WebSocket topic `activity`
- Emits: activity_status, token_update

**`useWeaveSocket()`**
- Returns: { subscribe() }
- Manages singleton WebSocket connection
- Ref-counted lifecycle (first hook mount → connect, last unmount → disconnect)
- Automatic reconnection with exponential backoff (1s → 30s)

### 11. **Database Requirements**

**Sessions:**
- id, workspace_id, instance_id, opencode_session_id, title, directory, parent_session_id, status, created_at, stopped_at, total_tokens, total_cost

**Workspaces:**
- id, directory, source_directory, isolation_strategy, branch, display_name, created_at

**Instances:**
- id, directory, status, pid, port, created_at

Support pagination with LIMIT/OFFSET and filtering by status.

### 12. **Environment Variables**

**Build-Time:**
```
NEXT_PUBLIC_API_BASE_URL=             (empty = relative, set = full URL)
NEXT_PUBLIC_WEAVE_PROFILE=default
NEXT_PUBLIC_APP_VERSION=0.14.0
NEXT_PUBLIC_COMMIT_SHA=a1b2c3d
```

**Runtime:**
```
WEAVE_PROFILE=default
```

---

## Implementation Checklist for .NET Backend

- [ ] 14 Session endpoints (create, list, get, patch, delete, prompt, command, fork, resume, abort, events SSE, messages, diffs, status)
- [ ] 4 Instance endpoints (agents, commands, models, find/files)
- [ ] 5 Config endpoints (config, version, profile, fleet/summary)
- [ ] 6 Directory/Repo endpoints (directories, repositories, info, detail, refresh, open-directory)
- [ ] 2 Global stream endpoints (activity-stream SSE, /ws WebSocket)
- [ ] 3 Integration endpoints (get, post, delete) + GitHub auth (device-code, poll)
- [ ] 13 GitHub API proxies (repos, issues, pulls, labels, milestones, assignees, bookmarks)
- [ ] 2 Skills endpoints (list, install)
- [ ] 3 Workspace root endpoints (list, add, remove)
- [ ] WebSocket topic-based pub/sub with re-subscription on reconnect
- [ ] SSE keepalive every 15 seconds
- [ ] CORS headers on SSE/WebSocket
- [ ] Message pagination with before/after cursors
- [ ] Session status tracking (lifecycle + activity)
- [ ] Workspace isolation strategies (existing, worktree, clone)
- [ ] Gap-fill on WebSocket reconnect
- [ ] Database persistence (sessions, workspaces, instances)
- [ ] Error handling with appropriate HTTP status codes
- [ ] Support for NEXT_PUBLIC_API_BASE_URL (relative and full URL modes)
- [ ] RFC 8628 GitHub device authorization flow
- [ ] OpenCode SDK integration (proxy calls + event forwarding)

---

## Key Files in Frontend Codebase

**Client Configuration:**
- `src/lib/api-client.ts` — URL builder, fetch wrapper
- `src/lib/api-types.ts` — TypeScript request/response types
- `next.config.ts` — environment variables

**React Hooks:**
- `src/hooks/use-session-events.ts` — main streaming hook
- `src/hooks/use-activity-stream.ts` — global activity
- `src/hooks/use-weave-socket.ts` — WebSocket management

**API Routes (48 endpoints):**
- `src/app/api/sessions/` (14 endpoints)
- `src/app/api/instances/` (4 endpoints)
- `src/app/api/config/` (5 endpoints)
- `src/app/api/repositories/` (6 endpoints)
- `src/app/api/directories/` (1 endpoint)
- `src/app/api/activity-stream/` (1 endpoint)
- `src/app/api/integrations/` (3 + GitHub auth + 13 proxies)
- `src/app/api/skills/` (2 endpoints)
- `src/app/api/workspace-roots/` (3 endpoints)
- `src/app/api/fleet/` (1 endpoint)

---

**The complete specification (2,032 lines) is in `COMPLETE_API_CONTRACT.md`**

This document is your definitive guide to implementing a backend that 100% satisfies the frontend's expectations.
