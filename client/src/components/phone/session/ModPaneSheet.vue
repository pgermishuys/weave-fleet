<script setup lang="ts">
import { computed } from "vue";
import { useModView } from "@/lib/mods/points";
import { X } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import ModDraftMark from "@/components/mods/ModDraftMark.vue";
import ModTree from "@/components/mods/ModTree.vue";
import { modPanes } from "@/lib/mods/points";
import { useResolvedModView } from "@/lib/mods/resolve";
import type { ModAction } from "@/lib/mods/types";

/** A pane a mod opened in this session, drawn full height from the session menu. It closes itself when the pane goes. */
const props = defineProps<{ open: boolean; sessionId: string; viewId: string | null }>();
const emit = defineEmits<{ (event: "close"): void }>();

// `viewId` is the pane's id in `modPanes` (session, mod and pane), as the session menu lists it.
const view = useModView(modPanes, () => props.viewId ?? undefined);
const resolved = useResolvedModView(modPanes, () => props.viewId ?? undefined, "Pane");
const shown = computed(() => props.open && view.value !== undefined);

function act(action: ModAction): void {
  view.value?.onAction?.(action, "phone");
}
</script>

<template>
  <BottomSheet
    :open="shown"
    :label="view?.title ?? 'Pane'"
    :detents="['large']"
    @close="emit('close')"
  >
    <template #head>
      <h2>{{ view?.title }}</h2>
      <ModDraftMark v-if="resolved.draws && resolved.draft" />
      <button
        type="button"
        class="ph-icon-btn"
        aria-label="Close"
        data-testid="sheet-close"
        @click="emit('close')"
      >
        <X aria-hidden="true" />
      </button>
    </template>
    <div
      v-if="resolved.draws && resolved.tree"
      class="mps__body"
      data-testid="mod-pane-body"
    >
      <ModTree
        :tree="resolved.tree"
        site="Pane"
        :session-id="sessionId"
        @action="act"
      />
    </div>
  </BottomSheet>
</template>

<style scoped>
/* No side padding: a mod's tree pads itself (a Box's `padding`), and the sheet's head is already inset. */
.mps__body {
  min-width: 0;
  padding: 0 0 16px;
  overflow-wrap: anywhere;
}
</style>
