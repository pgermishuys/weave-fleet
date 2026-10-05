import { createFileRoute } from "@tanstack/vue-router";
import NotificationSetup from "@/components/phone/NotificationSetup.vue";

export const Route = createFileRoute("/phone/setup")({
  component: NotificationSetup,
});
