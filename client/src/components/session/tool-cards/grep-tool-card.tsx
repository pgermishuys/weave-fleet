"use client";

import { useState, useMemo } from "react";
import { Search, ChevronRight, ChevronDown, Loader2, Check, X } from "lucide-react";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import { Badge } from "@/components/ui/badge";
import type { AccumulatedPart } from "@/lib/api-types";
import { parseGrepOutput } from "@/lib/tool-card-utils";

interface GrepToolCardProps {
  part: AccumulatedPart & { type: "tool" };
}

export function GrepToolCard({ part }: GrepToolCardProps) {
  const [open, setOpen] = useState(false);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const state = part.state as any;
  const isRunning = state?.status === "running" || !state?.status;
  const isCompleted = state?.status === "completed";
  const isError = state?.status === "error";

  const input = state?.input as {
    pattern?: string;
    path?: string;
    include?: string;
  } | undefined;
  const output: string = state?.output ? String(state.output) : "";
  const error: string = state?.error ? String(state.error) : "";

  const pattern = input?.pattern ?? "grep";
  const include = input?.include;

  const results = useMemo(() => parseGrepOutput(output), [output]);

  // Count unique files
  const fileCount = useMemo(() => {
    const files = new Set(results.map((r) => r.file));
    return files.size;
  }, [results]);

  const hasExpandableContent = !isRunning && (output || error);

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

          <Search className="h-3 w-3 shrink-0 text-purple-400" />
          <span className="flex-1 truncate font-mono">{pattern}</span>
          {include && (
            <span className="text-muted-foreground/50 shrink-0">{include}</span>
          )}
          {fileCount > 0 && (
            <Badge variant="outline" className="text-[9px] px-1 py-0 h-4 shrink-0">
              {fileCount} file{fileCount !== 1 ? "s" : ""}
            </Badge>
          )}
          <span className="font-mono text-muted-foreground/50 shrink-0">grep</span>

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
            <div className="overflow-y-auto max-h-[300px] bg-muted/20">
              {results.length > 0 ? (
                results.map((result, i) => (
                  <div
                    key={i}
                    className="px-3 py-0.5 text-xs font-mono text-muted-foreground hover:bg-accent/20 border-b border-border/20 last:border-b-0"
                  >
                    <span className="text-foreground/80">{result.file}</span>
                    <span className="text-muted-foreground/50">:</span>
                    <span className="text-amber-500">{result.line}</span>
                  </div>
                ))
              ) : (
                <div className="px-3 py-2 text-xs text-muted-foreground whitespace-pre-wrap">
                  {output}
                </div>
              )}
            </div>
          )}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
