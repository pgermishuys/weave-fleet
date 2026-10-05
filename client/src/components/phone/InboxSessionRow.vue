<script setup lang="ts">
import { computed } from "vue";
import { rowInfo, type InboxItem } from "@/lib/phone/inbox";
import { ago, duration } from "@/lib/phone/time";

/** A working or finished session: a status dot, its title and one line, 44px+ tall. Tapping opens it. */
const props = defineProps<{ item: InboxItem; now: number }>();
const emit = defineEmits<{ (event: "open", item: InboxItem): void }>();

const tone = computed(() => {
  if (props.item.status === "active") return "run";
  if (props.item.status === "waiting_input") return "wait";
  if (props.item.status === "error") return "error";
  return "done";
});
const info = computed(() => props.item.status === "active"
  ? rowInfo(props.item, duration(props.item.updatedAt, props.now))
  : `${props.item.machineName} · ${ago(props.item.updatedAt, props.now)}`);
</script>

<template>
  <button
    type="button"
    class="srow"
    :class="{ 'srow--stale': item.stale }"
    data-testid="inbox-row"
    @click="emit('open', item)"
  >
    <span
      class="srow__dot"
      :class="`srow__dot--${tone}`"
      aria-hidden="true"
    />
    <span class="srow__body">
      <span class="srow__name">{{ item.title }}</span>
      <span class="srow__info">{{ info }}</span>
    </span>
  </button>
</template>

<style scoped>
.srow {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 52px;
  padding: 8px 12px;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.srow + .srow {
  border-top: 1px solid var(--border);
}

.srow--stale {
  opacity: 0.6;
}

.srow__dot {
  width: 8px;
  height: 8px;
  flex: none;
  border-radius: 50%;
  background: var(--muted);
}

.srow__dot--run {
  background: var(--running);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--running) 22%, transparent);
}

.srow__dot--wait {
  background: var(--idle);
}

.srow__dot--error {
  background: var(--error);
}

.srow__dot--done {
  background: var(--complete);
}

.srow__body {
  display: grid;
  min-width: 0;
}

.srow__name {
  overflow: hidden;
  font-size: 14px;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.srow__info {
  overflow: hidden;
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
  text-overflow: ellipsis;
}
</style>
