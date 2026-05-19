import { createFileRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";
import KanbanBoard from "@/components/board/KanbanBoard.vue";
import { useBoardFeature } from "@/composables/use-board-feature";

const BoardPage = defineComponent({
  name: "BoardPage",
  setup() {
    const { isBoardFeatureEnabled } = useBoardFeature();

    return () => isBoardFeatureEnabled.value
      ? <KanbanBoard />
      : (
          <section class="mx-auto flex h-full max-w-2xl flex-col justify-center p-8 text-center">
            <h1 class="text-2xl font-semibold text-text">Board is disabled</h1>
            <p class="mt-2 text-sm text-muted">
              Enable Board from Settings → Features to show it in the left rail.
            </p>
          </section>
        );
  },
});

export const Route = createFileRoute("/board")({ component: BoardPage });
