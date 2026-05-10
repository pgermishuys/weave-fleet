import { createFileRoute, redirect } from "@tanstack/vue-router";
import SessionsV1Dashboard from "@/components/sessions-v1/SessionsV1Dashboard.vue";
import { useSessionsViewMode } from "@/composables/use-sessions-view-mode";

export const Route = createFileRoute("/sessions-v1")({
  beforeLoad() {
    const { isV1Enabled } = useSessionsViewMode();

    if (!isV1Enabled.value) {
      throw redirect({ to: "/" });
    }
  },
  component: SessionsV1RoutePage,
});

function SessionsV1RoutePage() {
  return <SessionsV1Dashboard />;
}
