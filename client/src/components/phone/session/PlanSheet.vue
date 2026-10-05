<script setup lang="ts">
import { Check } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import type { SessionProgressDetail } from "@/lib/session-progress";

/** The session's plan or todos, read-only. */
defineProps<{ open: boolean; progress: SessionProgressDetail | null }>();
const emit = defineEmits<{ (event: "close"): void }>();
</script>

<template>
  <BottomSheet
    :open="open"
    label="Plan"
    @close="emit('close')"
  >
    <template v-if="progress">
      <h2 class="pls__title">
        {{ progress.plan?.title ?? "Plan" }} <span class="pls__count">{{ progress.done }} of {{ progress.total }}</span>
      </h2>
      <template v-if="progress.plan">
        <section
          v-for="(group, index) in progress.plan.groups"
          :key="index"
          class="pls__group"
        >
          <h3
            v-if="group.title"
            class="pls__group-title"
          >
            {{ group.title }}
          </h3>
          <p
            v-for="step in group.steps"
            :key="step.key"
            class="pls__row"
            :class="{ 'pls__row--done': step.checked }"
          >
            <Check
              v-if="step.checked"
              :size="14"
              aria-hidden="true"
            />
            <span
              v-else
              class="pls__box"
              aria-hidden="true"
            />
            <span>{{ step.number ? `${step.number} ` : "" }}{{ step.title }}</span>
          </p>
        </section>
      </template>
      <template v-else>
        <p
          v-for="(todo, index) in progress.todos"
          :key="index"
          class="pls__row"
          :class="{ 'pls__row--done': todo.status === 'completed' }"
        >
          <Check
            v-if="todo.status === 'completed'"
            :size="14"
            aria-hidden="true"
          />
          <span
            v-else
            class="pls__box"
            aria-hidden="true"
          />
          <span>{{ todo.content }}</span>
        </p>
      </template>
    </template>
  </BottomSheet>
</template>

<style scoped>
.pls__title {
  margin-bottom: 8px;
  font-size: 15px;
  font-weight: 600;
}

.pls__count {
  font-weight: 400;
  color: var(--muted);
}

.pls__group-title {
  margin: 10px 0 4px;
  font-size: 12px;
  font-weight: 600;
  color: var(--muted);
}

.pls__row {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  min-height: 32px;
  padding: 6px 0;
  font-size: 14px;
}

.pls__row--done {
  color: var(--muted);
}

.pls__row :deep(svg) {
  margin-top: 3px;
  color: var(--running);
}

.pls__box {
  width: 13px;
  height: 13px;
  flex: none;
  margin-top: 3px;
  border: 1.5px solid var(--muted);
  border-radius: 3px;
}
</style>
