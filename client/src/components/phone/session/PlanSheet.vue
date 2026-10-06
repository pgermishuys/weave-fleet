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
    :title="progress?.plan?.title ?? 'Plan'"
    :detents="['medium', 'large']"
    initial="medium"
    @close="emit('close')"
  >
    <template v-if="progress">
      <p class="ph-foot pls__count">
        {{ progress.done }} of {{ progress.total }} done
      </p>
      <template v-if="progress.plan">
        <template
          v-for="(group, index) in progress.plan.groups"
          :key="index"
        >
          <div
            v-if="group.title"
            class="ph-label"
          >
            {{ group.title }}
          </div>
          <div class="ph-card pls__group">
            <div
              v-for="step in group.steps"
              :key="step.key"
              class="ph-set ph-set--static"
              :class="{ 'pls__row--done': step.checked }"
            >
              <Check
                v-if="step.checked"
                class="ph-set__ic pls__check"
                aria-hidden="true"
              />
              <span
                v-else
                class="pls__box"
                aria-hidden="true"
              />
              <span class="ph-set__main"><span class="ph-set__t">{{ step.number ? `${step.number} ` : "" }}{{ step.title }}</span></span>
            </div>
          </div>
        </template>
      </template>
      <div
        v-else
        class="ph-card pls__group"
      >
        <div
          v-for="(todo, index) in progress.todos"
          :key="index"
          class="ph-set ph-set--static"
          :class="{ 'pls__row--done': todo.status === 'completed' }"
        >
          <Check
            v-if="todo.status === 'completed'"
            class="ph-set__ic pls__check"
            aria-hidden="true"
          />
          <span
            v-else
            class="pls__box"
            aria-hidden="true"
          />
          <span class="ph-set__main"><span class="ph-set__t">{{ todo.content }}</span></span>
        </div>
      </div>
    </template>
  </BottomSheet>
</template>

<style scoped>
.pls__count {
  margin: -6px 18px 10px;
}

.pls__group + .ph-label {
  margin-top: 18px;
}

.pls__row--done .ph-set__t {
  color: var(--muted);
}

.pls__check {
  color: var(--running);
}

.pls__box {
  width: 16px;
  height: 16px;
  flex: none;
  margin: 0 1px;
  border: 1.5px solid var(--muted);
  border-radius: calc(var(--radius-btn) - 4px);
}
</style>
