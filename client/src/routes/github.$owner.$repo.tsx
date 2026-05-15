import { createFileRoute, Outlet } from "@tanstack/vue-router";
import { defineComponent } from "vue";

const GitHubRepoLayout = defineComponent({
  name: "GitHubRepoLayout",
  setup() {
    return () => <Outlet />;
  },
});

export const Route = createFileRoute("/github/$owner/$repo")({
  component: GitHubRepoLayout,
});
