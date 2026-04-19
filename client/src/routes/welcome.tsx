import { createFileRoute } from "@tanstack/vue-router";
import WelcomePage from "@/components/pages/WelcomePage.vue";

export const Route = createFileRoute("/welcome")({
  component: WelcomePage,
});
