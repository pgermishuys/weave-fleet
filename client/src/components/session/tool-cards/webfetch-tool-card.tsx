"use client";

import { useState, useCallback } from "react";
import { Globe, ChevronRight, ChevronDown, Copy, Check, Loader2, X } from "lucide-react";
import {
  Collapsible,
  CollapsibleTrigger,
  CollapsibleContent,
} from "@/components/ui/collapsible";
import { Badge } from "@/components/ui/badge";
import type { AccumulatedPart } from "@/lib/api-types";
import { MarkdownRenderer } from "../markdown-renderer";

interface WebFetchToolCardProps {
  part: AccumulatedPart & { type: "tool" };
}

/** Truncate a URL for compact display. */
function truncateUrl(url: string, maxLen = 60): string {
  if (url.length <= maxLen) return url;
  try {
    const u = new URL(url);
    const host = u.hostname;
    const path = u.pathname;
    const truncated = host + (path.length > 20 ? path.slice(0, 20) + "…" : path);
    return truncated.length <= maxLen ? truncated : truncated.slice(0, maxLen) + "…";
  } catch {
    return url.slice(0, maxLen) + "…";
  }
}

export function WebFetchToolCard({ part }: WebFetchToolCardProps) {
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const state = part.state as any;
  const isRunning = state?.status === "running" || !state?.status;
  const isCompleted = state?.status === "completed";
  const isError = state?.status === "error";

  const input = state?.input as {
    url?: string;
    format?: string;
    timeout?: number;
  } | undefined;
  const output: string = state?.output ? String(state.output) : "";
  const error: string = state?.error ? String(state.error) : "";

  const url = input?.url ?? "";
  const format = input?.format ?? "markdown";

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

          <Globe className="h-3 w-3 shrink-0 text-sky-400" />
          {url ? (
            <a
              href={url}
              target="_blank"
              rel="noopener noreferrer"
              className="flex-1 truncate text-sky-400 hover:underline"
              onClick={(e) => e.stopPropagation()}
            >
              {truncateUrl(url)}
            </a>
          ) : (
            <span className="flex-1 truncate">webfetch</span>
          )}
          <Badge variant="outline" className="text-[9px] px-1 py-0 h-4 shrink-0">
            {format}
          </Badge>
          <span className="font-mono text-muted-foreground/50 shrink-0">webfetch</span>

          {isRunning && <Loader2 className="h-3 w-3 animate-spin text-muted-foreground shrink-0" />}
          {isCompleted && <Check className="h-3 w-3 text-green-500 shrink-0" />}
          {isError && <X className="h-3 w-3 text-red-500 shrink-0" />}
        </button>
      </CollapsibleTrigger>

      <CollapsibleContent>
        <div className="mt-1 mb-1 ml-4 rounded-md border border-border/60 overflow-hidden">
          {/* Copy header */}
          <div className="flex items-center justify-between px-3 py-1.5 bg-muted/30 border-b border-border/40">
            {url && (
              <a
                href={url}
                target="_blank"
                rel="noopener noreferrer"
                className="text-[10px] text-sky-400 hover:underline truncate"
              >
                {url}
              </a>
            )}
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                void handleCopy();
              }}
              className="opacity-50 hover:opacity-100 transition-opacity flex items-center gap-1 text-[10px] text-muted-foreground shrink-0 ml-2"
              title="Copy content"
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
          ) : format === "markdown" ? (
            <div className="px-3 py-2 max-h-[300px] overflow-y-auto">
              <MarkdownRenderer content={output} />
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
