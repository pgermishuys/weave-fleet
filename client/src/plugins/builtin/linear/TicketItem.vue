<script setup lang="ts">
import { computed } from "vue";

type LinearTicketStatus = "todo" | "in-progress" | "done";
type LinearTicketPriority = 1 | 2 | 3 | 4;

interface LinearTicket {
  id: string;
  title: string;
  status: LinearTicketStatus;
  priority: LinearTicketPriority;
}

interface Props {
  ticket: LinearTicket;
}

const props = defineProps<Props>();

const statusLabels: Record<LinearTicketStatus, string> = {
  todo: "Todo",
  "in-progress": "In Progress",
  done: "Done",
};

const priorityLabels: Record<LinearTicketPriority, string> = {
  1: "Urgent",
  2: "High",
  3: "Normal",
  4: "Low",
};

const priorityColors: Record<LinearTicketPriority, string> = {
  1: "#ef4444",
  2: "#f59e0b",
  3: "#3b82f6",
  4: "#71717a",
};

const statusLabel = computed(() => statusLabels[props.ticket.status]);
const priorityLabel = computed(() => priorityLabels[props.ticket.priority]);
const priorityStyle = computed(() => ({ color: priorityColors[props.ticket.priority] }));
</script>

<template>
  <article class="ticket-item" tabindex="0" role="button" :aria-label="`${ticket.id} ${ticket.title}`">
    <span class="ticket-priority" :style="priorityStyle" :title="priorityLabel" aria-hidden="true">●</span>
    <span class="ticket-id">{{ ticket.id }}</span>

    <div class="ticket-body">
      <p class="ticket-title">{{ ticket.title }}</p>
    </div>

    <span class="ticket-status" :class="ticket.status">{{ statusLabel }}</span>
  </article>
</template>

<style scoped>
.ticket-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  cursor: pointer;
  transition: background-color 0.15s ease;
}

.ticket-item:hover,
.ticket-item:focus-visible {
  background: rgba(255, 255, 255, 0.03);
  outline: none;
}

.ticket-id {
  font-size: 12px;
  font-weight: 700;
  color: var(--muted);
  flex-shrink: 0;
  width: 56px;
}

.ticket-body {
  flex: 1;
  min-width: 0;
}

.ticket-title {
  margin: 0;
  font-size: 13px;
  color: var(--text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.ticket-status {
  padding: 2px 6px;
  font-size: 9px;
  border-radius: 10px;
  font-weight: 600;
  flex-shrink: 0;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.ticket-status.in-progress {
  background: rgba(245, 158, 11, 0.15);
  color: #f59e0b;
}

.ticket-status.todo {
  background: rgba(113, 113, 122, 0.15);
  color: var(--muted);
}

.ticket-status.done {
  background: rgba(34, 197, 94, 0.15);
  color: #22c55e;
}

.ticket-priority {
  font-size: 10px;
  flex-shrink: 0;
}
</style>
