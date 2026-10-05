import { createFileRoute } from "@tanstack/vue-router";
import { PhoneRouteStub } from "@/components/phone/phone-route-stub";

type NewSessionSearch = { machine?: string };

/** New session, a sheet over the inbox (PhoneStack draws it). */
export const Route = createFileRoute("/phone/new")({
  validateSearch: (search: Record<string, unknown>): NewSessionSearch =>
    typeof search.machine === "string" && search.machine ? { machine: search.machine } : {},
  component: PhoneRouteStub,
});
