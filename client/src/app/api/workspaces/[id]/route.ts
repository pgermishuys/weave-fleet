import { NextRequest, NextResponse } from "next/server";
import { getWorkspace, updateWorkspaceDisplayName } from "@/lib/server/db-repository";

interface RouteContext {
  params: Promise<{ id: string }>;
}

// PATCH /api/workspaces/[id] — update the display name of a workspace
export async function PATCH(
  request: NextRequest,
  context: RouteContext
): Promise<NextResponse> {
  const { id } = await context.params;

  let body: unknown;
  try {
    body = await request.json();
  } catch {
    return NextResponse.json({ error: "Invalid JSON body" }, { status: 400 });
  }

  if (
    typeof body !== "object" ||
    body === null ||
    !("displayName" in body) ||
    typeof (body as Record<string, unknown>).displayName !== "string" ||
    (body as Record<string, unknown>).displayName === ""
  ) {
    return NextResponse.json(
      { error: "displayName is required and must be a non-empty string" },
      { status: 400 }
    );
  }

  const displayName = (body as Record<string, unknown>).displayName as string;

  let workspace;
  try {
    workspace = getWorkspace(id);
  } catch {
    return NextResponse.json(
      { error: "Failed to look up workspace" },
      { status: 500 }
    );
  }

  if (!workspace) {
    return NextResponse.json({ error: "Workspace not found" }, { status: 404 });
  }

  try {
    updateWorkspaceDisplayName(id, displayName);
  } catch {
    return NextResponse.json(
      { error: "Failed to update workspace display name" },
      { status: 500 }
    );
  }

  return NextResponse.json({ id, displayName }, { status: 200 });
}
