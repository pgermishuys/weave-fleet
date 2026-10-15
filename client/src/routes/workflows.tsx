import { createFileRoute } from "@tanstack/vue-router";
import WorkflowsPage from "@/components/pages/WorkflowsPage.vue";

/** A link can open one workflow's Run box in a folder: the new-session page's Fix a bug starter does. */
type WorkflowsSearch = { workflow?: string; folder?: string };

export const Route = createFileRoute("/workflows")({
  validateSearch: (search: Record<string, unknown>): WorkflowsSearch => ({
    workflow: typeof search.workflow === "string" ? search.workflow : undefined,
    folder: typeof search.folder === "string" ? search.folder : undefined,
  }),
  component: WorkflowsPage,
});
