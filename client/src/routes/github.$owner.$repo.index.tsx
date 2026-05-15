import { createFileRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";
import GitHubRepoPage from "@/components/pages/GitHubRepoPage.vue";

const GitHubRepoIndexComponent = defineComponent({
  name: "GitHubRepoIndexComponent",
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

export const Route = createFileRoute("/github/$owner/$repo/")({
  component: GitHubRepoIndexComponent,
});
