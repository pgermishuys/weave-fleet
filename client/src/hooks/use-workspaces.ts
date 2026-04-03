"use client";

import { useMemo } from "react";
import type { SessionListItem } from "@/lib/api-types";
import { groupSessionsByWorkspace } from "@/lib/workspace-utils";

// Re-export types and pure functions so existing imports keep working
export type { WorkspaceGroup } from "@/lib/workspace-utils";
export { groupSessionsByWorkspace, filterSessionsByWorkspace, deriveDisplayName } from "@/lib/workspace-utils";

export function useWorkspaces(sessions: SessionListItem[]): ReturnType<typeof groupSessionsByWorkspace> {
  return useMemo(() => groupSessionsByWorkspace(sessions), [sessions]);
}
