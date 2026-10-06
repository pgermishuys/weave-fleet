import { createFileRoute } from "@tanstack/vue-router";
import AnsweredPage from "@/components/phone/AnsweredPage.vue";

type AnsweredSearch = { machine?: string; session?: string; reply?: string };

export const Route = createFileRoute("/phone/answered")({
  validateSearch: (search: Record<string, unknown>): AnsweredSearch => ({
    machine: typeof search.machine === "string" ? search.machine : undefined,
    session: typeof search.session === "string" ? search.session : undefined,
    reply: typeof search.reply === "string" ? search.reply : undefined,
  }),
  component: AnsweredPage,
});
