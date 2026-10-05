import { createFileRoute } from "@tanstack/vue-router";
import { PhoneRouteStub } from "@/components/phone/phone-route-stub";

/** Turning on notifications, a sheet over the inbox (PhoneStack draws it). */
export const Route = createFileRoute("/phone/setup")({
  component: PhoneRouteStub,
});
