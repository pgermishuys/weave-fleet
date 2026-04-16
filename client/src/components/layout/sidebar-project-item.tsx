
import React, { useState, useCallback } from "react";
import { ChevronRight, FolderOpen, Pencil, Trash2, ArrowUp, ArrowDown, Plus } from "lucide-react";
import { cn } from "@/lib/utils";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";
import { InlineEdit } from "@/components/ui/inline-edit";
import { SidebarWorkspaceItem } from "@/components/layout/sidebar-workspace-item";
import { ConfirmDeleteProjectDialog } from "@/components/fleet/confirm-delete-project-dialog";
import { NewSessionDialog } from "@/components/session/new-session-dialog";
import { usePersistedState } from "@/hooks/use-persisted-state";
import { useUpdateProject } from "@/hooks/use-update-project";
import { useDeleteProject } from "@/hooks/use-delete-project";
import { useReorderProject } from "@/hooks/use-reorder-project";
import type { DeleteProjectMode } from "@/hooks/use-delete-project";
import type { ProjectGroup } from "@/lib/workspace-utils";
import type { ProjectResponse } from "@/lib/api-types";

const PROJECT_COLLAPSED_KEY = "weave:sidebar:project-collapsed";

interface SidebarProjectItemProps {
  group: ProjectGroup;
  activeSessionPath: string;
  refetch: () => void;
  refetchProjects?: () => void;
  projectIndex?: number;
  projectCount?: number;
  userProjects?: ProjectResponse[];
}

export const SidebarProjectItem = React.memo(function SidebarProjectItem({
  group,
  activeSessionPath,
  refetch,
  refetchProjects,
  projectIndex = -1,
  projectCount = 0,
  userProjects = [],
}: SidebarProjectItemProps) {
  const projectKey = group.projectId ?? "ungrouped";
  const isUngrouped = group.projectId === null;

  const [collapsedIds, setCollapsedIds] = usePersistedState<string[]>(
    PROJECT_COLLAPSED_KEY,
    []
  );
  const isCollapsed = collapsedIds.includes(projectKey);

  const [isRenaming, setIsRenaming] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showNewSession, setShowNewSession] = useState(false);

  const { updateProject } = useUpdateProject();
  const { deleteProject, isDeleting } = useDeleteProject();
  const { reorderProject } = useReorderProject();

  const handleToggle = useCallback(() => {
    setCollapsedIds((prev) =>
      prev.includes(projectKey)
        ? prev.filter((id) => id !== projectKey)
        : [...prev, projectKey]
    );
  }, [projectKey, setCollapsedIds]);

  const totalSessions = group.workspaces.reduce(
    (sum, ws) => sum + ws.sessionCount,
    0
  );

  const handleRename = useCallback(
    async (newName: string) => {
      if (!group.projectId || !newName.trim()) return;
      try {
        await updateProject(group.projectId, { name: newName.trim() });
        refetchProjects?.();
        refetch();
      } catch {
        // error surfaced inside useUpdateProject
      }
    },
    [group.projectId, updateProject, refetchProjects, refetch]
  );

  const handleDeleteConfirm = useCallback(
    async (mode: DeleteProjectMode) => {
      if (!group.projectId) return;
      try {
        await deleteProject(group.projectId, mode);
        refetch();
        refetchProjects?.();
      } catch {
        // error surfaced inside useDeleteProject
      } finally {
        setShowDeleteConfirm(false);
      }
    },
    [group.projectId, deleteProject, refetch, refetchProjects]
  );

  const handleMoveUp = useCallback(async () => {
    if (!group.projectId || projectIndex <= 0) return;
    try {
      await reorderProject(group.projectId, projectIndex - 1);
      refetchProjects?.();
    } catch {
      // error surfaced inside useReorderProject
    }
  }, [group.projectId, projectIndex, reorderProject, refetchProjects]);

  const handleMoveDown = useCallback(async () => {
    if (!group.projectId || projectIndex >= projectCount - 1) return;
    try {
      await reorderProject(group.projectId, projectIndex + 1);
      refetchProjects?.();
    } catch {
      // error surfaced inside useReorderProject
    }
  }, [group.projectId, projectIndex, projectCount, reorderProject, refetchProjects]);

  const headerContent = (
    <div
      role="treeitem"
      tabIndex={0}
      aria-label={group.projectName}
      aria-expanded={!isCollapsed}
      data-project-id={group.projectId ?? undefined}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          handleToggle();
        }
        if (e.key === "F2" && !isUngrouped) {
          e.preventDefault();
          setIsRenaming(true);
        }
      }}
      className={cn(
        "group/header flex items-center gap-1.5 rounded-md px-2 py-1 cursor-pointer select-none",
        "text-xs font-medium transition-colors",
        isUngrouped
          ? "text-muted-foreground hover:bg-sidebar-accent/50 hover:text-foreground"
          : "text-foreground/80 hover:bg-sidebar-accent/50 hover:text-foreground",
        "focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
      )}
    >
      {/* Chevron */}
      <ChevronRight
        className={cn(
          "h-3 w-3 shrink-0 transition-transform duration-150",
          !isCollapsed && "rotate-90"
        )}
      />

      {/* Folder icon — only for named projects */}
      {!isUngrouped && (
        <FolderOpen className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
      )}

      {/* Project name — InlineEdit for named projects, static span for ungrouped */}
      {isUngrouped ? (
        <span className="flex-1 truncate">{group.projectName}</span>
      ) : (
        <>
          <InlineEdit
            value={group.projectName}
            onSave={handleRename}
            editing={isRenaming}
            onEditingChange={setIsRenaming}
            className="text-xs truncate block flex-1"
          />
          {/* Hidden trigger for F2 rename */}
          <button
            data-rename-trigger
            className="sr-only"
            tabIndex={-1}
            onClick={() => setIsRenaming(true)}
            aria-label={`Rename ${group.projectName}`}
          />
        </>
      )}

      {/* Session count badge */}
      <span className="ml-auto shrink-0 text-[10px] tabular-nums text-muted-foreground">
        {totalSessions}
      </span>
    </div>
  );

  return (
    <>
      <Collapsible open={!isCollapsed} onOpenChange={() => handleToggle()}>
        {/* Project header row — wrapped in ContextMenu for named projects */}
        {isUngrouped ? (
          <CollapsibleTrigger asChild>
            {headerContent}
          </CollapsibleTrigger>
        ) : (
          <ContextMenu>
            <ContextMenuTrigger asChild>
              <CollapsibleTrigger asChild>
                {headerContent}
              </CollapsibleTrigger>
            </ContextMenuTrigger>
            <ContextMenuContent>
              <ContextMenuItem
                onClick={() => setShowNewSession(true)}
                className="gap-2 text-xs"
              >
                <Plus className="h-3.5 w-3.5" />
                New Session
              </ContextMenuItem>

              <ContextMenuSeparator />

              <ContextMenuItem
                onClick={() => setIsRenaming(true)}
                className="gap-2 text-xs"
              >
                <Pencil className="h-3.5 w-3.5" />
                Rename
              </ContextMenuItem>

              <ContextMenuSeparator />

              <ContextMenuItem
                onClick={handleMoveUp}
                disabled={projectIndex <= 0}
                className="gap-2 text-xs"
              >
                <ArrowUp className="h-3.5 w-3.5" />
                Move Up
              </ContextMenuItem>
              <ContextMenuItem
                onClick={handleMoveDown}
                disabled={projectIndex >= projectCount - 1}
                className="gap-2 text-xs"
              >
                <ArrowDown className="h-3.5 w-3.5" />
                Move Down
              </ContextMenuItem>

              <ContextMenuSeparator />

              <ContextMenuItem
                onClick={() => setShowDeleteConfirm(true)}
                variant="destructive"
                className="gap-2 text-xs"
              >
                <Trash2 className="h-3.5 w-3.5" />
                Delete
              </ContextMenuItem>
            </ContextMenuContent>
          </ContextMenu>
        )}

        {/* Workspace children — indented under the project header */}
        <CollapsibleContent>
          <div className="pl-2 space-y-0.5 mt-0.5">
            {group.workspaces.map((workspace) => (
              <SidebarWorkspaceItem
                key={workspace.workspaceDirectory}
                group={workspace}
                activeSessionPath={activeSessionPath}
                refetch={refetch}
                userProjects={userProjects}
              />
            ))}
          </div>
        </CollapsibleContent>
      </Collapsible>

      {/* Delete confirmation dialog — rendered outside the Collapsible */}
      {!isUngrouped && (
        <ConfirmDeleteProjectDialog
          open={showDeleteConfirm}
          onOpenChange={setShowDeleteConfirm}
          projectName={group.projectName}
          onConfirm={handleDeleteConfirm}
          isDeleting={isDeleting}
        />
      )}

      {/* New Session dialog — rendered outside the Collapsible */}
      {!isUngrouped && (
        <NewSessionDialog
          open={showNewSession}
          onOpenChange={setShowNewSession}
          initialProjectId={group.projectId ?? undefined}
          userProjects={userProjects}
        />
      )}
    </>
  );
});
