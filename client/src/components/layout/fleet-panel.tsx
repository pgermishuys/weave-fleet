
import { useCallback, useRef, useState } from "react";
import { Link } from "react-router";
import { useLocation } from "react-router";
import { LayoutGrid, AlertTriangle, Plus, FolderPlus } from "lucide-react";
import { cn } from "@/lib/utils";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { useSessionsContext } from "@/contexts/sessions-context";
import { useProjects } from "@/hooks/use-projects";
import { useCurrentSessionDirectory } from "@/hooks/use-current-session-directory";
import { SidebarProjectItem } from "@/components/layout/sidebar-project-item";
import { NewSessionDialog } from "@/components/session/new-session-dialog";
import { CreateProjectDialog } from "@/components/fleet/create-project-dialog";
import { groupSessionsByProject } from "@/lib/workspace-utils";

export function FleetPanel() {
  const { pathname } = useLocation();
  const { sessions, error, refetch } = useSessionsContext();
  const { projects, refetch: refetchProjects } = useProjects();
  const currentDirectory = useCurrentSessionDirectory();
  const treeRef = useRef<HTMLDivElement>(null);
  const [newProjectOpen, setNewProjectOpen] = useState(false);

  const isFleetActive = pathname === "/" || pathname.startsWith("/?");

  // Group sessions by project using our utility
  const projectGroups = groupSessionsByProject(sessions, projects);

  // Named (non-null) projects only — used for index/count for reordering
  const namedGroups = projectGroups.filter((g) => g.projectId !== null);

  // User-type projects (not scratch) — passed down to avoid per-session fetches
  const userProjects = projects.filter((p) => p.type !== "scratch");

  const handleProjectCreated = useCallback(() => {
    void refetchProjects();
    void refetch();
  }, [refetchProjects, refetch]);

  const handleTreeKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLDivElement>) => {
      const tree = treeRef.current;
      if (!tree) return;

      const items = Array.from(
        tree.querySelectorAll<HTMLElement>(
          "[role='treeitem'], [data-tree-leaf]"
        )
      );
      const focused = document.activeElement as HTMLElement | null;
      const currentIndex = focused ? items.indexOf(focused) : -1;

      switch (e.key) {
        case "ArrowDown": {
          e.preventDefault();
          const next = items[currentIndex + 1];
          next?.focus();
          break;
        }
        case "ArrowUp": {
          e.preventDefault();
          const prev = items[currentIndex - 1];
          prev?.focus();
          break;
        }
        case "ArrowRight": {
          e.preventDefault();
          if (focused?.getAttribute("role") === "treeitem") {
            const next = items[currentIndex + 1];
            next?.focus();
          }
          break;
        }
        case "ArrowLeft": {
          e.preventDefault();
          if (focused?.getAttribute("role") === "treeitem") {
            const allSessionsRow = tree.querySelector<HTMLElement>(
              "[data-all-sessions]"
            );
            allSessionsRow?.focus();
          }
          break;
        }
        case "Enter": {
          e.preventDefault();
          focused?.click();
          break;
        }
        case "F2": {
          e.preventDefault();
          if (focused?.getAttribute("role") === "treeitem") {
            const renameTrigger = focused.querySelector<HTMLElement>(
              "[data-rename-trigger]"
            );
            renameTrigger?.click();
          }
          break;
        }
      }
    },
    []
  );

  return (
    <nav className="flex-1 overflow-y-auto thin-scrollbar p-2 space-y-1">
      {/* Fleet header row */}
      <div
        className={cn(
          "flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors",
          isFleetActive
            ? "bg-sidebar-accent text-sidebar-accent-foreground"
            : "text-muted-foreground hover:bg-sidebar-accent/50 hover:text-foreground"
        )}
      >
        {/* All Sessions link */}
        <Link
          to="/"
          data-all-sessions
          tabIndex={0}
          className="flex flex-1 items-center gap-1 min-w-0"
        >
          <LayoutGrid className="h-4 w-4 shrink-0" />
          <span className="flex-1 whitespace-nowrap">Fleet</span>
        </Link>

        {/* New Project button */}
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="shrink-0">
              <CreateProjectDialog
                open={newProjectOpen}
                onOpenChange={setNewProjectOpen}
                onCreated={handleProjectCreated}
                trigger={
                  <button
                    className="rounded-md p-1 text-muted-foreground hover:bg-sidebar-accent/50 hover:text-foreground transition-colors"
                  >
                    <FolderPlus className="h-3.5 w-3.5" />
                  </button>
                }
              />
            </span>
          </TooltipTrigger>
          <TooltipContent side="right">New Project</TooltipContent>
        </Tooltip>

        {/* New Session button */}
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="shrink-0">
              <NewSessionDialog
                defaultDirectory={currentDirectory}
                userProjects={userProjects}
                trigger={
                  <button
                    className="rounded-md p-1 text-muted-foreground hover:bg-sidebar-accent/50 hover:text-foreground transition-colors"
                  >
                    <Plus className="h-3.5 w-3.5" />
                  </button>
                }
              />
            </span>
          </TooltipTrigger>
          <TooltipContent side="right">New Session</TooltipContent>
        </Tooltip>
      </div>

      {/* Project tree */}
      <div
        ref={treeRef}
        role="tree"
        aria-label="Projects"
        onKeyDown={handleTreeKeyDown}
        className="mt-0.5 space-y-0.5"
      >
        {error ? (
          <div className="flex items-center gap-2 pl-3 pr-3 py-1.5 text-xs text-destructive">
            <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
            <span>Failed to load</span>
          </div>
        ) : projectGroups.length === 0 ? (
          <p className="pl-3 pr-3 py-1.5 text-xs text-muted-foreground">
            No sessions yet
          </p>
        ) : (
          projectGroups.map((group, idx) => {
            const namedIndex = group.projectId !== null
              ? namedGroups.findIndex((g) => g.projectId === group.projectId)
              : -1;
            return (
              <SidebarProjectItem
                key={group.projectId ?? "ungrouped"}
                group={group}
                activeSessionPath={pathname}
                refetch={refetch}
                refetchProjects={refetchProjects}
                projectIndex={namedIndex}
                projectCount={namedGroups.length}
                userProjects={userProjects}
              />
            );
          })
        )}
      </div>
    </nav>
  );
}
