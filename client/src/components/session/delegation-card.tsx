import { Link } from "react-router";
import { ArrowUpRight, AlertTriangle, CheckCircle2, Loader2 } from "lucide-react";
import type { DelegationDto } from "@/lib/api-types";
import { useSessionsContext } from "@/contexts/sessions-context";

interface DelegationCardProps {
  delegation: DelegationDto;
  currentSessionId?: string;
}

export function DelegationCard({ delegation, currentSessionId }: DelegationCardProps) {
  const { sessions } = useSessionsContext();
  const isRunning = delegation.status === "pending" || delegation.status === "running";
  const isError = delegation.status === "error" || delegation.status === "cancelled";
  const childSession = delegation.childSessionId
    ? sessions.find((session) => session.session.id === delegation.childSessionId)
    : undefined;
  const childUrl = childSession
    ? `/sessions/${encodeURIComponent(childSession.session.id)}?instanceId=${encodeURIComponent(childSession.instanceId)}${currentSessionId ? `&parentSessionId=${encodeURIComponent(currentSessionId)}` : ""}`
    : null;

  const content = (
    <>
      <div className="flex items-center gap-2 font-medium text-foreground/80">
        {isRunning && <Loader2 className="h-3 w-3 animate-spin text-indigo-400 shrink-0" />}
        {!isRunning && !isError && <CheckCircle2 className="h-3 w-3 text-green-500 shrink-0" />}
        {isError && <AlertTriangle className="h-3 w-3 text-red-500 shrink-0" />}
        <span className="flex-1">{delegation.title}</span>
        {childUrl && <ArrowUpRight className="h-3 w-3 shrink-0 text-muted-foreground/60" />}
      </div>
      <p className={`mt-1 text-xs capitalize ${isError ? "text-red-500/80" : "text-muted-foreground/70"}`}>
        {delegation.status}
      </p>
    </>
  );

  if (childUrl) {
    return (
      <Link
        to={childUrl}
        className="my-1 rounded-md border border-border/60 bg-muted/30 px-3 py-2 text-xs border-l-2 border-l-indigo-500/60 block hover:bg-muted/50 hover:border-border transition-colors"
      >
        {content}
      </Link>
    );
  }

  return (
    <div className="my-1 rounded-md border border-border/60 bg-muted/30 px-3 py-2 text-xs border-l-2 border-l-indigo-500/60">
      {content}
    </div>
  );
}
