import { createFileRoute } from "@tanstack/vue-router";
import PairPage from "@/components/phone/PairPage.vue";

export const Route = createFileRoute("/pair")({
  component: PairPage,
});
