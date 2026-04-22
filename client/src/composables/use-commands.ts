import { computed, onMounted, onUnmounted, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import {
  BarChart3,
  ChevronLeft,
  ChevronRight,
  Copy,
  Download,
  Eraser,
  Focus,
  GitBranchPlus,
  LayoutGrid,
  Maximize2,
  MessageSquare,
  MoonStar,
  PanelLeftClose,
  PanelRightClose,
  Plus,
  Puzzle,
  RefreshCcw,
  ScrollText,
  Settings,
  SquareDashedBottom,
  ZoomIn,
  ZoomOut,
} from "lucide-vue-next";
import { storeToRefs } from "pinia";
import { clearDraftText } from "@/composables/use-draft-state";
import type { Command } from "@/lib/command-registry";
import { dispatchCommandEvent } from "@/lib/command-events";
import { matchesKeyboardShortcut, useKeyboardShortcut } from "@/composables/use-keyboard-shortcut";
import { useAbortSession, useForkSession } from "@/composables/use-session-actions";
import type { SessionListItem } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";
import { useCommandStore } from "@/stores/commands";
import { useKeybindingsStore } from "@/stores/keybindings";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
import { useThemeStore } from "@/stores/theme";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

function isEditableTarget(target: EventTarget | null): boolean {
  return target instanceof HTMLInputElement
    || target instanceof HTMLTextAreaElement
    || target instanceof HTMLSelectElement
    || (target instanceof HTMLElement && target.isContentEditable);
}

function isModalContentTarget(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) {
    return false;
  }

  return target.closest('[data-slot="dialog-content"], [data-slot="sheet-content"], [data-slot="alert-dialog-content"]') !== null;
}

function getCurrentSessionId(pathname: string, activeSessionId: string | null): string | null {
  if (pathname.startsWith("/sessions/")) {
    const sessionId = pathname.slice("/sessions/".length).split("/")[0];
    return sessionId ? decodeURIComponent(sessionId) : activeSessionId;
  }

  return activeSessionId;
}

export function useCommands() {
  const commandStore = useCommandStore();
  const keybindingsStore = useKeybindingsStore();
  const sessionsStore = useSessionsStore();
  const sidebarStore = useSidebarStore();
  const themeStore = useThemeStore();
  const workspaceUiStore = useWorkspaceUiStore();
  const { abortSession } = useAbortSession();
  const { forkSession } = useForkSession();
  const router = useRouter();
  const pathname = useLocation({
    select: (location) => location.pathname,
  });

  const { bindings } = storeToRefs(keybindingsStore);
  const { sessions, activeSessionId, retentionStatus } = storeToRefs(sessionsStore);
  const { currentTheme } = storeToRefs(themeStore);

  const themeCycle = ["system", "dark", "light"] as const;

  function navigateToRoute(to: "/" | "/board" | "/analytics" | "/settings"): void {
    if (to === "/") {
      sidebarStore.setActiveRail("sessions");
    }

    if (to === "/board") {
      sidebarStore.setActiveRail("board");
    }

    if (to === "/analytics") {
      sidebarStore.setActiveRail("analytics");
    }

    if (to === "/settings") {
      sidebarStore.setActiveRail("settings");
    }

    void router.navigate({ to });
  }

  function navigateToSession(sessionId: string): void {
    sessionsStore.setActiveSessionId(sessionId);
    sidebarStore.setActiveRail("sessions");

    void router.navigate({
      to: "/sessions/$id",
      params: { id: sessionId },
      search: {
        instanceId: sessions.value.find((session) => session.session.id === sessionId)?.instanceId,
        parentSessionId: undefined,
      },
    });
  }

  function handleNewSession(): void {
    workspaceUiStore.openNewSessionDialog(null);
  }

  async function refreshSessions(): Promise<void> {
    const params = new URLSearchParams();

    if (retentionStatus.value !== "active") {
      params.set("retentionStatus", retentionStatus.value);
    }

    const url = params.size > 0 ? `/api/sessions?${params.toString()}` : "/api/sessions";
    const response = await apiFetch(url);

    if (!response.ok) {
      return;
    }

    const data = await response.json() as SessionListItem[];
    sessionsStore.setSessions(data);
  }

  async function interruptCurrentSession(): Promise<void> {
    const currentSession = sessions.value.find((session) => session.session.id === activeSessionId.value);

    if (!currentSession?.instanceId) {
      return;
    }

    await abortSession(currentSession.session.id, currentSession.instanceId);
    sessionsStore.patchSession(currentSession.session.id, {
      activityStatus: "idle",
      sessionStatus: "idle",
    });
  }

  async function forkCurrentSession(): Promise<void> {
    if (!activeSessionId.value) {
      return;
    }

    const response = await forkSession(activeSessionId.value);

    sessionsStore.setActiveSessionId(response.session.id);
    sidebarStore.setActiveRail("sessions");
    void router.navigate({
      to: "/sessions/$id",
      params: { id: response.session.id },
      search: { instanceId: response.instanceId, parentSessionId: undefined },
    });
  }

  function focusPrompt(): void {
    if (!activeSessionId.value) {
      return;
    }

    dispatchCommandEvent("weave:command-focus-prompt", { sessionId: activeSessionId.value });
  }

  function copyCurrentSessionId(): void {
    if (!activeSessionId.value) {
      return;
    }

    dispatchCommandEvent("weave:command-copy-session-id", { sessionId: activeSessionId.value });
  }

  function exportCurrentConversation(): void {
    if (!activeSessionId.value) {
      return;
    }

    dispatchCommandEvent("weave:command-export-conversation", { sessionId: activeSessionId.value });
  }

  function scrollActivityToTop(): void {
    dispatchCommandEvent("weave:command-scroll-top", { sessionId: activeSessionId.value });
  }

  function scrollActivityToBottom(): void {
    dispatchCommandEvent("weave:command-scroll-bottom", { sessionId: activeSessionId.value });
  }

  function clearConversationDraft(): void {
    if (!activeSessionId.value) {
      return;
    }

    clearDraftText(activeSessionId.value);
  }

  function toggleActivityFilter(): void {
    sessionsStore.setRetentionStatus(retentionStatus.value === "active" ? "all" : "active");
  }

  function cycleTheme(): void {
    const currentIndex = themeCycle.indexOf(currentTheme.value);
    const nextIndex = (currentIndex + 1) % themeCycle.length;
    themeStore.setTheme(themeCycle[nextIndex]);
  }

  function toggleFullscreen(): void {
    if (typeof document === "undefined") {
      return;
    }

    if (document.fullscreenElement) {
      void document.exitFullscreen();
      return;
    }

    void document.documentElement.requestFullscreen();
  }

  function zoomDocument(delta: number): void {
    if (typeof document === "undefined") {
      return;
    }

    const currentPercent = Number.parseFloat(document.documentElement.style.fontSize || "100") || 100;
    const nextPercent = Math.min(150, Math.max(70, currentPercent + delta));
    document.documentElement.style.fontSize = `${nextPercent}%`;
  }

  const currentSessionIndex = computed(() => {
    const currentSessionId = getCurrentSessionId(pathname.value, activeSessionId.value);
    return sessions.value.findIndex((session) => session.session.id === currentSessionId);
  });

  const availableCommands = computed<Command[]>(() => {
    const nextSession = currentSessionIndex.value >= 0 && sessions.value.length > 1
      ? sessions.value[(currentSessionIndex.value + 1) % sessions.value.length]
      : null;
    const previousSession = currentSessionIndex.value >= 0 && sessions.value.length > 1
      ? sessions.value[(currentSessionIndex.value - 1 + sessions.value.length) % sessions.value.length]
      : null;

    return [
      {
        id: "nav-fleet",
        label: "Go to Sessions",
        description: "Open the sessions workspace.",
        icon: MessageSquare,
        category: "Navigation",
        paletteHotkey: bindings.value["nav-fleet"]?.paletteHotkey ?? undefined,
        keywords: ["home", "fleet", "dashboard", "sessions"],
        action: () => navigateToRoute("/"),
      },
      {
        id: "nav-board",
        label: "Go to Board",
        description: "Open the fleet board view.",
        icon: LayoutGrid,
        category: "Navigation",
        keywords: ["kanban", "overview", "board"],
        action: () => navigateToRoute("/board"),
      },
      {
        id: "nav-analytics",
        label: "Go to Analytics",
        description: "Open analytics and reporting.",
        icon: BarChart3,
        category: "Navigation",
        keywords: ["reports", "metrics", "analytics"],
        action: () => navigateToRoute("/analytics"),
      },
      {
        id: "nav-settings",
        label: "Go to Settings",
        description: "Open workspace settings.",
        icon: Settings,
        category: "Navigation",
        paletteHotkey: bindings.value["nav-settings"]?.paletteHotkey ?? undefined,
        keywords: ["preferences", "config", "settings"],
        action: () => navigateToRoute("/settings"),
      },
      {
        id: "new-session",
        label: "New Session",
        description: "Create and open a new session.",
        icon: Plus,
        category: "Session",
        paletteHotkey: bindings.value["new-session"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["new-session"]?.globalShortcut ?? undefined,
        keywords: ["create", "spawn", "start"],
        action: handleNewSession,
      },
      {
        id: "refresh-sessions",
        label: "Refresh Sessions",
        description: "Reload the current sessions list.",
        icon: RefreshCcw,
        category: "Session",
        paletteHotkey: bindings.value["refresh-sessions"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["refresh-sessions"]?.globalShortcut ?? undefined,
        keywords: ["reload", "sync", "refresh"],
        action: () => {
          void refreshSessions().catch(() => {});
        },
      },
      {
        id: "focus-prompt",
        label: "Focus Prompt",
        description: activeSessionId.value ? "Focus the active session prompt." : "Open a session to focus the prompt.",
        icon: Focus,
        category: "Session",
        paletteHotkey: bindings.value["focus-prompt"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["focus-prompt"]?.globalShortcut ?? undefined,
        keywords: ["prompt", "composer", "input", "message"],
        disabled: activeSessionId.value === null,
        action: focusPrompt,
      },
      {
        id: "nav-go-to-session",
        label: "Go to Session...",
        description: sessions.value.length > 0
          ? `Switch between ${sessions.value.length} sessions.`
          : "No sessions available.",
        icon: MessageSquare,
        category: "Session",
        keywords: ["switch", "open", "jump", "session"],
        disabled: sessions.value.length === 0,
        action: () => {
          const firstSession = sessions.value[0];

          if (firstSession) {
            navigateToSession(firstSession.session.id);
          }
        },
        getSubCommands: () => {
          return sessions.value.map((session) => ({
            id: `nav-session-${session.session.id}`,
            label: session.session.title || session.session.id,
            description: [session.projectName ?? "Ungrouped", session.sessionStatus]
              .filter(Boolean)
              .join(" • "),
            icon: MessageSquare,
            category: "Session" as const,
            keywords: [session.session.id, session.projectName ?? "", session.sessionStatus],
            disabled: session.session.id === activeSessionId.value,
            action: () => navigateToSession(session.session.id),
          }));
        },
      },
      {
        id: "nav-next-session",
        label: "Next Session",
        description: nextSession ? `Switch to ${nextSession.session.title}.` : "A second session is required.",
        icon: ChevronRight,
        category: "Session",
        globalShortcut: bindings.value["nav-next-session"]?.globalShortcut ?? undefined,
        keywords: ["forward", "right", "next"],
        disabled: nextSession === null,
        action: () => {
          if (nextSession) {
            navigateToSession(nextSession.session.id);
          }
        },
      },
      {
        id: "nav-prev-session",
        label: "Previous Session",
        description: previousSession ? `Switch to ${previousSession.session.title}.` : "A second session is required.",
        icon: ChevronLeft,
        category: "Session",
        globalShortcut: bindings.value["nav-prev-session"]?.globalShortcut ?? undefined,
        keywords: ["back", "left", "previous"],
        disabled: previousSession === null,
        action: () => {
          if (previousSession) {
            navigateToSession(previousSession.session.id);
          }
        },
      },
      {
        id: "interrupt-session",
        label: "Interrupt Session",
        description: activeSessionId.value ? "Abort the active session." : "No active session selected.",
        icon: SquareDashedBottom,
        category: "Session",
        paletteHotkey: bindings.value["interrupt-session"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["interrupt-session"]?.globalShortcut ?? undefined,
        keywords: ["abort", "stop", "interrupt", "cancel"],
        disabled: activeSessionId.value === null,
        action: () => {
          void interruptCurrentSession().catch(() => {});
        },
      },
      {
        id: "copy-session-id",
        label: "Copy Session ID",
        description: activeSessionId.value ? "Copy the active session ID." : "No active session selected.",
        icon: Copy,
        category: "Session",
        paletteHotkey: bindings.value["copy-session-id"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["copy-session-id"]?.globalShortcut ?? undefined,
        keywords: ["copy", "session", "id"],
        disabled: activeSessionId.value === null,
        action: copyCurrentSessionId,
      },
      {
        id: "fork-session",
        label: "Fork Session",
        description: activeSessionId.value ? "Create a fork from the active session." : "No active session selected.",
        icon: GitBranchPlus,
        category: "Session",
        paletteHotkey: bindings.value["fork-session"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["fork-session"]?.globalShortcut ?? undefined,
        keywords: ["fork", "branch", "duplicate", "session"],
        disabled: activeSessionId.value === null,
        action: () => {
          void forkCurrentSession().catch(() => {});
        },
      },
      {
        id: "export-conversation",
        label: "Export Conversation",
        description: activeSessionId.value ? "Download the active conversation as JSON." : "No active session selected.",
        icon: Download,
        category: "Session",
        paletteHotkey: bindings.value["export-conversation"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["export-conversation"]?.globalShortcut ?? undefined,
        keywords: ["export", "download", "conversation", "json"],
        disabled: activeSessionId.value === null,
        action: exportCurrentConversation,
      },
      {
        id: "scroll-to-top",
        label: "Scroll to Top",
        description: "Scroll the activity stream to the top.",
        icon: ScrollText,
        category: "Session",
        paletteHotkey: bindings.value["scroll-to-top"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["scroll-to-top"]?.globalShortcut ?? undefined,
        keywords: ["scroll", "top", "history"],
        disabled: activeSessionId.value === null,
        action: scrollActivityToTop,
      },
      {
        id: "scroll-to-bottom",
        label: "Scroll to Bottom",
        description: "Scroll the activity stream to the latest message.",
        icon: ScrollText,
        category: "Session",
        paletteHotkey: bindings.value["scroll-to-bottom"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["scroll-to-bottom"]?.globalShortcut ?? undefined,
        keywords: ["scroll", "bottom", "latest"],
        disabled: activeSessionId.value === null,
        action: scrollActivityToBottom,
      },
      {
        id: "clear-conversation",
        label: "Clear Draft",
        description: activeSessionId.value ? "Clear the current composer draft." : "No active session selected.",
        icon: Eraser,
        category: "Session",
        paletteHotkey: bindings.value["clear-conversation"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["clear-conversation"]?.globalShortcut ?? undefined,
        keywords: ["clear", "draft", "composer", "conversation"],
        disabled: activeSessionId.value === null,
        action: clearConversationDraft,
      },
      {
        id: "toggle-sidebar",
        label: sidebarStore.panelCollapsed ? "Show Sidebar" : "Hide Sidebar",
        description: sidebarStore.panelCollapsed ? "Expand the left context panel." : "Collapse the left context panel.",
        icon: PanelLeftClose,
        category: "View",
        paletteHotkey: bindings.value["toggle-sidebar"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["toggle-sidebar"]?.globalShortcut ?? undefined,
        keywords: ["panel", "menu", "collapse", "expand", "sidebar"],
        action: () => sidebarStore.togglePanelCollapsed(),
      },
      {
        id: "toggle-right-panel",
        label: sidebarStore.rightPanelCollapsed ? "Show Right Panel" : "Hide Right Panel",
        description: sidebarStore.rightPanelCollapsed ? "Expand the right detail panel." : "Collapse the right detail panel.",
        icon: PanelRightClose,
        category: "View",
        globalShortcut: bindings.value["toggle-right-panel"]?.globalShortcut ?? undefined,
        keywords: ["panel", "details", "todo", "collapse", "expand", "right"],
        action: () => sidebarStore.toggleRightPanelCollapsed(),
      },
      {
        id: "toggle-diff-view",
        label: workspaceUiStore.inlineToolDiffs ? "Hide Inline Tool Diffs" : "Show Inline Tool Diffs",
        description: workspaceUiStore.inlineToolDiffs
          ? "Collapse inline diff rendering in tool cards."
          : "Show inline diff rendering in tool cards.",
        icon: LayoutGrid,
        category: "View",
        paletteHotkey: bindings.value["toggle-diff-view"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["toggle-diff-view"]?.globalShortcut ?? undefined,
        keywords: ["diff", "patch", "changes", "inline"],
        action: () => workspaceUiStore.toggleInlineToolDiffs(),
      },
      {
        id: "toggle-activity-filter",
        label: retentionStatus.value === "active" ? "Show All Sessions" : "Show Active Sessions",
        description: retentionStatus.value === "active"
          ? "Toggle the sessions filter from active to all."
          : "Toggle the sessions filter back to active only.",
        icon: MessageSquare,
        category: "View",
        paletteHotkey: bindings.value["toggle-activity-filter"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["toggle-activity-filter"]?.globalShortcut ?? undefined,
        keywords: ["filter", "activity", "sessions", "retention"],
        action: toggleActivityFilter,
      },
      {
        id: "cycle-theme",
        label: "Cycle Theme",
        description: `Current theme: ${currentTheme.value}.`,
        icon: MoonStar,
        category: "View",
        globalShortcut: bindings.value["cycle-theme"]?.globalShortcut ?? undefined,
        keywords: ["theme", "appearance", "dark", "light"],
        action: cycleTheme,
      },
      {
        id: "toggle-dark-light",
        label: themeStore.resolvedTheme === "dark" ? "Switch to Light Mode" : "Switch to Dark Mode",
        description: `Current resolved theme: ${themeStore.resolvedTheme}.`,
        icon: MoonStar,
        category: "View",
        paletteHotkey: bindings.value["toggle-dark-light"]?.paletteHotkey ?? undefined,
        globalShortcut: bindings.value["toggle-dark-light"]?.globalShortcut ?? undefined,
        keywords: ["theme", "dark", "light", "toggle"],
        action: () => themeStore.toggleTheme(),
      },
      {
        id: "toggle-fullscreen",
        label: "Toggle Fullscreen",
        description: "Enter or exit fullscreen mode.",
        icon: Maximize2,
        category: "View",
        globalShortcut: bindings.value["toggle-fullscreen"]?.globalShortcut ?? undefined,
        keywords: ["fullscreen", "maximize", "screen"],
        action: toggleFullscreen,
      },
      {
        id: "zoom-in",
        label: "Zoom In",
        description: "Increase the interface scale.",
        icon: ZoomIn,
        category: "View",
        globalShortcut: bindings.value["zoom-in"]?.globalShortcut ?? undefined,
        keywords: ["bigger", "larger", "increase", "font"],
        action: () => zoomDocument(10),
      },
      {
        id: "zoom-out",
        label: "Zoom Out",
        description: "Decrease the interface scale.",
        icon: ZoomOut,
        category: "View",
        globalShortcut: bindings.value["zoom-out"]?.globalShortcut ?? undefined,
        keywords: ["smaller", "decrease", "font"],
        action: () => zoomDocument(-10),
      },
      {
        id: "open-marketplace-panel",
        label: "Open Marketplace Panel",
        description: "Show plugin and integration options.",
        icon: Puzzle,
        category: "Fleet",
        keywords: ["plugins", "integrations", "marketplace"],
        action: () => sidebarStore.setActiveRail("marketplace"),
      },
    ];
  });

  useKeyboardShortcut("k", () => {
    commandStore.setPaletteOpen(!commandStore.paletteOpen);
  }, {
    platformModifier: true,
    allowInEditable: true,
  });

  let managedCommandIds: string[] = [];

  watch(
    availableCommands,
    (nextCommands) => {
      const nextCommandIds = nextCommands.map((command) => command.id);

      for (const commandId of managedCommandIds) {
        if (!nextCommandIds.includes(commandId)) {
          commandStore.unregisterCommand(commandId);
        }
      }

      for (const command of nextCommands) {
        commandStore.registerCommand(command);
      }

      managedCommandIds = nextCommandIds;
    },
    { immediate: true },
  );

  function handleGlobalKeyDown(event: KeyboardEvent): void {
    if (event.defaultPrevented) {
      return;
    }

    if (event.key === "Escape") {
      if (isModalContentTarget(event.target)) {
        return;
      }

      const escapeCommand = commandStore.commands.find((command) => {
        return command.globalShortcut?.key === "Escape";
      });

      if (escapeCommand && !escapeCommand.disabled) {
        event.preventDefault();
        escapeCommand.action();
        return;
      }
    }

    if (isEditableTarget(event.target)) {
      return;
    }

    for (const command of commandStore.commands) {
      if (!command.globalShortcut || command.disabled) {
        continue;
      }

      if (!matchesKeyboardShortcut(event, command.globalShortcut)) {
        continue;
      }

      event.preventDefault();
      command.action();
      break;
    }
  }

  onMounted(() => {
    if (typeof document === "undefined") {
      return;
    }

    document.addEventListener("keydown", handleGlobalKeyDown);
  });

  onUnmounted(() => {
    if (typeof document !== "undefined") {
    document.removeEventListener("keydown", handleGlobalKeyDown);
    }

    for (const commandId of managedCommandIds) {
      commandStore.unregisterCommand(commandId);
    }

    managedCommandIds = [];
  });

  return {
    commandStore,
  };
}
