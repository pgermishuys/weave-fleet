import { NextRequest, NextResponse } from "next/server";
import { spawnInstance, validateDirectory, _recoveryComplete } from "@/lib/server/process-manager";
import {
  getSession,
  getSessionByHarnessId,
  getWorkspace,
  updateSessionForResume,
} from "@/lib/server/db-repository";
import type { ResumeSessionResponse } from "@/lib/api-types";
import { existsSync } from "fs";
import { withTimeout, getSDKCallTimeoutMs } from "@/lib/server/async-utils";

interface RouteContext {
  params: Promise<{ id: string }>;
}

// POST /api/sessions/[id]/resume — resume a disconnected or stopped session
export async function POST(
  _request: NextRequest,
  context: RouteContext
): Promise<NextResponse> {
  // Wait for startup recovery before serving
  await _recoveryComplete;

  const { id: sessionId } = await context.params;

  // Step 1: Resolve session from DB — accept fleet DB id or opencode session id
  const dbSession = getSession(sessionId) ?? getSessionByHarnessId(sessionId);
  if (!dbSession) {
    return NextResponse.json({ error: "Session not found" }, { status: 404 });
  }

  // Step 2: Only disconnected or stopped sessions can be resumed
  const resumableStatuses = new Set(["disconnected", "stopped", "completed"]);
  if (!resumableStatuses.has(dbSession.status)) {
    return NextResponse.json(
      { error: "Session is already active — cannot resume a running session" },
      { status: 409 }
    );
  }

  // Step 3: Get the workspace to find the directory
  const workspace = getWorkspace(dbSession.workspace_id);
  if (!workspace) {
    return NextResponse.json(
      { error: "Workspace not found for this session" },
      { status: 400 }
    );
  }

  // Step 4: Validate directory is within allowed roots before touching the filesystem
  try {
    validateDirectory(workspace.directory);
  } catch (err) {
    const message = err instanceof Error ? err.message : "Invalid directory";
    return NextResponse.json({ error: message }, { status: 400 });
  }

  // Step 4b: Check the directory still exists on disk
  if (!existsSync(workspace.directory)) {
    return NextResponse.json(
      { error: "Workspace directory no longer exists — it may have been cleaned up" },
      { status: 400 }
    );
  }

  // Step 5: Spawn (or reuse) an opencode instance for the workspace directory
  let instance;
  try {
    instance = await spawnInstance(workspace.directory);
  } catch (err) {
    console.error(`[POST /api/sessions/${sessionId}/resume] Failed to spawn instance:`, err);
    return NextResponse.json(
      { error: "Failed to start opencode instance" },
      { status: 500 }
    );
  }

  // Step 6: Verify the opencode session still exists in this instance's DB
  let sdkSession;
  try {
    const result = await withTimeout(
      instance.client.session.get({ sessionID: dbSession.opencode_session_id }),
      getSDKCallTimeoutMs(),
      `session.get for ${dbSession.opencode_session_id}`,
    );
    sdkSession = result.data;
  } catch {
    sdkSession = null;
  }

  if (!sdkSession) {
    return NextResponse.json(
      { error: "Session no longer exists in opencode — it may have been deleted" },
      { status: 404 }
    );
  }

  // Step 7: Update the DB session to point at the new instance and mark active
  try {
    updateSessionForResume(dbSession.id, instance.id);
  } catch (err) {
    console.warn(`[POST /api/sessions/${sessionId}/resume] Failed to update DB:`, err);
    // Non-fatal — session is functional even if DB update fails.
    // The periodic health poll will reconcile instance_id and status on the next cycle.
  }

  const response: ResumeSessionResponse = {
    instanceId: instance.id,
    session: sdkSession,
  };
  return NextResponse.json(response, { status: 200 });
}
