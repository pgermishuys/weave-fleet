import { onMounted, shallowRef } from "vue";
import { useMachineTarget } from "@/lib/machine-target";
import { fetchOnMachine } from "@/lib/machines";
import type { MachineInfo } from "@/stores/machines";

/**
 * Whether the session's machine serves the web app at its own address, so a link to it opens a page. Home serves
 * this page, so it does; another machine is asked once. A node (`fleet node`) doesn't. A Fleet too old to say,
 * or one that doesn't answer, is taken to serve it, as before.
 */
export function useMachineWebApp() {
  const webApp = shallowRef(true);
  const machine = useMachineTarget().connection;

  onMounted(async () => {
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
