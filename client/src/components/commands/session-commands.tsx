"use client";

import { useEffect, useCallback, useState } from "react";
import { Plus, RefreshCw, OctagonX } from "lucide-react";
import { useCommandRegistry } from "@/contexts/command-registry-context";
import { useSessionsContext } from "@/contexts/sessions-context";
import { NewSessionDialog } from "@/components/session/new-session-dialog";
import { useKeybindings } from "@/contexts/keybindings-context";
import { useCurrentSessionDirectory } from "@/hooks/use-current-session-directory";
import { apiFetch } from "@/lib/api-client";

export function SessionCommands() {
  const { registerCommand, unregisterCommand } = useCommandRegistry();
  const { sessions, refetch } = useSessionsContext();
  const { bindings } = useKeybindings();
  const [dialogOpen, setDialogOpen] = useState(false);
  const currentDirectory = useCurrentSessionDirectory();

  const openNewSession = useCallback(() => setDialogOpen(true), []);
  const refreshSessions = useCallback(() => refetch(), [refetch]);

  const interruptAllSessions = useCallback(async () => {
    const busySessions = sessions.filter((s) => s.activityStatus === "busy");
    await Promise.allSettled(
      busySessions.map((s) =>
        apiFetch(`/api/sessions/${encodeURIComponent(s.session.id)}/abort`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ instanceId: s.instanceId }),
        })
      )
    );
  }, [sessions]);

  useEffect(() => {
    registerCommand({
      id: "new-session",
      label: "New Session",
      icon: Plus,
      category: "Session",
      paletteHotkey: bindings["new-session"]?.paletteHotkey ?? undefined,
      globalShortcut: bindings["new-session"]?.globalShortcut ?? undefined,
      keywords: ["create", "spawn", "start"],
      action: openNewSession,
    });
    registerCommand({
      id: "refresh-sessions",
      label: "Refresh Sessions",
      icon: RefreshCw,
      category: "Session",
      paletteHotkey: bindings["refresh-sessions"]?.paletteHotkey ?? undefined,
      keywords: ["reload", "update"],
      action: refreshSessions,
    });
    registerCommand({
      id: "interrupt-all-sessions",
      label: "Interrupt All Sessions",
      icon: OctagonX,
      category: "Fleet",
      keywords: ["abort", "cancel", "stop", "all"],
      disabled: !sessions.some((s) => s.activityStatus === "busy"),
      action: interruptAllSessions,
    });

    return () => {
      unregisterCommand("new-session");
      unregisterCommand("refresh-sessions");
      unregisterCommand("interrupt-all-sessions");
    };
  }, [registerCommand, unregisterCommand, bindings, openNewSession, refreshSessions, interruptAllSessions, sessions]);

  return (
    <NewSessionDialog open={dialogOpen} onOpenChange={setDialogOpen} defaultDirectory={currentDirectory} />
  );
}
