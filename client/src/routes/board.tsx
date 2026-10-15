import { createFileRoute } from "@tanstack/vue-router";
import { defineComponent } from "vue";
import FeatureOffNotice from "@/components/pages/FeatureOffNotice.vue";
import KanbanBoard from "@/components/board/KanbanBoard.vue";
import { useBoardFeature } from "@/composables/use-board-feature";

const BoardPage = defineComponent({
  name: "BoardPage",
  setup() {
    const { isBoardFeatureEnabled, setBoardFeatureEnabled } = useBoardFeature();

    return () => isBoardFeatureEnabled.value
      ? <KanbanBoard />
      : (
          <FeatureOffNotice
            title="Board"
            description="The board lays your sessions out in columns by status, so you can see what's running, waiting or done at a glance."
            turnOn={() => setBoardFeatureEnabled(true)}
          />
        );
  },
});

export const Route = createFileRoute("/board")({ component: BoardPage });
