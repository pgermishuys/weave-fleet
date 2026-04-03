"use client";

import { useState, useCallback, useMemo } from "react";
import { FileText, ChevronRight, ChevronDown, Copy, Check, Loader2, X } from "lucide-react";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import type { AccumulatedPart } from "@/lib/api-types";
import { shortenPath } from "@/lib/tool-labels";
import { getLanguageFromPath, countLines } from "@/lib/tool-card-utils";

interface ReadToolCardProps {
  part: AccumulatedPart & { type: "tool" };
}

export function ReadToolCard({ part }: ReadToolCardProps) {
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const state = part.state as any;
  const isRunning = state?.status === "running" || !state?.status;
  const isCompleted = state?.status === "completed";
  const isError = state?.status === "error";

  const input = state?.input as { filePath?: string; offset?: number; limit?: number } | undefined;
  const output: string = state?.output ? String(state.output) : "";
  const error: string = state?.error ? String(state.error) : "";

  const filePath = input?.filePath ?? "unknown";
  const shortPath = shortenPath(filePath);
  const language = getLanguageFromPath(filePath);

  // Count lines in output
  const lineCount = useMemo(() => countLines(output), [output]);

  // Build line range label
  const rangeLabel = useMemo(() => {
    if (input?.offset && input?.limit) {
      return `lines ${input.offset}–${input.offset + input.limit - 1}`;
    }
    if (input?.offset) {
      return `from line ${input.offset}`;
    }
    if (lineCount > 0) {
      return `${lineCount} line${lineCount !== 1 ? "s" : ""}`;
    }
    return null;
  }, [input, lineCount]);

  const hasExpandableContent = !isRunning && (output || error);

  const handleCopy = useCallback(async () => {
    const text = output || error || "";
    if (!text) return;
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // clipboard access denied
    }
  }, [output, error]);

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

          <FileText className="h-3 w-3 shrink-0 text-blue-400" />
          <span className="flex-1 truncate">{shortPath}</span>
          {rangeLabel && (
            <span className="text-muted-foreground/50 shrink-0">
              {rangeLabel}
            </span>
          )}
          <span className="font-mono text-muted-foreground/50 shrink-0">read</span>

          {isRunning && <Loader2 className="h-3 w-3 animate-spin text-muted-foreground shrink-0" />}
          {isCompleted && <Check className="h-3 w-3 text-green-500 shrink-0" />}
          {isError && <X className="h-3 w-3 text-red-500 shrink-0" />}
        </button>
      </CollapsibleTrigger>

      <CollapsibleContent>
        <div className="mt-1 mb-1 ml-4 rounded-md border border-border/60 overflow-hidden">
          {/* Header bar */}
          <div className="flex items-center justify-between px-3 py-1.5 bg-muted/30 border-b border-border/40">
            <span className="text-[10px] text-muted-foreground font-mono uppercase tracking-wide">
              {language || "text"}
            </span>
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                void handleCopy();
              }}
              className="opacity-50 hover:opacity-100 transition-opacity flex items-center gap-1 text-[10px] text-muted-foreground"
              title="Copy to clipboard"
            >
              {copied ? (
                <>
                  <Check className="h-3 w-3 text-green-600 dark:text-green-400" />
                  <span className="text-green-600 dark:text-green-400">Copied!</span>
                </>
              ) : (
                <Copy className="h-3 w-3" />
              )}
            </button>
          </div>

          {/* Content area */}
          {error ? (
            <div className="px-3 py-2 text-xs text-red-600 dark:text-red-400 whitespace-pre-wrap">
              {error}
            </div>
          ) : (
            <pre className="overflow-x-auto bg-muted/20 px-3 py-2 text-xs font-mono leading-relaxed max-h-[300px] overflow-y-auto m-0">
              {output}
            </pre>
          )}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
