import { createFileRoute } from "@tanstack/vue-router";
import { PhoneRouteStub } from "@/components/phone/phone-route-stub";

type PhoneTabSearch = { tab?: "sessions" | "machines" };

/** The inbox (PhoneStack draws it). */
export const Route = createFileRoute("/phone/")({
  validateSearch: (search: Record<string, unknown>): PhoneTabSearch =>
    search.tab === "sessions" || search.tab === "machines" ? { tab: search.tab } : {},
  component: PhoneRouteStub,
});
