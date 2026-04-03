import { NextRequest, NextResponse } from "next/server";
import {
  listInstalledSkills,
  installSkillFromSource,
} from "@/lib/server/config-manager";

// GET /api/skills — list all installed skills with metadata and agent assignments
export async function GET(): Promise<NextResponse> {
  try {
    const skills = listInstalledSkills();
    return NextResponse.json({ skills }, { status: 200 });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Unknown error";
    return NextResponse.json({ error: message }, { status: 500 });
  }
}

// POST /api/skills — install a skill from URL or content
export async function POST(request: NextRequest): Promise<NextResponse> {
  try {
    const body = await request.json();

    if (!body || typeof body !== "object") {
      return NextResponse.json(
        { error: "Invalid request body" },
        { status: 400 }
      );
    }

    const { url, content, agents } = body as {
      url?: string;
      content?: string;
      agents?: string[];
    };

    if (!url && !content) {
      return NextResponse.json(
        { error: "Either 'url' or 'content' must be provided" },
        { status: 400 }
      );
    }

    const result = await installSkillFromSource({ url, content, agents });

    return NextResponse.json(
      {
        ok: true,
        skill: result,
      },
      { status: 201 }
    );
  } catch (error) {
    const message = error instanceof Error ? error.message : "Unknown error";
    return NextResponse.json({ error: message }, { status: 500 });
  }
}
