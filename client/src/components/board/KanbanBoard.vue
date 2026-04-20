<script setup lang="ts">
import type { BoardSession, BoardStatus } from "@/stores/board";
import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import KanbanColumn from "@/components/board/KanbanColumn.vue";
import { useBoardStore } from "@/stores/board";
import { useSidebarStore } from "@/stores/sidebar";

type KanbanColumnKey = "queued" | "running" | "in_review" | "completed" | "failed";

interface KanbanColumnDefinition {
  key: KanbanColumnKey;
  title: string;
  color: string;
  emptyLabel: string;
}

interface KanbanColumnData extends KanbanColumnDefinition {
  sessions: BoardSession[];
}

const columnDefinitions: readonly KanbanColumnDefinition[] = [
  {
    key: "queued",
    title: "Queued",
    color: "#71717a",
    emptyLabel: "No queued sessions",
  },
  {
    key: "running",
    title: "Running",
    color: "var(--running)",
    emptyLabel: "No running sessions",
  },
  {
    key: "in_review",
    title: "In Review",
    color: "#f59e0b",
    emptyLabel: "Nothing waiting for review",
  },
  {
    key: "completed",
    title: "Completed",
    color: "var(--complete)",
    emptyLabel: "No completed sessions",
  },
  {
    key: "failed",
    title: "Failed",
    color: "var(--error)",
    emptyLabel: "No failed sessions",
  },
] as const;

const boardStore = useBoardStore();
const sidebarStore = useSidebarStore();
const router = useRouter();

const { quickStats, selectedProject, sortedSessions } = storeToRefs(boardStore);

const subtitle = computed(() => {
  if (selectedProject.value === "all") {
    return "Track every session across the fleet in one place.";
  }

  return `Focused on ${selectedProject.value} while keeping the full delivery pipeline visible.`;
});

function getColumnKey(status: BoardStatus): KanbanColumnKey {
  switch (status) {
    case "idle":
      return "queued";
    case "waiting_input":
      return "in_review";
    case "completed":
      return "completed";
    case "error":
      return "failed";
    case "active":
    default:
      return "running";
  }
}

const columns = computed<KanbanColumnData[]>(() => {
  return columnDefinitions.map((definition) => ({
    ...definition,
    sessions: sortedSessions.value.filter((session) => getColumnKey(session.status) === definition.key),
  }));
});

function handleNewSession(): void {
  sidebarStore.setActiveRail("sessions");
  void router.navigate({ to: "/" });
}
</script>

<template>
  <section
    class="kanban-container"
    aria-label="Kanban board"
  >
    <header class="kanban-header">
      <div class="kanban-header__copy">
        <p class="kanban-header__eyebrow">
          Fleet board
        </p>
        <div>
          <h1 class="kanban-header__title">
            Kanban Board
          </h1>
          <p class="kanban-header__subtitle">
            {{ subtitle }}
          </p>
        </div>
      </div>

      <button
        type="button"
        class="kanban-header__button"
        @click="handleNewSession"
      >
        New Session
      </button>
    </header>

    <section
      class="kanban-summary"
      aria-label="Visible session summary"
    >
      <div class="kanban-summary__item">
        <span class="kanban-summary__value">{{ quickStats.visible }}</span>
        <span class="kanban-summary__label">Visible</span>
      </div>
      <div class="kanban-summary__item">
        <span class="kanban-summary__value">{{ quickStats.active }}</span>
        <span class="kanban-summary__label">Running</span>
      </div>
      <div class="kanban-summary__item">
        <span class="kanban-summary__value">{{ quickStats.completed }}</span>
        <span class="kanban-summary__label">Completed</span>
      </div>
      <div class="kanban-summary__item">
        <span class="kanban-summary__value">{{ quickStats.projects }}</span>
        <span class="kanban-summary__label">Projects</span>
      </div>
    </section>

    <div class="kanban-board">
      <KanbanColumn
        v-for="column in columns"
        :key="column.key"
        :title="column.title"
        :color="column.color"
        :sessions="column.sessions"
        :empty-label="column.emptyLabel"
      />
    </div>
  </section>
</template>

<style scoped>
.kanban-container {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  margin: -24px;
}

.kanban-header {
  padding: 16px 24px 12px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  border-bottom: 1px solid var(--border);
}

.kanban-header__copy {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.kanban-header__eyebrow {
  margin: 0;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.kanban-header__title {
  margin: 0;
  font-size: 28px;
  font-weight: 700;
  line-height: 1.1;
  color: var(--text);
}

.kanban-header__subtitle {
  margin: 6px 0 0;
  font-size: 14px;
  line-height: 1.5;
  color: var(--muted);
}

.kanban-header__button {
  border: 1px solid var(--accent);
  border-radius: var(--radius-btn);
  background: var(--accent);
  color: #fff;
  font-size: 13px;
  font-weight: 600;
  padding: 9px 14px;
  cursor: pointer;
  white-space: nowrap;
}

.kanban-header__button:hover {
  filter: brightness(1.05);
}

.kanban-header__button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.kanban-summary {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
  padding: 12px 24px 0;
}

.kanban-summary__item {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.kanban-summary__value {
  font-size: 20px;
  font-weight: 700;
  color: var(--text);
}

.kanban-summary__label {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.kanban-board {
  flex: 1;
  display: flex;
  gap: 16px;
  min-height: 0;
  padding: 16px 24px;
  overflow-x: auto;
  overflow-y: hidden;
}

@media (max-width: 960px) {
  .kanban-header {
    flex-direction: column;
    align-items: stretch;
  }

  .kanban-summary {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
