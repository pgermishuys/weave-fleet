import { NextRequest, NextResponse } from "next/server";
import { removeInstalledSkill } from "@/lib/server/config-manager";

// DELETE /api/skills/[name] — remove an installed skill
export async function DELETE(
  _request: NextRequest,
  { params }: { params: Promise<{ name: string }> }
): Promise<NextResponse> {
  try {
    const { name } = await params;

    if (!name) {
      return NextResponse.json(
        { error: "Skill name is required" },
        { status: 400 }
      );
    }

    removeInstalledSkill(name);

    return NextResponse.json({ ok: true }, { status: 200 });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Unknown error";
    const status = message.includes("not installed") ? 404 : 500;
    return NextResponse.json({ error: message }, { status });
  }
}
