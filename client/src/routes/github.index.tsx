import { createFileRoute } from "@tanstack/vue-router";
import GitHubBrowserPage from "@/plugins/builtin/github/pages/GitHubBrowserPage.vue";

export const Route = createFileRoute("/github/")({
  component: GitHubBrowserPage,
});
