"use client";

import { Header } from "@/components/layout/header";
import { mockQueueItems, formatCost, formatTokens, formatDuration, getStatusColor, getStatusDot } from "@/lib/mock-data";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader } from "@/components/ui/card";
import { Clock, Coins, Hash, Play, Pause } from "lucide-react";
import { Button } from "@/components/ui/button";

const running = mockQueueItems.filter((q) => q.status === "running");
const queued = mockQueueItems.filter((q) => q.status === "queued");
const completed = mockQueueItems.filter((q) => q.status === "completed");

export default function QueuePage() {
  return (
    <div className="flex flex-col h-full">
      <Header
        title="Task Queue"
        subtitle={`${running.length} running · ${queued.length} queued · ${completed.length} completed`}
        actions={
          <div className="flex items-center gap-2">
            <Button variant="outline" size="sm" className="gap-1.5">
              <Play className="h-3 w-3" /> Resume
            </Button>
            <Button variant="outline" size="sm" className="gap-1.5">
              <Pause className="h-3 w-3" /> Pause
            </Button>
          </div>
        }
      />
      <div className="flex-1 overflow-auto p-6 space-y-8">
        {/* Running */}
        {running.length > 0 && (
          <section>
            <h3 className="text-sm font-semibold text-muted-foreground mb-3 uppercase">Running</h3>
            <div className="space-y-2">
              {running.map((item) => (
                <QueueItemRow key={item.id} item={item} />
              ))}
            </div>
          </section>
        )}

        {/* Queued */}
        {queued.length > 0 && (
          <section>
            <h3 className="text-sm font-semibold text-muted-foreground mb-3 uppercase">Queued</h3>
            <div className="space-y-2">
              {queued.map((item) => (
                <QueueItemRow key={item.id} item={item} />
              ))}
            </div>
          </section>
        )}

        {/* Completed */}
        {completed.length > 0 && (
          <section>
            <h3 className="text-sm font-semibold text-muted-foreground mb-3 uppercase">Completed</h3>
            <div className="space-y-2">
              {completed.map((item) => (
                <QueueItemRow key={item.id} item={item} />
              ))}
            </div>
          </section>
        )}
      </div>
    </div>
  );
}

function QueueItemRow({ item }: { item: (typeof mockQueueItems)[number] }) {
  return (
    <Card>
      <CardContent className="flex items-center gap-4 py-3 px-4">
        <span className={`h-2.5 w-2.5 rounded-full shrink-0 ${getStatusDot(item.status)} ${item.status === "running" ? "animate-pulse" : ""}`} />
        <div className="flex-1 min-w-0">
          <p className="text-sm font-medium truncate">{item.prompt}</p>
          <div className="flex items-center gap-3 text-xs text-muted-foreground mt-0.5">
            <span className="font-mono">{item.workspaceDir}</span>
            <Badge variant="outline" className="text-[10px] px-1.5 py-0">
              P{item.priority}
            </Badge>
          </div>
        </div>
        <div className="flex items-center gap-3 text-xs text-muted-foreground shrink-0">
          {item.tokens != null && (
            <span className="flex items-center gap-1">
              <Hash className="h-3 w-3" /> {formatTokens(item.tokens)}
            </span>
          )}
          {item.cost != null && (
            <span className="flex items-center gap-1">
              <Coins className="h-3 w-3" /> {formatCost(item.cost)}
            </span>
          )}
          {item.duration != null && (
            <span className="flex items-center gap-1">
              <Clock className="h-3 w-3" /> {formatDuration(item.duration)}
            </span>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
