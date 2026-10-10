<script setup lang="ts">
import { computed } from "vue";
import { modIcon } from "@/components/mods/mod-icons";
import { str, toneColor, type ModLeafNode } from "@/components/mods/mod-style";

/** Fleet's status pill: the tone's colour as a wash, with text and border in the tone. */
const props = defineProps<{ node: ModLeafNode }>();

const tone = computed(() => str(props.node.props.tone) ?? "neutral");
const icon = computed(() => modIcon(props.node.props.icon));
</script>

<template>
  <span
    class="mod-pill"
    :data-tone="tone"
    :style="{ '--mod-tone': toneColor(tone) }"
  >
    <component
      :is="icon"
      v-if="icon"
      :size="13"
      aria-hidden="true"
    />
    <span class="mod-pill__label">{{ str(node.props.label) }}</span>
  </span>
</template>

<style scoped>
.mod-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  max-width: 100%;
  height: 22px;
  padding: 0 9px;
  border: 1px solid color-mix(in srgb, var(--mod-tone, var(--muted)) 28%, transparent);
  border-radius: 999px;
  background: color-mix(in srgb, var(--mod-tone, var(--muted)) 14%, transparent);
  color: var(--mod-tone, var(--muted));
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
}

.mod-pill svg {
  flex: none;
}

/* A long label gives way with an ellipsis, so a pill never makes its row wider than the screen. */
.mod-pill__label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
