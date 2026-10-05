import { Outlet, createFileRoute } from "@tanstack/vue-router";

/** The phone pages: their frame and sign-in come from the root layout (PhoneShell + PhoneAuthGate). */
export const Route = createFileRoute("/phone")({
  component: () => <Outlet />,
});
