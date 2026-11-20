import { onScopeDispose } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Radar } from "lucide-vue-next";
import { useMachinesStore } from "@/stores/machines";
import { useNoticesStore } from "@/stores/notices";
import { useSettingsNav } from "@/composables/use-settings-nav";

/** How often a screen looks for machines that want to join. One look listens for a couple of seconds, so it isn't often. */
export const JOIN_LOOK_EVERY_MS = 20_000;

/** The notice's id for one machine, so the same machine is never announced twice at once. */
export const joinNoticeId = (machineId: string): string => `wants-to-join:${machineId}`;

/**
 * Says "atlas wants to join your Fleet" on this screen while atlas is waiting (it
 * ran `fleet node pair` or `fleet node up`). Every connected screen looks on its own, so every one says it. Adding goes
 * through Add a machine → Found, where the code is checked: the notice only gets you there. It goes away when the
 * machine is added or stops waiting.
 */
export function useJoinPrompt(): void {
  const notices = useNoticesStore();
  const machines = useMachinesStore();
  const router = useRouter();
  const { openAddMachine } = useSettingsNav();

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

  start();

  onScopeDispose(() => {
    stopped = true;
    stop();
  });
}
