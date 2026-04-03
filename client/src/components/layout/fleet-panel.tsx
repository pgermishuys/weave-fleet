"use client";

import { useCallback, useRef } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { LayoutGrid, AlertTriangle, Plus } from "lucide-react";
import { cn } from "@/lib/utils";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { useSessionsContext } from "@/contexts/sessions-context";
import { useWorkspaces } from "@/hooks/use-workspaces";
import { useCurrentSessionDirectory } from "@/hooks/use-current-session-directory";
import { SidebarWorkspaceItem } from "@/components/layout/sidebar-workspace-item";
import { NewSessionDialog } from "@/components/session/new-session-dialog";

export function FleetPanel() {
  const pathname = usePathname();
  const { sessions, error, refetch } = useSessionsContext();
  const workspaces = useWorkspaces(sessions);
  const currentDirectory = useCurrentSessionDirectory();
  const treeRef = useRef<HTMLDivElement>(null);

  const isFleetActive = pathname === "/" || pathname.startsWith("/?");

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
          href="/"
          data-all-sessions
          tabIndex={0}
          className="flex flex-1 items-center gap-1 min-w-0"
        >
          <LayoutGrid className="h-4 w-4 shrink-0" />
          <span className="flex-1 whitespace-nowrap">Fleet</span>
        </Link>
        {/* New Session button */}
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="shrink-0">
              <NewSessionDialog
                defaultDirectory={currentDirectory}
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

      {/* Workspace tree */}
      <div
        ref={treeRef}
        role="tree"
        aria-label="Workspaces"
        onKeyDown={handleTreeKeyDown}
        className="mt-0.5 space-y-0.5"
      >
        {error ? (
          <div className="flex items-center gap-2 pl-3 pr-3 py-1.5 text-xs text-destructive">
            <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
            <span>Failed to load</span>
          </div>
        ) : workspaces.length === 0 ? (
          <p className="pl-3 pr-3 py-1.5 text-xs text-muted-foreground">
            No workspaces yet
          </p>
        ) : (
          workspaces.map((group) => (
            <SidebarWorkspaceItem
              key={group.workspaceDirectory}
              group={group}
              activeSessionPath={pathname}
              refetch={refetch}
            />
          ))
        )}
      </div>
    </nav>
  );
}
