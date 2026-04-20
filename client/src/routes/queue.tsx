import { createFileRoute } from "@tanstack/vue-router";
import QueuePage from "@/components/pages/QueuePage.vue";

export const Route = createFileRoute("/queue")({
  component: QueuePage,
});
