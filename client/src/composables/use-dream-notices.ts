import { onMounted, onUnmounted, watch } from "vue";
import { useNavigate } from "@tanstack/vue-router";
import { Moon } from "lucide-vue-next";
import { onDomainEvent } from "@/composables/on-domain-event";
import { useDreamingFeature } from "@/composables/use-dreaming-feature";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { appliedDreamChanges, describeDreamChanges, getNightlyDreaming, MEMORY_DREAMED, type DreamView } from "@/lib/dreaming";
import { liveTarget } from "@/lib/machine-target";
import { useNoticesStore } from "@/stores/notices";

/**
 * With the Dreaming switch on: tells the user once, in the morning, that a night's dream updated a repository's notes,
 * and opens Settings → Memory on that repository. Off, nothing is asked and nothing is shown.
 */
export function useDreamNotices(): void {
  const notices = useNoticesStore();
  const navigate = useNavigate();
  const { isDreamingEnabled } = useDreamingFeature();
  const { openMemory } = useSettingsNav();
  let unsubscribe: (() => void) | null = null;

  function show(dream: DreamView): void {
    if (!dream.runId || dream.status !== "done" || appliedDreamChanges(dream).length === 0) return;
    const id = `dream-${dream.id}`;
    notices.post({
      id,
      title: `Fleet updated ${dream.repositoryName}'s notes`,
      body: describeDreamChanges(dream),
      icon: Moon,
      actions: [
        {
          label: "See what changed",
          tone: "primary",
          run: () => {
            openMemory(dream.repository);
            void navigate({ to: "/settings" });
            notices.settle(id);
          },
        },
      ],
    });
  }

  async function catchUp(): Promise<void> {
    try {
      for (const dream of (await getNightlyDreaming()).recent) show(dream);
    } catch {
      // Nothing to say about last night; Settings → Memory shows the error if it matters.
    }
  }

  function stop(): void {
    unsubscribe?.();
    unsubscribe = null;
  }

  function start(): void {
    if (unsubscribe) return;
    void catchUp();
    unsubscribe = onDomainEvent(liveTarget(), "sessions", MEMORY_DREAMED, (event) => {
      const dream = event.payload as DreamView | undefined;
      if (dream?.id) show(dream);
    });
  }

  onMounted(() => {
    watch(isDreamingEnabled, (on) => (on ? start() : stop()), { immediate: true });
  });
  onUnmounted(stop);
}
