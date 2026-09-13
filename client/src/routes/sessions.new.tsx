import { createFileRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";
import NewSessionComposer from "@/components/sessions/NewSessionComposer.vue";
import { useSessionsStore } from "@/stores/sessions";

const NewSessionPage = defineComponent({
  name: "NewSessionPage",
  setup() {
    // Nothing is open yet, so the sidebar and status bar shouldn't show the last session.
    useSessionsStore().setActiveSessionId(null);

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
        <NewSessionComposer />
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
