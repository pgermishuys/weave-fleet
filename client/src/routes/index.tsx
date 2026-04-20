import { createFileRoute } from "@tanstack/vue-router";
import FleetDashboard from "@/components/dashboard/FleetDashboard.vue";

export const Route = createFileRoute("/")({
  component: HomePage,
});

function HomePage() {
  return <FleetDashboard />;
}
