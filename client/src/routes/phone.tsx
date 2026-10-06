import { createFileRoute } from "@tanstack/vue-router";
import PhoneStack from "@/components/phone/PhoneStack.vue";

/**
 * The phone pages: their frame and sign-in come from the root layout (PhoneShell + PhoneAuthGate). PhoneStack draws
 * the inbox, sessions and the New session and Notifications sheets as a navigation stack; the child routes below only
 * say which address is which (and read its search), and draw nothing themselves.
 */
export const Route = createFileRoute("/phone")({
  component: PhoneStack,
});
