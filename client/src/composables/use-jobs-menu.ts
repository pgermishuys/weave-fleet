import { computed, onMounted, onUnmounted } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import { jobGroups, jobIdFromPath, jobPath } from "@/lib/jobs";
import { useJobsStore } from "@/stores/jobs";

/** The job the address names; the list and the detail both read it, so neither passes it to the other. */
export function useSelectedJobId() {
  const pathname = useLocation({ select: (location) => location.pathname });
  return computed(() => jobIdFromPath(pathname.value));
}

/** The jobs-list: jobs grouped Running, Queued, Done, and the one open. Watches the queue while it's mounted. */
export function useJobsMenu() {
  const router = useRouter();
  const store = useJobsStore();
  const active = useSelectedJobId();
  const groups = computed(() => jobGroups(store.jobs));

  let stopWatching: (() => void) | null = null;
  onMounted(() => {
    stopWatching = store.watch();
  });
  onUnmounted(() => stopWatching?.());

  function select(id: string): void {
    if (id === active.value) return;
    void router.navigate({ to: jobPath(id) });
  }

  return { groups, active, select, isLoaded: computed(() => store.isLoaded) };
}
