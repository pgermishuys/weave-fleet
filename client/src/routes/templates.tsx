import { createFileRoute } from "@tanstack/vue-router";
import TemplatesPage from "@/components/pages/TemplatesPage.vue";

export const Route = createFileRoute("/templates")({
  component: TemplatesPage,
});
