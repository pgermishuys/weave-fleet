import { defineComponent, onMounted, watch } from "vue";
import { createFileRoute, useRouter } from "@tanstack/vue-router";
import FleetDashboard from "@/components/dashboard/FleetDashboard.vue";
import { useMissionControlFeature } from "@/composables/use-mission-control-feature";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * Fleet installed on a phone's Home Screen opens on the phone inbox, not the desktop dashboard. A phone browser that
 * isn't installed keeps the dashboard; the top bar links to the phone view.
 */
export function opensAsPhoneApp(search: string, matches: (query: string) => boolean): boolean {
  // The phone's "Open the full Fleet" asks for the dashboard itself.
  if (new URLSearchParams(search).get("view") === "full") return false;
  const installed = matches("(display-mode: standalone)") || new URLSearchParams(search).get("source") === "pwa";
  return installed && matches("(max-width: 716px)");
}

// Fleet opened at "/": with Mission control on that's where it lands, once. Opened on a deep link, or back at "/" from
// Sessions, it stays where it is.
let landing = typeof window !== "undefined" && window.location.pathname === "/";

export const HomePage = defineComponent({
  name: "HomePage",
  setup() {
    const router = useRouter();
    const preferences = usePreferencesStore();
    const { isMissionControlEnabled } = useMissionControlFeature();
    // Only the first showing of "/" at app start may land; later ones (Sessions) never do.
    let mayLand = landing;
    landing = false;
    watch(
      () => [preferences.hasFetched, isMissionControlEnabled.value] as const,
      ([loaded, enabled]) => {
        if (!mayLand || !loaded) return;
        mayLand = false;
        if (enabled) void router.navigate({ to: "/fleet", replace: true });
      },
      { immediate: true },
    );
    onMounted(() => {
      if (typeof window.matchMedia !== "function") return;
      if (opensAsPhoneApp(window.location.search, (query) => window.matchMedia(query).matches)) {
        void router.navigate({ to: "/phone", replace: true });
      }
    });
    return () => <FleetDashboard />;
  },
});

export const Route = createFileRoute("/")({
  component: HomePage,
});
