import { NextRequest, NextResponse } from "next/server";
import { deleteWorkspaceRoot } from "@/lib/server/db-repository";

// DELETE /api/workspace-roots/[id] — removes a user-added root
export async function DELETE(
  _request: NextRequest,
  { params }: { params: Promise<{ id: string }> }
): Promise<NextResponse> {
  try {
    const { id } = await params;

    const deleted = deleteWorkspaceRoot(id);
    if (!deleted) {
      return NextResponse.json(
        { error: "Workspace root not found" },
        { status: 404 }
      );
    }

    return NextResponse.json({ ok: true }, { status: 200 });
  } catch (err) {
    console.error("[DELETE /api/workspace-roots/[id]] Error:", err);
    return NextResponse.json(
      { error: "Failed to remove workspace root" },
      { status: 500 }
    );
  }
}
