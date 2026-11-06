import { computed } from "vue";
import { useLocation } from "@tanstack/vue-router";
import { analyticsSectionFromPath } from "@/lib/analytics-sections";

/** The Analytics section the address is on; the menu and the page both read it, so neither passes it to the other. */
export function useAnalyticsSection() {
  const pathname = useLocation({ select: (location) => location.pathname });
  return computed(() => analyticsSectionFromPath(pathname.value));
}
