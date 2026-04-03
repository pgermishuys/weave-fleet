"use client";

import { useState, useMemo } from "react";
import { BookOpen, ChevronRight, ChevronDown, Loader2, Check, X } from "lucide-react";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import { Badge } from "@/components/ui/badge";
import type { AccumulatedPart } from "@/lib/api-types";

interface SkillToolCardProps {
  part: AccumulatedPart & { type: "tool" };
}

export function SkillToolCard({ part }: SkillToolCardProps) {
  const [open, setOpen] = useState(false);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const state = part.state as any;
  const isRunning = state?.status === "running" || !state?.status;
  const isCompleted = state?.status === "completed";
  const isError = state?.status === "error";

  const input = state?.input as { name?: string } | undefined;
  const output: string = state?.output ? String(state.output) : "";
  const error: string = state?.error ? String(state.error) : "";

  const skillName = input?.name ?? "skill";

  // Extract first paragraph of skill content as description
  const description = useMemo(() => {
    if (!output) return null;
    // Skip leading blank lines and headers, grab first paragraph
    const lines = output.split("\n");
    let started = false;
    const descLines: string[] = [];
    for (const line of lines) {
      const trimmed = line.trim();
      // Skip headers and blank lines at start
      if (!started && (!trimmed || trimmed.startsWith("#") || trimmed.startsWith("<"))) continue;
      if (!started && trimmed) started = true;
      if (started) {
        if (!trimmed && descLines.length > 0) break; // end of first paragraph
        descLines.push(trimmed);
      }
    }
    const text = descLines.join(" ");
    return text.length > 200 ? text.slice(0, 200) + "…" : text || null;
  }, [output]);

  const hasExpandableContent = !isRunning && (description || error);

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

          <BookOpen className="h-3 w-3 shrink-0 text-emerald-400" />
          <span className="flex-1 truncate">{skillName}</span>
          <Badge
            variant="outline"
            className={`text-[9px] px-1 py-0 h-4 shrink-0 ${
              isCompleted ? "border-green-500/40 text-green-500" : ""
            }`}
          >
            {isRunning ? "loading" : isCompleted ? "loaded" : "error"}
          </Badge>
          <span className="font-mono text-muted-foreground/50 shrink-0">skill</span>

          {isRunning && <Loader2 className="h-3 w-3 animate-spin text-muted-foreground shrink-0" />}
          {isCompleted && <Check className="h-3 w-3 text-green-500 shrink-0" />}
          {isError && <X className="h-3 w-3 text-red-500 shrink-0" />}
        </button>
      </CollapsibleTrigger>

      <CollapsibleContent>
        <div className="mt-1 mb-1 ml-4 rounded-md border border-border/60 overflow-hidden bg-muted/20">
          {error ? (
            <div className="px-3 py-2 text-xs text-red-600 dark:text-red-400 whitespace-pre-wrap">
              {error}
            </div>
          ) : description ? (
            <div className="px-3 py-2 text-xs text-muted-foreground leading-relaxed">
              {description}
            </div>
          ) : null}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
