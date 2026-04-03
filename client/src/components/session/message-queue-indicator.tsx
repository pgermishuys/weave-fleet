"use client";

import { X, ListOrdered, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { QueuedMessage } from "@/hooks/use-message-queue";

interface MessageQueueIndicatorProps {
  queue: QueuedMessage[];
  onRemove: (index: number) => void;
  onClear: () => void;
  isAutoSending: boolean;
}

export function MessageQueueIndicator({
  queue,
  onRemove,
  onClear,
  isAutoSending,
}: MessageQueueIndicatorProps) {
  if (queue.length === 0 && !isAutoSending) return null;

  return (
    <div className="border border-border/60 rounded-md bg-muted/30 overflow-hidden">
      {/* Header */}
      <div className="flex items-center justify-between px-3 py-1.5 border-b border-border/40">
        <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
          <ListOrdered className="h-3 w-3" />
          <span>
            {isAutoSending && queue.length === 0
              ? "Sending queued message…"
              : `${queue.length} message${queue.length !== 1 ? "s" : ""} queued`}
          </span>
        </div>
        {queue.length > 1 && (
          <Button
            variant="ghost"
            size="sm"
            className="h-5 px-1.5 text-[10px] text-muted-foreground hover:text-destructive gap-1"
            onClick={onClear}
          >
            <Trash2 className="h-2.5 w-2.5" />
            Clear all
          </Button>
        )}
      </div>

      {/* Queue items */}
      {queue.length > 0 && (
        <div className="divide-y divide-border/30 max-h-[120px] overflow-y-auto">
          {queue.map((item, index) => (
            <div
              key={item.id}
              className="flex items-center gap-2 px-3 py-1.5 group hover:bg-muted/50 transition-colors"
            >
              <span className="text-[10px] text-muted-foreground/60 font-mono w-4 shrink-0">
                {index + 1}.
              </span>
              <span className="text-xs text-foreground/80 truncate flex-1">
                {item.text}
              </span>
              {item.agent && (
                <span className="text-[10px] text-muted-foreground bg-muted rounded px-1 py-0.5 shrink-0">
                  @{item.agent}
                </span>
              )}
              <Button
                variant="ghost"
                size="sm"
                className="h-4 w-4 p-0 opacity-0 group-hover:opacity-100 transition-opacity shrink-0"
                onClick={() => onRemove(index)}
              >
                <X className="h-3 w-3 text-muted-foreground hover:text-destructive" />
              </Button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
