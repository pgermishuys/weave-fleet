"use client";

import { useState, useMemo } from "react";
import { Pencil, ChevronRight, ChevronDown, Loader2, Check, X } from "lucide-react";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import { Badge } from "@/components/ui/badge";
import type { AccumulatedPart } from "@/lib/api-types";
import { shortenPath } from "@/lib/tool-labels";

interface EditToolCardProps {
  part: AccumulatedPart & { type: "tool" };
}

/** Compute a simple line-level diff summary from old/new strings. */
function computeDiffSummary(oldStr: string, newStr: string): { added: number; removed: number } {
  const oldLines = oldStr.split("\n");
  const newLines = newStr.split("\n");
  return {
    removed: oldLines.length,
    added: newLines.length,
  };
}

export function EditToolCard({ part }: EditToolCardProps) {
  const [open, setOpen] = useState(false);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const state = part.state as any;
  const isRunning = state?.status === "running" || !state?.status;
  const isCompleted = state?.status === "completed";
  const isError = state?.status === "error";

  const input = state?.input as {
    filePath?: string;
    oldString?: string;
    newString?: string;
    replaceAll?: boolean;
  } | undefined;
  const error: string = state?.error ? String(state.error) : "";

  const filePath = input?.filePath ?? "unknown";
  const oldString = input?.oldString ?? "";
  const newString = input?.newString ?? "";
  const replaceAll = input?.replaceAll ?? false;
  const shortPath = shortenPath(filePath);

  const diff = useMemo(() => computeDiffSummary(oldString, newString), [oldString, newString]);

  const diffLabel = `+${diff.added} -${diff.removed}`;

  const hasExpandableContent = !isRunning && (oldString || newString || error);

  // Split old and new into lines for diff rendering
  const oldLines = useMemo(() => oldString.split("\n"), [oldString]);
  const newLines = useMemo(() => newString.split("\n"), [newString]);

  return (
    <Collapsible open={open} onOpenChange={setOpen}>
      <CollapsibleTrigger asChild disabled={!hasExpandableContent}>
        <button
          type="button"
          className="flex w-full items-center gap-2 py-0.5 text-xs text-muted-foreground text-left hover:bg-accent/30 rounded-sm px-1 -mx-1 transition-colors disabled:hover:bg-transparent disabled:cursor-default"
        >
          {hasExpandableContent ? (
            open ? (
              <ChevronDown className="h-3 w-3 shrink-0 text-muted-foreground/60" />
            ) : (
              <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground/60" />
            )
          ) : (
            <span className="inline-block h-3 w-3 shrink-0" />
          )}

          <Pencil className="h-3 w-3 shrink-0 text-amber-400" />
          <span className="flex-1 truncate">{shortPath}</span>
          {(oldString || newString) && (
            <span className="text-muted-foreground/50 shrink-0">({diffLabel})</span>
          )}
          {replaceAll && (
            <Badge variant="outline" className="text-[9px] px-1 py-0 h-4 shrink-0">
              replace all
            </Badge>
          )}
          <span className="font-mono text-muted-foreground/50 shrink-0">edit</span>

          {isRunning && <Loader2 className="h-3 w-3 animate-spin text-muted-foreground shrink-0" />}
          {isCompleted && <Check className="h-3 w-3 text-green-500 shrink-0" />}
          {isError && <X className="h-3 w-3 text-red-500 shrink-0" />}
        </button>
      </CollapsibleTrigger>

      <CollapsibleContent>
        <div className="mt-1 mb-1 ml-4 rounded-md border border-border/60 overflow-hidden">
          {error ? (
            <div className="px-3 py-2 text-xs text-red-600 dark:text-red-400 whitespace-pre-wrap">
              {error}
            </div>
          ) : (
            <div className="overflow-x-auto bg-muted/20 text-xs font-mono leading-relaxed max-h-[300px] overflow-y-auto">
              {/* Removed lines */}
              {oldLines.map((line, i) => (
                <div key={`old-${i}`} className="px-3 py-0 bg-red-500/10 text-red-600 dark:text-red-400">
                  <span className="select-none text-red-500/50 mr-2">-</span>
                  {line}
                </div>
              ))}
              {/* Added lines */}
              {newLines.map((line, i) => (
                <div key={`new-${i}`} className="px-3 py-0 bg-green-500/10 text-green-600 dark:text-green-400">
                  <span className="select-none text-green-500/50 mr-2">+</span>
                  {line}
                </div>
              ))}
            </div>
          )}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
