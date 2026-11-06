import { createFileRoute } from "@tanstack/vue-router";
import JobsPage from "@/components/pages/JobsPage.vue";

// No job picked. A job is /jobs/$id; the page reads which one from the address.
export const Route = createFileRoute("/jobs/")({
  component: JobsPage,
});
