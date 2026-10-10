<script setup lang="ts">
import { computed } from "vue";
import ModTree from "@/components/mods/ModTree.vue";
import { modPanes, modPaneViewId, useModView } from "@/lib/mods/points";
import { useResolvedModView } from "@/lib/mods/resolve";
import type { ModAction } from "@/lib/mods/types";

/**
 * A pane a mod opened, drawn from the view the mod last contributed. Without one (restored after a reload, before the
 * mod redraws), or with a tree that is empty or invalid, the pane shows a quiet empty state.
 */
const props = defineProps<{
  sessionId: string;
  paneId: string;
  mod: string;
}>();

// A pane id is the session's, the mod's and its own.
const viewId = () => modPaneViewId(props.sessionId, props.mod, props.paneId);
const view = useModView(modPanes, viewId);
const resolved = useResolvedModView(modPanes, viewId, "Pane");
const tree = computed(() => (resolved.value.draws ? resolved.value.tree : null));
const modName = computed(() => view.value?.mod ?? props.mod);

function send(action: ModAction): void {
  view.value?.onAction?.(action, "desktop");
}
</script>

<template>
  <div
    class="mod-pane"
    data-testid="mod-pane"
  >
    <ModTree
      v-if="tree"
      :tree="tree"
      site="Pane"
      :session-id="sessionId"
      @action="send"
    />
    <p
      v-else
      class="mod-pane__empty"
      data-testid="mod-pane-empty"
    >
      {{ view ? "Nothing to show here yet" : `Waiting for ${modName} to draw this pane` }}
    </p>
  </div>
</template>

<style scoped>
.mod-pane {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 12px;
}

.mod-pane__empty {
  margin: 0;
  padding: 24px 8px;
  text-align: center;
  font-size: 12px;
  color: var(--muted);
}
</style>
