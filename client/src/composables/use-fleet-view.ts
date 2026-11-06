import { computed } from "vue";
import { useLocation } from "@tanstack/vue-router";
import { fleetViewFromPath } from "@/lib/fleet-views";

/** The Fleet view the address is on; the menu and the page both read it, so neither passes it to the other. */
export function useFleetView() {
  const pathname = useLocation({ select: (location) => location.pathname });
  return computed(() => fleetViewFromPath(pathname.value));
}
