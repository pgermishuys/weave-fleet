import { onScopeDispose, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Radar } from "lucide-vue-next";
import { YOUR_FLEET_PREFERENCE_KEY } from "@/lib/your-fleet";
import { useMachinesStore } from "@/stores/machines";
import { useNoticesStore } from "@/stores/notices";
import { usePreferencesStore } from "@/stores/preferences";
import { useSettingsNav } from "@/composables/use-settings-nav";

/** How often a screen looks for machines that want to join. One look listens for a couple of seconds, so it isn't often. */
export const JOIN_LOOK_EVERY_MS = 20_000;

/** The notice's id for one machine, so the same machine is never announced twice at once. */
export const joinNoticeId = (machineId: string): string => `wants-to-join:${machineId}`;

/**
 * With Your Fleet on (Settings → Labs), says "atlas wants to join your Fleet" on this screen while atlas is waiting (it
 * ran `fleet node pair` or `fleet node up`). Every connected screen looks on its own, so every one says it. Adding goes
 * through Add a machine → Found, where the code is checked: the notice only gets you there. It goes away when the
 * machine is added or stops waiting.
 */
export function useJoinPrompt(): void {
  const notices = useNoticesStore();
  const machines = useMachinesStore();
  const preferences = usePreferencesStore();
  const router = useRouter();
  const { openAddMachine } = useSettingsNav();
  preferences.ensureLoaded();

  const announced = new Set<string>();
  let timer: ReturnType<typeof setTimeout> | null = null;
  let stopped = false;

  function addIt(): void {
    openAddMachine();
    void router.navigate({ to: "/settings" });
  }

  async function look(): Promise<void> {
    await machines.loadFound();
    const waiting = machines.foundMachines ?? [];
    const now = new Set(waiting.map((machine) => machine.machineId));
    for (const machine of waiting) {
      announced.add(machine.machineId);
      notices.post({
        id: joinNoticeId(machine.machineId),
        title: `${machine.name} wants to join your Fleet`,
        body: `It is waiting on ${machine.source === "Tailscale" ? "Tailscale" : "your home network"}. Check the code it shows, then add it.`,
        icon: Radar,
        chip: `${machine.name} wants to join`,
        onChipClick: addIt,
        actions: [
          { label: `Add ${machine.name}`, tone: "primary", run: () => { addIt(); notices.settle(joinNoticeId(machine.machineId)); } },
          { label: "Not now", run: () => notices.settle(joinNoticeId(machine.machineId)) },
        ],
      });
    }
    for (const id of [...announced]) {
      if (now.has(id)) continue;
      announced.delete(id);
      notices.remove(joinNoticeId(id));
    }
  }

  function schedule(): void {
    if (stopped) return;
    timer = setTimeout(() => { void look().finally(schedule); }, JOIN_LOOK_EVERY_MS);
  }

  function start(): void {
    if (timer) return;
    void look().finally(schedule);
  }

  function stop(): void {
    if (timer) clearTimeout(timer);
    timer = null;
    for (const id of announced) notices.remove(joinNoticeId(id));
    announced.clear();
  }

  watch(
    () => [preferences.hasFetched, preferences.get(YOUR_FLEET_PREFERENCE_KEY, "false")] as const,
    ([fetched, value]) => {
      if (fetched && value === "true") start();
      else stop();
    },
    { immediate: true },
  );

  onScopeDispose(() => {
    stopped = true;
    stop();
  });
}
