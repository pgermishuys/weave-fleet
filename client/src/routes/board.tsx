import { createFileRoute } from "@tanstack/vue-router";
import KanbanBoard from "@/components/board/KanbanBoard.vue";

export const Route = createFileRoute("/board")({
  component: BoardPage,
});

function BoardPage() {
  return <KanbanBoard />;
}
