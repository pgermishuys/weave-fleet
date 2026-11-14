<script setup lang="ts">
import { computed, onMounted } from "vue";
import ModPaneOff from "@/components/mods/ModPaneOff.vue";
import ModTree from "@/components/mods/ModTree.vue";
import { modPanes, modPaneViewId, useModView } from "@/lib/mods/points";
import { isModOff } from "@/lib/mods/mod-off";
import { useResolvedModView } from "@/lib/mods/resolve";
import type { ModAction } from "@/lib/mods/types";
import { useModsStore } from "@/stores/mods";
import { EmptyState } from "@/components/ui/empty-state";
import { Puzzle } from "lucide-vue-next";

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

// A pane the mod opened without declaring it is judged by the kept mods; have them.
const mods = useModsStore();
onMounted(() => {
  if (!mods.kept) void mods.loadKept().catch(() => undefined);
});
const off = computed(() => isModOff(props.sessionId, props.mod));

function send(action: ModAction): void {
  view.value?.onAction?.(action, "desktop");
}
</script>

<template>
  <div
    class="mod-pane"
    data-testid="mod-pane"
  >
    <ModPaneOff
      v-if="off"
      :session-id="sessionId"
      :mod="mod"
    />
    <ModTree
      v-else-if="tree"
      :tree="tree"
      site="Pane"
      :session-id="sessionId"
      @action="send"
    />
    <EmptyState
      v-else
      :icon="Puzzle"
      :title="view ? 'Nothing to show here yet' : `Waiting for ${modName} to draw this pane`"
      compact
      data-testid="mod-pane-empty"
    />
  </div>
</template>

<style scoped>
.mod-pane {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 12px;
}

</style>
