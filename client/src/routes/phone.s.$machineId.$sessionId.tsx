import { createFileRoute } from "@tanstack/vue-router";
import PhoneSessionPage from "@/components/phone/session/PhoneSessionPage.vue";

type PhoneSessionSearch = { ask?: string };

export const Route = createFileRoute("/phone/s/$machineId/$sessionId")({
  validateSearch: (search: Record<string, unknown>): PhoneSessionSearch =>
    typeof search.ask === "string" && search.ask ? { ask: search.ask } : {},
  component: PhoneSessionPage,
});
