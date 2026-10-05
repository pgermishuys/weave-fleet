import { createFileRoute, useSearch } from "@tanstack/vue-router";
import PhoneNewSessionPage from "@/components/phone/new/PhoneNewSessionPage.vue";

type NewSessionSearch = { machine?: string };

export const Route = createFileRoute("/phone/new")({
  validateSearch: (search: Record<string, unknown>): NewSessionSearch =>
    typeof search.machine === "string" && search.machine ? { machine: search.machine } : {},
  component: NewSessionRoute,
});

function NewSessionRoute() {
  const search = useSearch({ from: "/phone/new" });
  return <PhoneNewSessionPage machineId={search.value.machine} />;
}
