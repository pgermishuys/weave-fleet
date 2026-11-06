import { computed, watch } from "vue";
import { RotateCw } from "lucide-vue-next";
import { api, type SessionListItem } from "@/api/client";
import { updatedToVersion } from "@/composables/use-update-notices";
import { useMachinesStore, type MachineEntry, type MachineSessions } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";
import { useSessionsStore } from "@/stores/sessions";
import { useNoticesStore, type NoticeItem } from "@/stores/notices";

const PREFIX = "restart-cut-off:";
/** The user preference ("Continue cut-off sessions by itself"), off until turned on. */
export const RESTART_CONTINUE_AUTO_PREFERENCE_KEY = "RestartContinueAuto";
/** How many cut-off sessions the card lists before "+N more". */
const LISTED = 5;

/** "Fleet updated to v0.66.0" after an update, else "Fleet restarted". */
export function restartTitle(updatedTo: string | null): string {
  return updatedTo ? `Fleet updated to v${updatedTo.replace(/^v/, "")}` : "Fleet restarted";
}

export function cutOffText(count: number): string {
  return `${count} ${count === 1 ? "session was" : "sessions were"} cut off.`;
}

/** One line per other machine whose sessions a restart cut off: "build-box restarted: 1 session cut off". */
export function otherMachineLines(entries: readonly MachineEntry[], sessionsByMachine: Record<string, MachineSessions>): string[] {
  const lines: string[] = [];
  for (const entry of entries) {
    if (entry.isLive) continue;
    const count = (sessionsByMachine[entry.key]?.sessions ?? []).filter((item) => item.interrupted).length;
    if (count > 0) lines.push(`${entry.name} restarted: ${count} ${count === 1 ? "session" : "sessions"} cut off`);
  }
  return lines;
}

export function continuingText(count: number): string {
  return `Continuing ${count} ${count === 1 ? "session" : "sessions"}.`;
}

function itemFor(item: SessionListItem): NoticeItem {
  const title = item.session.title?.trim() || "Untitled session";
  return { text: item.interrupted?.waiting ? `${title}: the question was lost. Ask again.` : title };
}

/**
 * After Fleet restarts with sessions mid-turn, says so once: which sessions were cut off, with Continue all and
 * Dismiss all. Continue sends each session's own message as the user's click; Dismiss sends nothing.
 */
export function useRestartNotice(): void {
  const notices = useNoticesStore();
  const sessionsStore = useSessionsStore();
  const preferences = usePreferencesStore();
  const machines = useMachinesStore();
  const cutOff = computed(() => sessionsStore.sessions.filter((item) => item.interrupted));
  const machineLines = computed(() => otherMachineLines(machines.entries, machines.others));
  /** The most sessions Fleet was continuing by itself in this restart, so the notice keeps its count as the sends go out. */
  let continuing = 0;

  async function eachSession(ids: string[], request: (id: string) => Promise<unknown>): Promise<void> {
    await Promise.allSettled(ids.map(request));
  }

  /** Stop all: nothing not yet sent is sent; the cards stay with Continue. Never stops a running turn. */
  async function stopAutomatic(): Promise<void> {
    continuing = 0;
    await api.POST("/api/sessions/interruptions/stop-automatic");
  }

  function show(): void {
    const items = cutOff.value;
    const existing = notices.notices.find((notice) => notice.id.startsWith(PREFIX));
    const lines = machineLines.value;
    if (items.length === 0 && lines.length === 0) {
      continuing = 0;
      if (existing) notices.remove(existing.id);
      return;
    }
    if (items.length === 0) {
      // Only another machine restarted: that machine's own notice has the buttons.
      const id = `${PREFIX}machines:${lines.join("|")}`;
      if (existing && existing.id !== id) notices.remove(existing.id);
      notices.post({ id, icon: RotateCw, tone: "warn", title: "A machine restarted", items: lines.map((text) => ({ text })), holdMs: 30_000 });
      return;
    }
    const automatic = items.filter((item) => item.interrupted?.auto).length;
    continuing = Math.max(continuing, automatic);
    const at = items.map((item) => item.interrupted?.at ?? "").sort().at(-1);
    const id = `${PREFIX}${at}`;
    const ids = items.map((item) => item.session.id);
    const done = () => notices.remove(id);
    if (existing && existing.id !== id) notices.remove(existing.id);
    notices.post({
      id,
      icon: RotateCw,
      tone: "warn",
      title: restartTitle(updatedToVersion.value),
      body: automatic > 0 ? continuingText(continuing) : cutOffText(items.length),
      items: [...items.slice(0, LISTED).map(itemFor), ...lines.map((text) => ({ text }))],
      more: items.length > LISTED ? `+${items.length - LISTED} more` : undefined,
      holdMs: 30_000,
      link: automatic > 0 || preferences.get(RESTART_CONTINUE_AUTO_PREFERENCE_KEY, "false") === "true"
        ? undefined
        : {
            label: "Next time, continue these by itself?",
            href: "#",
            run: () => void preferences.set(RESTART_CONTINUE_AUTO_PREFERENCE_KEY, "true"),
          },
      onEscape: automatic > 0 ? () => void stopAutomatic() : undefined,
      actions: automatic > 0
        ? [{ label: "Stop all", tone: "danger", run: stopAutomatic }]
        : [
        {
          label: "Continue all",
          tone: "primary",
          run: async () => {
            await eachSession(ids, (sessionId) =>
              api.POST("/api/sessions/{id}/interruption/continue", { params: { path: { id: sessionId } } }),
            );
            done();
          },
        },
        {
          label: "Dismiss all",
          run: async () => {
            await eachSession(ids, (sessionId) => api.DELETE("/api/sessions/{id}/interruption", { params: { path: { id: sessionId } } }));
            done();
          },
        },
      ],
    });
  }

  watch([cutOff, updatedToVersion, machineLines, () => preferences.get(RESTART_CONTINUE_AUTO_PREFERENCE_KEY, "false")], show, { immediate: true });
}
