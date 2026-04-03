import { NextRequest, NextResponse } from "next/server";
import { spawnInstance, _recoveryComplete } from "@/lib/server/process-manager";
import { createWorkspace } from "@/lib/server/workspace-manager";
import {
  getSession,
  getSessionByHarnessId,
  getWorkspace,
  insertSession,
} from "@/lib/server/db-repository";
import { randomUUID } from "crypto";
import { log } from "@/lib/server/logger";
import type { ForkSessionRequest, ForkSessionResponse } from "@/lib/api-types";
import { withTimeout, getSDKCallTimeoutMs } from "@/lib/server/async-utils";

interface RouteContext {
  params: Promise<{ id: string }>;
}

// POST /api/sessions/[id]/fork — create a new session in the same workspace as an existing session
export async function POST(
  request: NextRequest,
  context: RouteContext
): Promise<NextResponse> {
  // Wait for startup recovery before serving
  await _recoveryComplete;

  const { id: sessionId } = await context.params;

  // Step 1: Parse optional body (title)
  let body: ForkSessionRequest = {};
  try {
    const text = await request.text();
    if (text.trim()) {
      body = JSON.parse(text) as ForkSessionRequest;
    }
  } catch (err) {
    log.warn("fork-route", "Invalid JSON body in POST request", { err });
    return NextResponse.json({ error: "Invalid JSON body" }, { status: 400 });
  }

  // Step 2: Resolve the source session from DB (accept Fleet DB id or opencode session id)
  let dbSession: ReturnType<typeof getSession>;
  try {
    dbSession = getSession(sessionId) ?? getSessionByHarnessId(sessionId);
  } catch (err) {
    log.warn("fork-route", "DB lookup failed for source session", { sessionId, err });
    return NextResponse.json(
      { error: "Failed to look up source session" },
      { status: 500 }
    );
  }

  if (!dbSession) {
    return NextResponse.json(
      { error: "Source session not found" },
      { status: 404 }
    );
  }

  // Step 3: Get the workspace to determine the source directory and isolation strategy
  let workspace: ReturnType<typeof getWorkspace>;
  try {
    workspace = getWorkspace(dbSession.workspace_id);
  } catch (err) {
    log.warn("fork-route", "DB lookup failed for workspace", { workspaceId: dbSession.workspace_id, err });
    return NextResponse.json(
      { error: "Failed to look up source session workspace" },
      { status: 500 }
    );
  }

  if (!workspace) {
    return NextResponse.json(
      { error: "Source session workspace not found" },
      { status: 404 }
    );
  }

  // Use the source_directory (original repo) for worktree/clone strategies,
  // or the workspace directory for "existing" strategy.
  const sourceDirectory = workspace.source_directory ?? workspace.directory;

  try {
    // Step 4: Create a new workspace using the "existing" strategy on the same directory.
    // Forking always uses "existing" strategy — the new session shares the same workspace.
    const newWorkspace = await createWorkspace({
      sourceDirectory,
      strategy: "existing",
    });

    // Step 5: Spawn (or reuse) the OpenCode instance for the workspace directory
    const instance = await spawnInstance(newWorkspace.directory);

    // Step 6: Create the session in OpenCode
    const title = body.title?.trim() || "New Session";
    const result = await withTimeout(
      instance.client.session.create({ title }),
      getSDKCallTimeoutMs(),
      `session.create for fork in instance ${instance.id}`,
    );

    const session = result.data;
    if (!session) {
      return NextResponse.json(
        { error: "Failed to create session — SDK returned no data" },
        { status: 500 }
      );
    }

     // Step 7: Persist the new session to DB — forked sessions are independent, not children
     const sessionDbId = randomUUID();
     try {
       insertSession({
         id: sessionDbId,
         workspace_id: newWorkspace.id,
         instance_id: instance.id,
         opencode_session_id: session.id,
         title: session.title ?? title,
         directory: newWorkspace.directory,
         parent_session_id: null,
       });
    } catch (err) {
      log.warn("fork-route", "Failed to persist forked session to DB — running in-memory only", {
        sessionId: sessionDbId,
        err,
      });
    }

    const response: ForkSessionResponse = {
      instanceId: instance.id,
      workspaceId: newWorkspace.id,
      session,
      forkedFromSessionId: sessionId,
    };
    return NextResponse.json(response, { status: 200 });
  } catch (err) {
    log.error("fork-route", "Failed to fork session", { sessionId, err });
    return NextResponse.json(
      { error: "Failed to fork session" },
      { status: 500 }
    );
  }
}
