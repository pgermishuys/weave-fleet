import { createFileRoute } from "@tanstack/vue-router";
import JobsPage from "@/components/pages/JobsPage.vue";

export const Route = createFileRoute("/jobs")({
  component: JobsPage,
});
