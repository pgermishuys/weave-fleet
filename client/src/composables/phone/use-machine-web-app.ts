import { onMounted, shallowRef } from "vue";
import { fetchOnMachine, getActiveMachine } from "@/lib/machines";
import type { MachineInfo } from "@/stores/machines";

/**
 * Whether the machine this page works in serves the web app at its own address, so a link to it opens a page. Home
 * serves this page, so it does; another machine is asked once. A node (`fleet node`) doesn't. A Fleet too old to say,
 * or one that doesn't answer, is taken to serve it, as before.
 */
export function useMachineWebApp() {
  const webApp = shallowRef(true);

  onMounted(async () => {
    const machine = getActiveMachine();
    if (!machine) return;
    try {
      const response = await fetchOnMachine(machine, "/api/machine");
      if (!response.ok) return;
      const info = await response.json() as Pick<MachineInfo, "webApp">;
      webApp.value = info.webApp !== false;
    } catch {
      // Unreachable: the page says so elsewhere; the links stay as they were.
    }
  });

  return webApp;
}
