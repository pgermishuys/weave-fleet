"use client";

import { useEffect, useCallback } from "react";
import { useRouter, usePathname, useSearchParams } from "next/navigation";
import { LayoutGrid, Settings, ChevronRight, ChevronLeft, Clock, List } from "lucide-react";
import { useCommandRegistry } from "@/contexts/command-registry-context";
import { useKeybindings } from "@/contexts/keybindings-context";
import { useSessionsContext } from "@/contexts/sessions-context";

export function NavigationCommands() {
  const { registerCommand, unregisterCommand } = useCommandRegistry();
  const { bindings } = useKeybindings();
  const { sessions } = useSessionsContext();
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();

  const goToFleet = useCallback(() => router.push("/"), [router]);
  const goToSettings = useCallback(() => router.push("/settings"), [router]);

  // Navigate to next/previous session based on current position in session list
  const navigateSession = useCallback(
    (direction: "next" | "prev") => {
      if (sessions.length === 0) return;
      // Extract current session ID from pathname
      const match = pathname.match(/^\/sessions\/([^/]+)/);
      if (!match) return;
      const currentId = decodeURIComponent(match[1]);
      const currentIndex = sessions.findIndex((s) => s.session.id === currentId);
      if (currentIndex < 0) return;

      const targetIndex =
        direction === "next"
          ? (currentIndex + 1) % sessions.length
          : (currentIndex - 1 + sessions.length) % sessions.length;
      const target = sessions[targetIndex];
      router.push(
        `/sessions/${encodeURIComponent(target.session.id)}?instanceId=${encodeURIComponent(target.instanceId)}`
      );
    },
    [sessions, pathname, router]
  );

  const goToNextSession = useCallback(() => navigateSession("next"), [navigateSession]);
  const goToPrevSession = useCallback(() => navigateSession("prev"), [navigateSession]);
  const goToMostRecent = useCallback(() => {
    if (sessions.length === 0) return;
    const target = sessions[0]; // sessions are sorted by most recent
    router.push(
      `/sessions/${encodeURIComponent(target.session.id)}?instanceId=${encodeURIComponent(target.instanceId)}`
    );
  }, [sessions, router]);

  useEffect(() => {
    registerCommand({
      id: "nav-fleet",
      label: "Go to Fleet",
      icon: LayoutGrid,
      category: "Navigation",
      paletteHotkey: bindings["nav-fleet"]?.paletteHotkey ?? undefined,
      keywords: ["home", "dashboard", "sessions"],
      action: goToFleet,
    });
    registerCommand({
      id: "nav-settings",
      label: "Go to Settings",
      icon: Settings,
      category: "Navigation",
      paletteHotkey: bindings["nav-settings"]?.paletteHotkey ?? undefined,
      keywords: ["preferences", "config"],
      action: goToSettings,
    });
    registerCommand({
      id: "nav-next-session",
      label: "Next Session",
      icon: ChevronRight,
      category: "Navigation",
      globalShortcut: bindings["nav-next-session"]?.globalShortcut ?? undefined,
      keywords: ["forward", "right"],
      disabled: sessions.length < 2,
      action: goToNextSession,
    });
    registerCommand({
      id: "nav-prev-session",
      label: "Previous Session",
      icon: ChevronLeft,
      category: "Navigation",
      globalShortcut: bindings["nav-prev-session"]?.globalShortcut ?? undefined,
      keywords: ["back", "left"],
      disabled: sessions.length < 2,
      action: goToPrevSession,
    });
    registerCommand({
      id: "nav-most-recent",
      label: "Go to Most Recent Session",
      icon: Clock,
      category: "Navigation",
      keywords: ["latest", "newest", "last"],
      disabled: sessions.length === 0,
      action: goToMostRecent,
    });
    registerCommand({
      id: "nav-go-to-session",
      label: "Go to Session...",
      icon: List,
      category: "Navigation",
      keywords: ["switch", "open", "jump", "session"],
      disabled: sessions.length === 0,
      action: () => {
        // Default action: go to most recent if no sub-commands used
        if (sessions.length > 0) goToMostRecent();
      },
      getSubCommands: () =>
        sessions.map((s) => ({
          id: `nav-session-${s.session.id}`,
          label: s.session.title || s.session.id.slice(0, 12),
          description: s.activityStatus ?? undefined,
          icon: List,
          category: "Navigation" as const,
          action: () =>
            router.push(
              `/sessions/${encodeURIComponent(s.session.id)}?instanceId=${encodeURIComponent(s.instanceId)}`
            ),
        })),
    });

    return () => {
      unregisterCommand("nav-fleet");
      unregisterCommand("nav-settings");
      unregisterCommand("nav-next-session");
      unregisterCommand("nav-prev-session");
      unregisterCommand("nav-most-recent");
      unregisterCommand("nav-go-to-session");
    };
  }, [
    registerCommand,
    unregisterCommand,
    bindings,
    goToFleet,
    goToSettings,
    goToNextSession,
    goToPrevSession,
    goToMostRecent,
    sessions,
  ]);

  return null;
}
