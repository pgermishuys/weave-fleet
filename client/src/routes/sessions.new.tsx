import { createFileRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";
import NewSessionForm from "@/components/sessions/NewSessionForm.vue";

const NewSessionPage = defineComponent({
  name: "NewSessionPage",
  setup() {
    return () => (
      <div
        style={{
          display: "flex",
          height: "100%",
          minHeight: 0,
          flexDirection: "column",
          overflow: "hidden",
        }}
      >
        <NewSessionForm />
      </div>
    );
  },
});

export const Route = createFileRoute("/sessions/new")({
  validateSearch: (search: Record<string, unknown>) => ({
    projectId: typeof search.projectId === "string" ? search.projectId : undefined,
    source: typeof search.source === "string" ? search.source : undefined,
  }),
  component: NewSessionPage,
});
