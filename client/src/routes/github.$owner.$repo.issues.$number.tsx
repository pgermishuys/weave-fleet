import { createFileRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";
import GitHubWorkItemDetailPage from "@/components/pages/GitHubWorkItemDetailPage.vue";

const GitHubIssueDetailRouteComponent = defineComponent({
  name: "GitHubIssueDetailRouteComponent",
  setup() {
    const params = Route.useParams();

    return () => (
      <GitHubWorkItemDetailPage
        owner={params.value.owner}
        repo={params.value.repo}
        number={params.value.number}
        kind="issue"
      />
    );
  },
});

export const Route = createFileRoute("/github/$owner/$repo/issues/$number")({
  component: GitHubIssueDetailRouteComponent,
});
