import { createFileRoute } from "@tanstack/vue-router";
import AnalyticsPage from "@/components/analytics/AnalyticsPage.vue";

export const Route = createFileRoute("/analytics")({
  component: AnalyticsRoutePage,
});

function AnalyticsRoutePage() {
  return <AnalyticsPage />;
}
