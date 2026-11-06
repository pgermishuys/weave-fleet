import { computed, watch } from "vue";
import { RotateCw } from "lucide-vue-next";
import { api, type SessionListItem } from "@/api/client";
import { updatedToVersion } from "@/composables/use-update-notices";
import { useSessionsStore } from "@/stores/sessions";
import { useNoticesStore, type NoticeItem } from "@/stores/notices";

const PREFIX = "restart-cut-off:";
/** How many cut-off sessions the card lists before "+N more". */
const LISTED = 5;

/** "Fleet updated to v0.66.0" after an update, else "Fleet restarted". */
export function restartTitle(updatedTo: string | null): string {
  return updatedTo ? `Fleet updated to v${updatedTo.replace(/^v/, "")}` : "Fleet restarted";
}

export function cutOffText(count: number): string {
  return `${count} ${count === 1 ? "session was" : "sessions were"} cut off.`;
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
  const cutOff = computed(() => sessionsStore.sessions.filter((item) => item.interrupted));

  async function eachSession(ids: string[], request: (id: string) => Promise<unknown>): Promise<void> {
    await Promise.allSettled(ids.map(request));
  }

  function show(): void {
    const items = cutOff.value;
    const existing = notices.notices.find((notice) => notice.id.startsWith(PREFIX));
    if (items.length === 0) {
      if (existing) notices.remove(existing.id);
      return;
    }
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
      body: cutOffText(items.length),
      items: items.slice(0, LISTED).map(itemFor),
      more: items.length > LISTED ? `+${items.length - LISTED} more` : undefined,
      holdMs: 30_000,
      actions: [
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

  watch([cutOff, updatedToVersion], show, { immediate: true });
}
