import { onMounted } from "vue";
import { createFileRoute, useRouter } from "@tanstack/vue-router";
import FleetDashboard from "@/components/dashboard/FleetDashboard.vue";

export const Route = createFileRoute("/")({
  component: HomePage,
});

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

function HomePage() {
  const router = useRouter();
  onMounted(() => {
    if (typeof window.matchMedia !== "function") return;
    if (opensAsPhoneApp(window.location.search, (query) => window.matchMedia(query).matches)) {
      void router.navigate({ to: "/phone", replace: true });
    }
  });
  return <FleetDashboard />;
}
