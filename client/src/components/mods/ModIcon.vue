<script setup lang="ts">
import { computed } from "vue";
import { modIcon } from "@/components/mods/mod-icons";
import { roleColor, str, type ModLeafNode } from "@/components/mods/mod-style";

/** One of Fleet's icons by name. With a `label` it's an image to a screen reader; without, decoration. */
const props = defineProps<{ node: ModLeafNode }>();

const name = computed(() => str(props.node.props.name));
const icon = computed(() => modIcon(name.value));
const label = computed(() => str(props.node.props.label));
</script>

<template>
  <span
    v-if="icon"
    class="mod-icon"
    :class="{ 'mod-icon--spin': name === 'loader' }"
    :data-icon="name"
    :style="{ color: roleColor(node.props.color) }"
    :role="label ? 'img' : undefined"
    :aria-label="label"
    :aria-hidden="label ? undefined : 'true'"
  >
    <component
      :is="icon"
      :size="14"
      aria-hidden="true"
    />
  </span>
</template>

<style scoped>
.mod-icon {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
}

.mod-icon--spin {
  animation: mod-spin 0.8s linear infinite;
}

@keyframes mod-spin {
  to { transform: rotate(360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .mod-icon--spin { animation-duration: 3s; }
}
</style>
