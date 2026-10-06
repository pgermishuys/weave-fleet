import { createFileRoute } from "@tanstack/vue-router";
import { PhoneRouteStub } from "@/components/phone/phone-route-stub";

type PhoneSessionSearch = { ask?: string };

/** A session, pushed over the inbox (PhoneStack draws it). */
export const Route = createFileRoute("/phone/s/$machineId/$sessionId")({
  validateSearch: (search: Record<string, unknown>): PhoneSessionSearch =>
    typeof search.ask === "string" && search.ask ? { ask: search.ask } : {},
  component: PhoneRouteStub,
});
