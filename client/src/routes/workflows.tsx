import { createFileRoute } from "@tanstack/vue-router";
import WorkflowsPage from "@/components/pages/WorkflowsPage.vue";

export const Route = createFileRoute("/workflows")({
  component: WorkflowsPage,
});
