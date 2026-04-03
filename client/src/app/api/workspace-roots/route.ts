import { NextRequest, NextResponse } from "next/server";
import { existsSync, statSync, realpathSync } from "fs";
import { isAbsolute, resolve } from "path";
import { randomUUID } from "crypto";
import {
  getEnvRoots,
  getAllowedRoots,
} from "@/lib/server/process-manager";
import {
  listWorkspaceRoots,
  insertWorkspaceRoot,
  getWorkspaceRootByPath,
} from "@/lib/server/db-repository";
import type {
  WorkspaceRootItem,
  WorkspaceRootsResponse,
  AddWorkspaceRootResponse,
} from "@/lib/api-types";

// GET /api/workspace-roots — returns all roots with source metadata
export async function GET(): Promise<NextResponse> {
  try {
    const envRoots = new Set(getEnvRoots());
    const dbRoots = listWorkspaceRoots();

    const seen = new Set<string>();
    const items: WorkspaceRootItem[] = [];

    // Add env roots first
    for (const path of envRoots) {
      const resolved = resolve(path);
      if (seen.has(resolved)) continue;
      seen.add(resolved);
      items.push({
        id: null,
        path: resolved,
        source: "env",
        exists: existsSync(resolved) && statSync(resolved).isDirectory(),
      });
    }

    // Add DB roots (skip duplicates with env roots)
    for (const dbRoot of dbRoots) {
      const resolved = resolve(dbRoot.path);
      if (seen.has(resolved)) continue;
      seen.add(resolved);
      items.push({
        id: dbRoot.id,
        path: resolved,
        source: "user",
        exists: existsSync(resolved) && statSync(resolved).isDirectory(),
      });
    }

    const response: WorkspaceRootsResponse = { roots: items };
    return NextResponse.json(response, { status: 200 });
  } catch (err) {
    console.error("[GET /api/workspace-roots] Error:", err);
    return NextResponse.json(
      { error: "Failed to list workspace roots" },
      { status: 500 }
    );
  }
}

// POST /api/workspace-roots — adds a new user root
export async function POST(request: NextRequest): Promise<NextResponse> {
  try {
    const body = await request.json();
    const rawPath = body?.path;

    // 1. Must be a non-empty string
    if (!rawPath || typeof rawPath !== "string") {
      return NextResponse.json(
        { error: "Path is required" },
        { status: 400 }
      );
    }

    // 2. Must be absolute
    if (!isAbsolute(rawPath)) {
      return NextResponse.json(
        { error: "Path must be absolute" },
        { status: 400 }
      );
    }

    // 3. Must exist on the filesystem
    if (!existsSync(rawPath)) {
      return NextResponse.json(
        { error: "Directory does not exist" },
        { status: 400 }
      );
    }

    // 4. Must be a directory
    try {
      if (!statSync(rawPath).isDirectory()) {
        return NextResponse.json(
          { error: "Path exists but is not a directory" },
          { status: 400 }
        );
      }
    } catch {
      return NextResponse.json(
        { error: "Cannot access path" },
        { status: 400 }
      );
    }

    // 5. SECURITY: normalize traversal segments (e.g. /home/../etc → /etc)
    const resolved = resolve(rawPath);

    // 6. SECURITY: resolve symlinks to prevent symlink-based escapes
    let realPath: string;
    try {
      realPath = realpathSync(resolved);
    } catch {
      return NextResponse.json(
        { error: "Cannot resolve path" },
        { status: 400 }
      );
    }

    // 7. Check for duplicates against all existing roots (env + DB)
    const allRoots = getAllowedRoots();
    const isDuplicate = allRoots.some((root) => resolve(root) === realPath);
    if (isDuplicate) {
      return NextResponse.json(
        { error: "Root already exists" },
        { status: 409 }
      );
    }

    // Also check if the realPath differs from resolved (symlink) and the DB already has it
    const existingByPath = getWorkspaceRootByPath(realPath);
    if (existingByPath) {
      return NextResponse.json(
        { error: "Root already exists" },
        { status: 409 }
      );
    }

    // 8. Store the resolved real path, NOT the user-submitted path
    const id = randomUUID();
    insertWorkspaceRoot({ id, path: realPath });

    const response: AddWorkspaceRootResponse = { id, path: realPath };
    return NextResponse.json(response, { status: 201 });
  } catch (err) {
    console.error("[POST /api/workspace-roots] Error:", err);
    return NextResponse.json(
      { error: "Failed to add workspace root" },
      { status: 500 }
    );
  }
}
