<script setup lang="ts">
import { computed } from "vue";
import ModDraftMark from "@/components/mods/ModDraftMark.vue";
import ModTree from "@/components/mods/ModTree.vue";
import { statusChips, useModView } from "@/lib/mods/points";
import { useResolvedModView } from "@/lib/mods/resolve";
import type { ModAction } from "@/lib/mods/types";

/** A mod's chip in the status bar for one session. Draws nothing, not even a wrapper, unless a mod's valid tree is there. */
const props = defineProps<{ sessionId: string }>();

const view = useModView(statusChips, () => props.sessionId);
const resolved = useResolvedModView(statusChips, () => props.sessionId, "StatusChip");
const tree = computed(() => (resolved.value.draws ? resolved.value.tree : null));
const draft = computed(() => resolved.value.draws && resolved.value.draft);

function send(action: ModAction): void {
  view.value?.onAction?.(action, "desktop");
}
</script>

<template>
  <span
    v-if="tree"
    class="mod-status-chip"
    data-testid="mod-status-chip"
  >
    <ModTree
      :tree="tree"
      site="StatusChip"
      :session-id="sessionId"
      @action="send"
    />
    <ModDraftMark v-if="draft" />
  </span>
</template>

<style scoped>
.mod-status-chip {
  display: inline-flex;
  min-width: 0;
  align-items: center;
  gap: 6px;
  max-width: 260px;
  overflow: hidden;
  white-space: nowrap;
}
</style>
