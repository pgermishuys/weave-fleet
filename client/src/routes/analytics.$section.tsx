import { createFileRoute, redirect } from "@tanstack/vue-router";
import AnalyticsPage from "@/components/analytics/AnalyticsPage.vue";
import { isAnalyticsSectionId } from "@/lib/analytics-sections";

export const Route = createFileRoute("/analytics/$section")({
  // A section that doesn't exist (an old or mistyped link) opens Overview.
  beforeLoad: ({ params }) => {
    if (!isAnalyticsSectionId(params.section)) throw redirect({ to: "/analytics" });
  },
  component: AnalyticsPage,
});
