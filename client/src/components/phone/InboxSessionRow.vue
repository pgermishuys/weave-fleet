<script setup lang="ts">
import { computed } from "vue";
import { sessionLine, type InboxItem } from "@/lib/phone/inbox";
import { ago, duration } from "@/lib/phone/time";

/** A session as a list row: its status dot, title and one line ("falcon · Working · 6m"). Tapping opens it. */
const props = defineProps<{ item: InboxItem; now: number }>();
const emit = defineEmits<{ (event: "open", item: InboxItem): void }>();

const tone = computed(() => {
  if (props.item.status === "active") return "running";
  if (props.item.status === "waiting_input") return "waiting";
  if (props.item.status === "error") return "error";
  return "done";
});
const info = computed(() => sessionLine(props.item, { duration: duration(props.item.updatedAt, props.now), ago: ago(props.item.updatedAt, props.now) }));
</script>

<template>
  <button
    type="button"
    class="ph-row"
    :class="{ 'ph-row--stale': item.stale }"
    style="--ph-sep-left: 44px"
    data-testid="inbox-row"
    @click="emit('open', item)"
  >
    <span
      class="ph-dot"
      :class="`ph-dot--${tone}`"
      aria-hidden="true"
    />
    <span class="ph-row__main">
      <span class="ph-row__title">{{ item.title }}</span>
      <span class="ph-row__sub">{{ info }}</span>
    </span>
  </button>
</template>

<style scoped>
.ph-row__title,
.ph-row__sub {
  display: block;
}

.ph-row--stale {
  opacity: 0.6;
}
</style>
