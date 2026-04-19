import { createFileRoute } from "@tanstack/vue-router";
import PipelinesPage from "@/components/pages/PipelinesPage.vue";

export const Route = createFileRoute("/pipelines")({
  component: PipelinesPage,
});
