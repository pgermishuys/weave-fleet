import { createFileRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";
import GitHubWorkItemDetailPage from "@/components/pages/GitHubWorkItemDetailPage.vue";

const GitHubPullRequestDetailRouteComponent = defineComponent({
  name: "GitHubPullRequestDetailRouteComponent",
  setup() {
    const params = Route.useParams();

    return () => (
      <GitHubWorkItemDetailPage
        owner={params.value.owner}
        repo={params.value.repo}
        number={params.value.number}
        kind="pull"
      />
    );
  },
});

export const Route = createFileRoute("/github/$owner/$repo/pulls/$number")({
  component: GitHubPullRequestDetailRouteComponent,
});
