"use client";

import { useState, useCallback } from "react";
import { SquareTerminal, ChevronRight, ChevronDown, Copy, Check, Loader2, X } from "lucide-react";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import type { AccumulatedPart } from "@/lib/api-types";

interface BashToolCardProps {
  part: AccumulatedPart & { type: "tool" };
}

export function BashToolCard({ part }: BashToolCardProps) {
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const state = part.state as any;
  const isRunning = state?.status === "running" || !state?.status;
  const isCompleted = state?.status === "completed";
  const isError = state?.status === "error";

  const input = state?.input as {
    command?: string;
    description?: string;
    workdir?: string;
    timeout?: number;
  } | undefined;
  const output: string = state?.output ? String(state.output) : "";
  const error: string = state?.error ? String(state.error) : "";

  const command = input?.command ?? "";
  const description = input?.description ?? "";
  const workdir = input?.workdir;

  // Compact label: description if available, else truncated command
  const compactLabel = description || (command.length > 60 ? command.slice(0, 60) + "…" : command) || "bash";

  const hasExpandableContent = !isRunning && (command || output || error);

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

          <SquareTerminal className="h-3 w-3 shrink-0 text-muted-foreground" />
          <span className="flex-1 truncate">{compactLabel}</span>
          <span className="font-mono text-muted-foreground/50 shrink-0">bash</span>

          {isRunning && <Loader2 className="h-3 w-3 animate-spin text-muted-foreground shrink-0" />}
          {isCompleted && <Check className="h-3 w-3 text-green-500 shrink-0" />}
          {isError && <X className="h-3 w-3 text-red-500 shrink-0" />}
        </button>
      </CollapsibleTrigger>

      <CollapsibleContent>
        <div
          className={`mt-1 mb-1 ml-4 rounded-md border overflow-hidden ${
            isError ? "border-red-500/40" : "border-border/60"
          }`}
        >
          {/* Terminal header with workdir */}
          {workdir && (
            <div className="flex items-center justify-between px-3 py-1 bg-muted/40 border-b border-border/40">
              <span className="text-[10px] text-muted-foreground font-mono truncate">
                {workdir}
              </span>
              <button
                type="button"
                onClick={(e) => {
                  e.stopPropagation();
                  void handleCopy();
                }}
                className="opacity-50 hover:opacity-100 transition-opacity flex items-center gap-1 text-[10px] text-muted-foreground shrink-0"
                title="Copy output"
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
          )}

          {/* Terminal content */}
          <pre className="overflow-x-auto bg-zinc-950 dark:bg-zinc-950 px-3 py-2 text-xs font-mono leading-relaxed max-h-[300px] overflow-y-auto m-0 text-zinc-200">
            {/* Command prompt */}
            {command && (
              <div className="text-green-400">
                <span className="select-none text-green-500/70">$ </span>
                {command}
              </div>
            )}
            {/* Output */}
            {output && (
              <div className="mt-1 text-zinc-300 whitespace-pre-wrap">{output}</div>
            )}
            {/* Error */}
            {error && (
              <div className="mt-1 text-red-400 whitespace-pre-wrap">{error}</div>
            )}
          </pre>

          {/* Copy button when no workdir header */}
          {!workdir && (output || error) && (
            <div className="flex justify-end px-3 py-1 bg-zinc-950 border-t border-zinc-800">
              <button
                type="button"
                onClick={(e) => {
                  e.stopPropagation();
                  void handleCopy();
                }}
                className="opacity-50 hover:opacity-100 transition-opacity flex items-center gap-1 text-[10px] text-zinc-400"
                title="Copy output"
              >
                {copied ? (
                  <>
                    <Check className="h-3 w-3 text-green-400" />
                    <span className="text-green-400">Copied!</span>
                  </>
                ) : (
                  <Copy className="h-3 w-3" />
                )}
              </button>
            </div>
          )}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}
