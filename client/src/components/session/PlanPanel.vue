<script setup lang="ts">
import { computed } from "vue";
import { storeToRefs } from "pinia";
import TodoListView from "@/components/session/TodoListView.vue";
import { useSessionTodos } from "@/composables/use-session-todos";
import { useSessionsStore } from "@/stores/sessions";

const props = defineProps<{
  sessionId?: string | null;
  instanceId?: string | null;
  sessionTitle?: string;
}>();

const sessionsStore = useSessionsStore();
const { sessions } = storeToRefs(sessionsStore);

const selectedSession = computed(() => {
  if (!props.sessionId) {
    return null;
  }

  return sessions.value.find((session) => session.session.id === props.sessionId) ?? null;
});

const resolvedSessionId = computed(() => props.sessionId ?? "");
const resolvedInstanceId = computed(() => props.instanceId ?? selectedSession.value?.instanceId ?? "");

const { todos } = useSessionTodos(resolvedSessionId, resolvedInstanceId);

const panelTitle = computed(() => props.sessionTitle ? `${props.sessionTitle} Plan` : "Execution Plan");
</script>

<template>
  <section
    class="plan-panel"
    aria-label="Session plan"
  >
    <header class="plan-header">
      <p class="plan-eyebrow">
        Todos
      </p>
      <h3 class="plan-title">
        {{ panelTitle }}
      </h3>
    </header>

    <TodoListView
      :todos="todos"
      :aria-label="panelTitle"
    />
  </section>
</template>

<style scoped>
.plan-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.plan-header {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.plan-eyebrow {
  margin: 0;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--muted);
}

.plan-title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}
</style>
