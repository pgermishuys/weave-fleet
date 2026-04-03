"use client";

import { Session } from "@/lib/types";
import { formatTokens, formatCost, getStatusDot } from "@/lib/mock-data";
import { Progress } from "@/components/ui/progress";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import {
  CheckSquare,
  Square,
  Hash,
  Coins,
  FileCode,
  FilePlus,
  FileEdit,
  FileX,
  Gauge,
} from "lucide-react";

interface SessionSidebarProps {
  session: Session;
}

const planTasks = [
  { label: "Design database schema", done: true },
  { label: "Create auth models", done: true },
  { label: "Implement auth service", done: true },
  { label: "Add middleware", done: true },
  { label: "Write unit tests", done: false },
  { label: "Integration tests", done: false },
  { label: "Review & cleanup", done: false },
];

const agentTokens = [
  { agent: "loom", tokens: 12000, color: "bg-[#4A90D9]" },
  { agent: "tapestry", tokens: 18000, color: "bg-[#D94A4A]" },
  { agent: "thread", tokens: 3200, color: "bg-[#27AE60]" },
  { agent: "pattern", tokens: 4800, color: "bg-[#9B59B6]" },
  { agent: "weft", tokens: 2800, color: "bg-[#1ABC9C]" },
];

function FileIcon({ type }: { type: string }) {
  switch (type) {
    case "added": return <FilePlus className="h-3 w-3 text-green-500" />;
    case "modified": return <FileEdit className="h-3 w-3 text-amber-500" />;
    case "deleted": return <FileX className="h-3 w-3 text-red-500" />;
    default: return <FileCode className="h-3 w-3" />;
  }
}

export function SessionSidebar({ session }: SessionSidebarProps) {
  const totalTokens = session.tokens.input + session.tokens.output + session.tokens.reasoning;
  const maxAgentTokens = Math.max(...agentTokens.map((a) => a.tokens));

  return (
    <div className="space-y-5">
      {/* Plan Progress */}
      {session.planProgress && (
        <section>
          <h3 className="text-xs font-semibold uppercase text-muted-foreground mb-2">
            Plan Progress
          </h3>
          <div className="space-y-1.5">
            {planTasks.map((task, i) => (
              <div
                key={i}
                className={`flex items-start gap-2 text-xs ${
                  task.done ? "text-muted-foreground" : "text-foreground"
                } ${
                  !task.done && i === session.planProgress!.done
                    ? "font-medium"
                    : ""
                }`}
              >
                {task.done ? (
                  <CheckSquare className="h-3.5 w-3.5 text-green-500 mt-0.5 shrink-0" />
                ) : (
                  <Square className="h-3.5 w-3.5 text-muted-foreground mt-0.5 shrink-0" />
                )}
                <span className={task.done ? "line-through" : ""}>
                  {task.label}
                </span>
              </div>
            ))}
          </div>
          <div className="mt-2">
            <Progress
              value={(session.planProgress.done / session.planProgress.total) * 100}
              className="h-1.5"
            />
            <p className="text-[10px] text-muted-foreground mt-1">
              {session.planProgress.done} of {session.planProgress.total} tasks
            </p>
          </div>
        </section>
      )}

      <Separator />

      {/* Agent Activity */}
      <section>
        <h3 className="text-xs font-semibold uppercase text-muted-foreground mb-2">
          Agent Activity
        </h3>
        <div className="space-y-2">
          {agentTokens.map((a) => (
            <div key={a.agent} className="space-y-1">
              <div className="flex items-center justify-between text-xs">
                <span className="font-medium capitalize">{a.agent}</span>
                <span className="text-muted-foreground">
                  {formatTokens(a.tokens)}
                </span>
              </div>
              <div className="h-1.5 rounded-full bg-accent overflow-hidden">
                <div
                  className={`h-full rounded-full ${a.color}`}
                  style={{ width: `${(a.tokens / maxAgentTokens) * 100}%` }}
                />
              </div>
            </div>
          ))}
        </div>
      </section>

      <Separator />

      {/* Resources */}
      <section>
        <h3 className="text-xs font-semibold uppercase text-muted-foreground mb-2">
          Resources
        </h3>
        <div className="space-y-2 text-xs">
          <div className="flex items-center justify-between">
            <span className="flex items-center gap-1.5 text-muted-foreground">
              <Hash className="h-3 w-3" /> Tokens
            </span>
            <span className="font-medium">{formatTokens(totalTokens)}</span>
          </div>
          <div className="flex items-center justify-between">
            <span className="flex items-center gap-1.5 text-muted-foreground">
              <Coins className="h-3 w-3" /> Cost
            </span>
            <span className="font-medium">{formatCost(session.cost)}</span>
          </div>
          <div className="flex items-center justify-between">
            <span className="flex items-center gap-1.5 text-muted-foreground">
              <Gauge className="h-3 w-3" /> Cache hit
            </span>
            <span className="font-medium">
              {Math.round(
                (session.tokens.cache / (session.tokens.input + session.tokens.cache)) * 100
              )}%
            </span>
          </div>
          <div className="space-y-1 mt-2">
            <div className="flex items-center justify-between text-[10px] text-muted-foreground">
              <span>Context window</span>
              <span>{Math.round(session.contextUsage * 100)}%</span>
            </div>
            <Progress value={session.contextUsage * 100} className="h-1.5" />
          </div>
        </div>
      </section>

      <Separator />

      {/* Modified Files */}
      {session.modifiedFiles.length > 0 && (
        <section>
          <h3 className="text-xs font-semibold uppercase text-muted-foreground mb-2">
            Modified Files
          </h3>
          <div className="space-y-1.5">
            {session.modifiedFiles.map((file) => (
              <div key={file.path} className="flex items-center gap-1.5 text-xs">
                <FileIcon type={file.type} />
                <span className="font-mono text-muted-foreground truncate">
                  {file.path}
                </span>
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
