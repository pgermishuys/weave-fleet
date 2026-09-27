import { createFileRoute } from "@tanstack/vue-router";
import { computed, defineComponent } from "vue";
import NewSessionComposer from "@/components/sessions/NewSessionComposer.vue";
import { liveTarget, provideMachineTarget, targetFor } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";
import { useSessionsStore } from "@/stores/sessions";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

const NewSessionPage = defineComponent({
  name: "NewSessionPage",
  setup() {
    // Nothing is open yet, so the sidebar and status bar shouldn't show the last session.
    useSessionsStore().setActiveSessionId(null);

    // The draft starts on the machine picked for it, else the live one. Everything in the box asks that machine; the
    // box is rebuilt when it changes.
    const workspaceUi = useWorkspaceUiStore();
    const machines = useMachinesStore();
    const target = computed(() => {
      const picked = machines.entries.find((entry) => entry.key === workspaceUi.newSessionMachine);
      return picked ? targetFor(picked.connection) : liveTarget();
    });
    provideMachineTarget(() => target.value);

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
        <NewSessionComposer key={target.value.key} />
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
