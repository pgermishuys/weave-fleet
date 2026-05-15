import { createFileRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";
import GitHubRepoPage from "@/components/pages/GitHubRepoPage.vue";

const GitHubRepoRouteComponent = defineComponent({
  name: "GitHubRepoRouteComponent",
  setup() {
    const params = Route.useParams();

    return () => (
      <GitHubRepoPage
        owner={params.value.owner}
        repo={params.value.repo}
      />
    );
  },
});

export const Route = createFileRoute("/github/$owner/$repo")({
  component: GitHubRepoRouteComponent,
});
