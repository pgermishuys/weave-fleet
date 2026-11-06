import { createFileRoute } from "@tanstack/vue-router";
import AnalyticsPage from "@/components/analytics/AnalyticsPage.vue";

// Overview. The other sections are /analytics/$section; the page reads which one from the address.
export const Route = createFileRoute("/analytics/")({
  component: AnalyticsPage,
});
