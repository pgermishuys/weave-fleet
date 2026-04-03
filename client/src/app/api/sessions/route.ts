import { NextRequest, NextResponse } from "next/server";
import { spawnInstance, listInstances, validateDirectory, _recoveryComplete } from "@/lib/server/process-manager";
import { createWorkspace } from "@/lib/server/workspace-manager";
import { insertSession, listSessions, countSessions, getWorkspace, getInstance, getSessionByHarnessId, insertSessionCallback, getSessionIdsWithActiveChildren } from "@/lib/server/db-repository";
import { startMonitoring } from "@/lib/server/callback-monitor";
import { formatContextAsPrompt } from "@/lib/server/context-formatter";
import { compressedJson } from "@/lib/server/compressed-response";
import { randomUUID } from "crypto";
import { log } from "@/lib/server/logger";
import { withTimeout, getSDKCallTimeoutMs } from "@/lib/server/async-utils";
import type {
  CreateSessionRequest,
  CreateSessionResponse,
  SessionListItem,
  SessionActivityStatus,
  SessionLifecycleStatus,
  InstanceStatus,
} from "@/lib/api-types";

// POST /api/sessions — spawn an OpenCode instance (or reuse) and create a session
export async function POST(request: NextRequest): Promise<NextResponse> {
  // Wait for startup recovery before serving
  await _recoveryComplete;

  let body: CreateSessionRequest;
  try {
    body = await request.json();
  } catch (err) {
    log.warn("sessions-route", "Invalid JSON body in POST request", { err });
    return NextResponse.json({ error: "Invalid JSON body" }, { status: 400 });
  }

  const { directory, title, isolationStrategy = "existing", branch, context, initialPrompt } = body;

  if (!directory || typeof directory !== "string") {
    return NextResponse.json(
      { error: "directory is required" },
      { status: 400 }
    );
  }

  let resolvedDir: string;
  try {
    resolvedDir = validateDirectory(directory);
  } catch (err) {
    const message = err instanceof Error ? err.message : "Invalid directory";
    return NextResponse.json({ error: message }, { status: 400 });
  }

  try {
    // Step 1: Create workspace record (isolation strategy applied here)
    const workspace = await createWorkspace({
      sourceDirectory: resolvedDir,
      strategy: isolationStrategy,
      branch,
    });

    // Step 2: Spawn (or reuse) the OpenCode instance for the workspace directory
    const instance = await spawnInstance(workspace.directory);

    // Step 3: Create the session in OpenCode
    const sessionTitle = title ?? context?.title ?? "New Session";
    const result = await withTimeout(
      instance.client.session.create({ title: sessionTitle }),
      getSDKCallTimeoutMs(),
      `session.create for instance ${instance.id}`,
    );

    const session = result.data;
    if (!session) {
      return NextResponse.json(
        { error: "Failed to create session — SDK returned no data" },
        { status: 500 }
      );
    }

    // Step 4: Persist session to DB
    const sessionDbId = randomUUID();

    // Resolve parent session ID if onComplete is provided
    let parentDbSessionId: string | null = null;
    if (body.onComplete?.notifySessionId && body.onComplete?.notifyInstanceId) {
      try {
        const targetDbSession = getSessionByHarnessId(body.onComplete.notifySessionId);
        if (targetDbSession) {
          parentDbSessionId = targetDbSession.id;
        } else {
          log.warn("sessions-route", "Callback target session not found", { notifySessionId: body.onComplete.notifySessionId });
        }
      } catch (err) {
        log.warn("sessions-route", "Failed to resolve parent session for callback", { notifySessionId: body.onComplete?.notifySessionId, err });
      }
    }

    try {
      insertSession({
        id: sessionDbId,
        workspace_id: workspace.id,
        instance_id: instance.id,
        opencode_session_id: session.id,
        title: session.title ?? title ?? "New Session",
        directory: workspace.directory,
        parent_session_id: parentDbSessionId,
      });
    } catch (err) {
      log.warn("sessions-route", "Failed to persist session to DB — running in-memory only", { sessionId: sessionDbId, err });
    }

    // Step 5: Register completion callback if requested
    if (parentDbSessionId && body.onComplete?.notifyInstanceId) {
      try {
        insertSessionCallback({
          id: randomUUID(),
          source_session_id: sessionDbId,
          target_session_id: parentDbSessionId,
          target_instance_id: body.onComplete.notifyInstanceId,
        });
      } catch (err) {
        log.warn("sessions-route", "Failed to register session completion callback", { sessionId: sessionDbId, err });
      }

      // Start server-side monitoring so callback fires without browser SSE
      try {
        startMonitoring(sessionDbId, session.id, instance.id);
      } catch (err) {
        log.warn("sessions-route", "Failed to start callback monitoring for child session", { sessionId: sessionDbId, err });
      }
    }

    // Step 6: Send initial prompt if context or initialPrompt is provided (fire-and-forget)
    const promptText = initialPrompt ?? (context ? formatContextAsPrompt(context) : null);
    if (promptText && session.id) {
      instance.client.session.promptAsync({
        sessionID: session.id,
        parts: [{ type: "text", text: promptText }],
      }).catch((err: unknown) => {
        log.warn("sessions-route", "Failed to send initial context prompt — session still created", { sessionId: session.id, err });
      });
    }

    const response: CreateSessionResponse = {
      instanceId: instance.id,
      workspaceId: workspace.id,
      session,
    };
    return NextResponse.json(response, { status: 200 });
  } catch (err) {
    log.error("sessions-route", "Failed to create session", { err });
    return NextResponse.json(
      { error: "Failed to create session" },
      { status: 500 }
    );
  }
}

// GET /api/sessions — list all sessions (DB + live state merged)
export async function GET(request: NextRequest): Promise<NextResponse> {
  // Wait for startup recovery before serving
  await _recoveryComplete;

  // Parse pagination query parameters
  const url = request.nextUrl;
  const limitParam = url.searchParams.get("limit");
  const offsetParam = url.searchParams.get("offset");
  const statusParam = url.searchParams.get("status");

  const limit = limitParam !== null ? Math.max(0, parseInt(limitParam, 10) || 100) : 100;
  const offset = offsetParam !== null ? Math.max(0, parseInt(offsetParam, 10) || 0) : 0;
  const statuses = statusParam
    ? statusParam.split(",").filter(Boolean) as ReturnType<typeof listSessions>[number]["status"][]
    : undefined;

  const liveInstances = listInstances();
  const liveInstanceMap = new Map(liveInstances.map((i) => [i.id, i]));

  // Load sessions from DB with pagination/filtering
  let dbSessions: ReturnType<typeof listSessions>;
  try {
    dbSessions = listSessions({ limit, offset, statuses });
  } catch (err) {
    log.warn("sessions-route", "DB unavailable — falling back to live-only session listing", { err });
    const items: SessionListItem[] = [];
    await Promise.allSettled(
      liveInstances.map(async (instance) => {
        try {
          const result = await withTimeout(
            instance.client.session.list(),
            getSDKCallTimeoutMs(),
            `session.list for instance ${instance.id}`,
          );
          const sessions = result.data ?? [];
          const isRunning = instance.status === "running";
          for (const session of sessions) {
            const legacyStatus = isRunning ? "active" : "stopped";
            items.push({
              instanceId: instance.id,
              workspaceId: "",
              workspaceDirectory: instance.directory,
              workspaceDisplayName: null,
              isolationStrategy: "existing",
              sourceDirectory: null,
              branch: null,
              sessionStatus: legacyStatus,
              session,
              instanceStatus: instance.status,
              activityStatus: deriveActivityStatus(legacyStatus),
              lifecycleStatus: deriveLifecycleStatus(legacyStatus),
              typedInstanceStatus: isRunning ? "running" : "stopped",
            });
          }
        } catch (err) {
          log.warn("sessions-route", "Failed to list sessions from live instance during DB-unavailable fallback", { instanceId: instance.id, err });
        }
      })
    );
    return compressedJson(request, items);
  }

  const items: SessionListItem[] = [];

  // Session statuses are kept up-to-date in the DB by the session-status-watcher
  // (which listens to SSE events in real-time). No SDK HTTP calls needed here.

  // Batch-fetch which sessions have active children (for parent status override)
  let parentIdsWithActiveChildren: Set<string>;
  try {
    parentIdsWithActiveChildren = getSessionIdsWithActiveChildren();
  } catch (err) {
    log.warn("sessions-route", "Failed to query active children — skipping parent override", { err });
    parentIdsWithActiveChildren = new Set();
  }

  // ── Build session list items from DB + live status ──────────────────────
  for (const dbSession of dbSessions) {
    // Determine the live instance state
    const liveInstance = liveInstanceMap.get(dbSession.instance_id);

    let instanceStatus: "running" | "dead" = "dead";
    let sessionStatus: SessionListItem["sessionStatus"];

    if (liveInstance) {
      instanceStatus = liveInstance.status;
      if (liveInstance.status === "running") {
        // Use DB status (kept in sync by session-status-watcher via SSE events)
        const TERMINAL_STATUSES = ["stopped", "completed", "error", "disconnected"];
        if (TERMINAL_STATUSES.includes(dbSession.status)) {
          sessionStatus = dbSession.status;
        } else if (dbSession.status === "idle") {
          sessionStatus = "idle";
        } else if (dbSession.status === "waiting_input") {
          sessionStatus = "waiting_input";
        } else {
          sessionStatus = "active";
        }

        // Override idle parents to active if they have busy children
        if (
          sessionStatus === "idle" &&
          dbSession.id &&
          parentIdsWithActiveChildren.has(dbSession.id)
        ) {
          sessionStatus = "active";
        }
      } else {
        sessionStatus = "stopped";
      }
    } else {
      // Not in live map — check DB instance status
      let dbInst: ReturnType<typeof getInstance>;
      try {
        dbInst = getInstance(dbSession.instance_id);
      } catch (err) {
        log.warn("sessions-route", "Failed to look up DB instance for session", { instanceId: dbSession.instance_id, err });
        dbInst = undefined;
      }
      if (dbInst?.status === "running") {
        // DB says running but not in live Map → orphan / disconnected
        sessionStatus = "disconnected";
      } else {
        // Instance is dead — map DB status to appropriate session status
        if (dbSession.status === "completed") {
          sessionStatus = "completed";
        } else if (dbSession.status === "idle") {
          // Was idle when instance died → naturally completed
          sessionStatus = "completed";
        } else if (dbSession.status === "stopped") {
          sessionStatus = "stopped";
        } else if (dbSession.status === "disconnected") {
          // Instance is dead and session was "disconnected" (e.g. from a previous
          // graceful shutdown). Since the instance is gone, the session is stopped.
          sessionStatus = "stopped";
        } else if (dbSession.status === "error") {
          sessionStatus = "error";
        } else {
          // active session with dead instance → disconnected
          sessionStatus = "disconnected";
        }
      }
    }

    // Get workspace info
    let workspaceDirectory = dbSession.directory;
    let isolationStrategy: string = "existing";
    let workspaceDisplayName: string | null = null;
    let sourceDirectory: string | null = null;
    let branch: string | null = null;
    try {
      const ws = getWorkspace(dbSession.workspace_id);
      if (ws) {
        workspaceDirectory = ws.directory;
        isolationStrategy = ws.isolation_strategy;
        workspaceDisplayName = ws.display_name;
        sourceDirectory = ws.source_directory;
        branch = ws.branch;
      }
    } catch (err) {
      log.warn("sessions-route", "Failed to fetch workspace info from DB", { workspaceId: dbSession.workspace_id, err });
    }

    // ── All sessions use DB-only stubs (no live session.get() calls) ────────
    // The sidebar only needs id, title, status, and timestamps — all available
    // from the DB. Eliminating the session.get() round-trip for every live
    // session removes the most expensive part of the polling cycle.
    items.push({
      instanceId: dbSession.instance_id,
      workspaceId: dbSession.workspace_id,
      workspaceDirectory,
      workspaceDisplayName,
      isolationStrategy,
      sourceDirectory,
      branch,
      sessionStatus,
      session: {
        id: dbSession.opencode_session_id,
        title: dbSession.title,
        directory: dbSession.directory,
        projectID: "",
        version: "0",
        time: {
          created: new Date(dbSession.created_at).getTime(),
          updated: new Date(dbSession.stopped_at ?? dbSession.created_at).getTime(),
        },
      } as Parameters<typeof items.push>[0]["session"],
      instanceStatus,
      dbId: dbSession.id,
      parentSessionId: dbSession.parent_session_id,
      activityStatus: deriveActivityStatus(sessionStatus),
      lifecycleStatus: deriveLifecycleStatus(sessionStatus),
      typedInstanceStatus: instanceStatus === "running" ? "running" : "stopped",
      totalTokens: dbSession.total_tokens || undefined,
      totalCost: dbSession.total_cost || undefined,
    });
  }

  const total = countSessions(statuses);
  return compressedJson(request, items, {
    headers: {
      "X-Total-Count": String(total),
      "X-Limit": String(limit),
      "X-Offset": String(offset),
    },
  });
}

// ─── Status derivation helpers ────────────────────────────────────────────────

function deriveActivityStatus(
  sessionStatus: SessionListItem["sessionStatus"]
): SessionActivityStatus | null {
  switch (sessionStatus) {
    case "active":
      return "busy";
    case "idle":
      return "idle";
    case "waiting_input":
      return "waiting_input";
    default:
      return null;
  }
}

function deriveLifecycleStatus(
  sessionStatus: SessionListItem["sessionStatus"]
): SessionLifecycleStatus {
    switch (sessionStatus) {
    case "active":
    case "idle":
    case "waiting_input":
      return "running";
    case "disconnected":
      return "disconnected";
    case "completed":
      return "completed";
    case "stopped":
      return "stopped";
    case "error":
      return "error";
  }
}
