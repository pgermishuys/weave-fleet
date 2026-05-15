import { createFileRoute } from "@tanstack/vue-router";
import GitHubBrowserPage from "@/components/pages/GitHubBrowserPage.vue";

export const Route = createFileRoute("/github")({
  component: GitHubBrowserPage,
});
