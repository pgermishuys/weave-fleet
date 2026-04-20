import { computed, toValue, type ComputedRef, type MaybeRefOrGetter } from "vue";
import type { SessionListItem } from "@/lib/api-types";
import {
  deriveDisplayName,
  filterSessionsByWorkspace,
  groupSessionsByWorkspace,
  type WorkspaceGroup,
} from "@/lib/workspace-utils";

export type { WorkspaceGroup } from "@/lib/workspace-utils";
export { deriveDisplayName, filterSessionsByWorkspace, groupSessionsByWorkspace } from "@/lib/workspace-utils";

export function useWorkspaces(
  sessions: MaybeRefOrGetter<readonly SessionListItem[]>,
): ComputedRef<WorkspaceGroup[]> {
  return computed(() => groupSessionsByWorkspace([...toValue(sessions)]));
}
