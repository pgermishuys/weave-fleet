import { createFileRoute } from "@tanstack/vue-router";
import RepositoriesPage from "@/components/pages/RepositoriesPage.vue";

export const Route = createFileRoute("/repositories")({
  component: RepositoriesPage,
});
