import { createFileRoute } from "@tanstack/vue-router";
import { Outlet } from "@tanstack/vue-router";
import { defineComponent } from "vue";

const GitHubLayout = defineComponent({
  name: "GitHubLayout",
  setup() {
    return () => <Outlet />;
  },
});

export const Route = createFileRoute("/github")({
  component: GitHubLayout,
});
