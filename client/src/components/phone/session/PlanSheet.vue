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
      <p class="ph-group-f pls__count">
        {{ progress.done }} of {{ progress.total }} done
      </p>
      <template v-if="progress.plan">
        <template
          v-for="(group, index) in progress.plan.groups"
          :key="index"
        >
          <div
            v-if="group.title"
            class="ph-group-h"
          >
            {{ group.title }}
          </div>
          <div class="ph-group pls__group">
            <div
              v-for="step in group.steps"
              :key="step.key"
              class="ph-row ph-row--static"
              :class="{ 'pls__row--done': step.checked }"
            >
              <Check
                v-if="step.checked"
                class="pls__check"
                :size="20"
                :stroke-width="2.6"
                aria-hidden="true"
              />
              <span
                v-else
                class="pls__box"
                aria-hidden="true"
              />
              <span class="ph-row__main"><span class="ph-row__title ph-row__title--wrap">{{ step.number ? `${step.number} ` : "" }}{{ step.title }}</span></span>
            </div>
          </div>
        </template>
      </template>
      <div
        v-else
        class="ph-group pls__group"
      >
        <div
          v-for="(todo, index) in progress.todos"
          :key="index"
          class="ph-row ph-row--static"
          :class="{ 'pls__row--done': todo.status === 'completed' }"
        >
          <Check
            v-if="todo.status === 'completed'"
            class="pls__check"
            :size="20"
            :stroke-width="2.6"
            aria-hidden="true"
          />
          <span
            v-else
            class="pls__box"
            aria-hidden="true"
          />
          <span class="ph-row__main"><span class="ph-row__title ph-row__title--wrap">{{ todo.content }}</span></span>
        </div>
      </div>
    </template>
  </BottomSheet>
</template>

<style scoped>
.pls__count {
  margin: 0 32px 10px;
}

.pls__group + .ph-group-h {
  margin-top: 18px;
}

.pls__row--done .ph-row__title {
  color: var(--muted);
}

.pls__check {
  flex: none;
  color: var(--running);
}

.pls__box {
  width: 18px;
  height: 18px;
  flex: none;
  margin: 0 1px;
  border: 2px solid var(--muted);
  border-radius: 5px;
}
</style>
