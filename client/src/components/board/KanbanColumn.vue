<script setup lang="ts">
import type { BoardSession } from "@/stores/board";
import KanbanCard from "@/components/board/KanbanCard.vue";

defineProps<{
  title: string;
  color: string;
  sessions: readonly BoardSession[];
  emptyLabel: string;
}>();
</script>

<template>
  <section
    class="kanban-col"
    :aria-label="`${title} column`"
  >
    <div
      class="col-bar"
      :style="{ backgroundColor: color }"
    />

    <div class="kanban-col__header">
      <h2 class="kanban-col__title">
        {{ title }}
      </h2>
      <span class="kanban-col__count">{{ sessions.length }}</span>
    </div>

    <div class="kanban-col__cards">
      <KanbanCard
        v-for="session in sessions"
        :key="session.id"
        :session="session"
      />

      <p
        v-if="sessions.length === 0"
        class="kanban-col__empty"
      >
        {{ emptyLabel }}
      </p>
    </div>
  </section>
</template>

<style scoped>
.kanban-col {
  min-width: 280px;
  max-width: 280px;
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-height: 0;
}

.col-bar {
  height: 3px;
  border-radius: 2px;
}

.kanban-col__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.kanban-col__title {
  margin: 0;
  font-size: 14px;
  font-weight: 700;
  color: var(--text);
}

.kanban-col__count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 26px;
  height: 26px;
  padding: 0 8px;
  border-radius: 999px;
  border: 1px solid var(--border);
  background: var(--card-bg);
  font-size: 12px;
  font-weight: 600;
  color: var(--muted);
}

.kanban-col__cards {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
  min-height: 0;
  overflow-y: auto;
  padding-right: 4px;
}

.kanban-col__empty {
  margin: 0;
  padding: 18px 14px;
  border: 1px dashed var(--border);
  border-radius: var(--radius-card);
  background: rgba(255, 255, 255, 0.02);
  font-size: 12px;
  text-align: center;
  color: var(--muted);
}
</style>
