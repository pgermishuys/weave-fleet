<script setup lang="ts">
import { computed, type StyleValue } from "vue";
import ModNode from "@/components/mods/ModNode.vue";
import { roleColor, sideValue, spacePx, str, type ModContainerNode } from "@/components/mods/mod-style";

/**
 * A flex box. Spacing is in Fleet's 4 px steps and `width` a share of the parent, so a tree fits the phone; the box
 * can always shrink (`min-width: 0`) and never grows past its parent.
 */
const props = defineProps<{ node: ModContainerNode }>();

const p = computed(() => props.node.props);
const border = computed(() => {
  const style = str(p.value.borderStyle);
  return style === "round" || style === "single" || style === "dashed" || style === "quote" ? style : undefined;
});
const background = computed(() => {
  const value = str(p.value.background);
  return value === "subtle" || value === "tint" ? value : undefined;
});

const style = computed<StyleValue>(() => {
  const v = p.value;
  const width = typeof v.width === "string" && /^\d+(\.\d+)?%$/.test(v.width) ? v.width : undefined;
  const style: Record<string, string | number | undefined> = {
    flexDirection: v.flexDirection === "column" ? "column" : "row",
    gap: spacePx(v.gap),
    padding: spacePx(v.padding),
    paddingLeft: spacePx(v.paddingX),
    paddingRight: spacePx(v.paddingX),
    paddingTop: spacePx(v.paddingY),
    paddingBottom: spacePx(v.paddingY),
    alignItems: sideValue(v.alignItems),
    justifyContent: sideValue(v.justifyContent),
    flexWrap: v.flexWrap === "wrap" ? "wrap" : undefined,
    flexGrow: v.flexGrow === 1 ? 1 : undefined,
    // Not `flexBasis`: in a column parent that is the height.
    width,
    maxWidth: width ? "100%" : undefined,
    "--mod-edge": roleColor(v.borderColor),
  };
  // Vue clears a property set to undefined, so `paddingLeft: undefined` would undo `padding`: leave unset ones out.
  return Object.fromEntries(Object.entries(style).filter(([, value]) => value !== undefined));
});
</script>

<template>
  <div
    class="mod-box"
    :style="style"
    :data-border="border"
    :data-background="background"
  >
    <ModNode
      v-for="(child, index) in node.children"
      :key="index"
      :node="child"
    />
  </div>
</template>

<style scoped>
.mod-box {
  display: flex;
  box-sizing: border-box;
  min-width: 0;
  max-width: 100%;
}

.mod-box[data-border="round"] {
  border: 1px solid var(--mod-edge, var(--border));
  border-radius: var(--radius-card);
}

.mod-box[data-border="single"] {
  border: 1px solid var(--mod-edge, var(--border));
  border-radius: 0;
}

.mod-box[data-border="dashed"] {
  border: 1px dashed var(--mod-edge, var(--border));
  border-radius: var(--radius-btn);
}

.mod-box[data-border="quote"] {
  border-left: 3px solid var(--mod-edge, var(--border));
}

/* Fleet's raised surface, as the tool list's. */
.mod-box[data-background="subtle"] {
  background: color-mix(in srgb, var(--text) 4%, transparent);
}

.mod-box[data-background="tint"] {
  background: color-mix(in srgb, var(--mod-edge, var(--muted)) 12%, transparent);
}
</style>
