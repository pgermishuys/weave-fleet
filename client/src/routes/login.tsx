import { createFileRoute } from "@tanstack/vue-router";
import LoginPage from "@/components/auth/LoginPage.vue";

export const Route = createFileRoute("/login")({
  component: LoginPage,
});
