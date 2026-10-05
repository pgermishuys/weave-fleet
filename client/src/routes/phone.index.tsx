import { createFileRoute } from "@tanstack/vue-router";
import InboxPage from "@/components/phone/InboxPage.vue";

type PhoneTabSearch = { tab?: "sessions" | "machines" };

export const Route = createFileRoute("/phone/")({
  validateSearch: (search: Record<string, unknown>): PhoneTabSearch =>
    search.tab === "sessions" || search.tab === "machines" ? { tab: search.tab } : {},
  component: InboxPage,
});
