<script setup lang="ts">
import { computed, provide, useSlots, watch } from "vue";
import ModNode from "@/components/mods/ModNode.vue";
import { MOD_TREE } from "@/components/mods/mod-context";
import { MOD_INLINE_SITES, type ModAction, type ModRenderSite } from "@/lib/mods/types";
import { validateModTree } from "@/lib/mods/validate";

/**
 * A tree a mod drew, drawn with Fleet's own components in Fleet's theme. The tree is checked first: an invalid one draws
 * as if the mod weren't there (the `fleet` slot alone) and says why, once. A `Fleet` node in a valid tree draws the slot
 * in place. At the inline sites (a tool row's line, the status bar) the root is one compact inline row.
 */
const props = defineProps<{
  /** A `ModWireElement` or `null`, as it arrived. */
  tree: unknown;
  site: ModRenderSite;
  /** The session the tree is drawn for: a `Page` element is served under it. */
  sessionId?: string;
}>();

const emit = defineEmits<{
  action: [action: ModAction];
  /** The tree broke the contract; the reason. */
  invalid: [reason: string];
}>();

const slots = useSlots();
const check = computed(() => validateModTree(props.tree, props.site));
const inline = computed(() => MOD_INLINE_SITES.includes(props.site));

// Once per tree, not per draw.
watch(
  [() => props.tree, () => props.site],
  () => {
    if (!check.value.ok) emit("invalid", check.value.reason);
  },
  { immediate: true },
);

provide(MOD_TREE, {
  get site() { return props.site; },
  get sessionId() { return props.sessionId; },
  send: (action) => emit("action", action),
  drawFleet: () => slots.fleet?.(),
});
</script>

<template>
  <div
    class="mod-tree"
    :class="{ 'mod-tree--inline': inline }"
    data-testid="mod-tree"
  >
    <slot
      v-if="!check.ok"
      name="fleet"
    />
    <ModNode
      v-else-if="check.tree"
      :node="check.tree"
    />
  </div>
</template>

<style scoped>
.mod-tree {
  min-width: 0;
  max-width: 100%;
  overflow-wrap: anywhere;
}

/* One compact row: it sits on a 30 px tool row and in the status bar. */
.mod-tree--inline {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  vertical-align: middle;
}
</style>
