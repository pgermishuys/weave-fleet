<script setup lang="ts">
import { computed } from "vue";
import ModDraftMark from "@/components/mods/ModDraftMark.vue";
import ModTree from "@/components/mods/ModTree.vue";
import { composerBands } from "@/lib/mods/points";
import { useResolvedModView } from "@/lib/mods/resolve";
import type { ModAction, ModSurface } from "@/lib/mods/types";

/**
 * The band a mod draws above a session's composer, styled as Fleet's own strips there. No contribution, an invalid
 * tree or a `null` tree draws nothing at all, not even a wrapper.
 */
const props = defineProps<{ sessionId: string; surface: ModSurface }>();

const resolved = useResolvedModView(composerBands, () => props.sessionId, "ComposerBand");
const drawn = computed(() => {
  const view = resolved.value;
  return view.draws && view.tree ? view : null;
});

function act(action: ModAction): void {
  drawn.value?.view.onAction?.(action, props.surface);
}
</script>

<template>
  <div
    v-if="drawn"
    class="mod-composer-band"
    :class="`mod-composer-band--${surface}`"
    data-testid="mod-composer-band"
  >
    <ModTree
      class="mod-composer-band__tree"
      :tree="drawn.tree"
      site="ComposerBand"
      :session-id="sessionId"
      @action="act"
    />
    <ModDraftMark v-if="drawn.draft" />
  </div>
</template>

<style scoped>
.mod-composer-band {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 6px 8px;
  box-sizing: border-box;
  min-width: 0;
  width: 100%;
  padding: 6px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  color: var(--text);
  font-size: 12px;
  overflow: hidden;
  overflow-wrap: anywhere;
}

.mod-composer-band--desktop {
  max-width: 760px;
  margin: 0 auto 6px;
}

.mod-composer-band--phone {
  margin: 0 0 6px;
}

.mod-composer-band__tree {
  flex: 1 1 0;
  min-width: 0;
}
</style>
