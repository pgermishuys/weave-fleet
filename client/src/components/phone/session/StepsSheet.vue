<script setup lang="ts">
import { computed, shallowRef, watch, type Component } from "vue";
import { ChevronLeft, X } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import PhoneToolRun from "@/components/phone/session/PhoneToolRun.vue";
import ModTree from "@/components/mods/ModTree.vue";
import { stepDetail, stepRow, type FoldedStep } from "@/lib/phone/fold-steps";
import { toolRowViewId, toolRowViews } from "@/lib/mods/points";
import { useResolvedModView } from "@/lib/mods/resolve";
import type { ModAction } from "@/lib/mods/types";

/**
 * Steps, read-only: a list of them ("3 more steps"), or one step opened — an edit as its diff, a command with what it
 * printed. A step opened from the list has Back to it.
 */
const props = defineProps<{ open: boolean; steps: readonly FoldedStep[]; focus?: FoldedStep | null; sessionId?: string }>();
const emit = defineEmits<{ (event: "close"): void }>();

const picked = shallowRef<FoldedStep | null>(null);
watch(() => props.open, (open) => {
  picked.value = open ? props.focus ?? null : null;
}, { immediate: true });

const fromList = computed(() => !props.focus);
const row = computed(() => (picked.value ? stepRow(picked.value) : null));
const detail = computed(() => (picked.value ? stepDetail(picked.value) : null));

// A mod's tree for the opened step's result replaces Fleet's detail; the `fleet` slot is Fleet's own, drawn straight
// into the body when no mod draws.
const resultView = useResolvedModView(
  toolRowViews,
  () => (props.sessionId && picked.value ? toolRowViewId("ToolResult", props.sessionId, picked.value.part.callId) : undefined),
  "ToolResult",
);
const FleetDetail: Component = (_props, { slots }) => slots.fleet?.();
const resultBind = computed(() => {
  const view = resultView.value;
  if (!view.draws) return {};
  return {
    tree: view.tree,
    site: "ToolResult",
    sessionId: props.sessionId,
    onAction: (action: ModAction) => view.view.onAction?.(action, "phone"),
  };
});
const subtitle = computed(() => {
  if (!row.value) return "";
  return row.value.pattern || picked.value?.category === "run" ? row.value.detail : row.value.detail.split("/").pop() ?? "";
});
</script>

<template>
  <BottomSheet
    :open="open"
    :label="row ? row.label : 'Steps'"
    :detents="['medium', 'large']"
    initial="medium"
    :title="row ? undefined : `${steps.length} more step${steps.length === 1 ? '' : 's'}`"
    @close="emit('close')"
  >
    <template
      v-if="row"
      #head
    >
      <button
        v-if="fromList"
        type="button"
        class="ph-icon-btn ph-icon-btn--text ss__back"
        aria-label="Back to the steps"
        @click="picked = null"
      >
        <ChevronLeft aria-hidden="true" />
      </button>
      <h2>{{ row.label }}<span class="ph-sheet__sub">{{ subtitle }}</span></h2>
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

    <div class="ph-sheet__pad">
      <PhoneToolRun
        v-if="!picked"
        :parts="[{ kind: 'steps', key: 'sheet', steps: [...steps], summary: '', running: false, failed: 0 }]"
        all
        :session-id="sessionId"
        @open="(step) => (picked = step)"
      />
      <component
        :is="resultView.draws ? ModTree : FleetDetail"
        v-else
        v-bind="resultBind"
      >
        <template #fleet>
          <div
            v-if="detail?.kind === 'diff'"
            class="ph-cmd ss__diff"
            data-testid="phone-step-detail"
          >
            <div class="ss__file">
              {{ detail.file }} <span class="ph-add">+{{ detail.adds }}</span> <span class="ph-del">−{{ detail.dels }}</span>
            </div>
            <div class="ss__lines">
              <span
                v-for="(line, index) in detail.lines"
                :key="index"
                class="ss__line"
                :class="`ss__line--${line.kind}`"
              >{{ line.text || " " }}</span>
            </div>
          </div>
          <pre
            v-else-if="detail"
            class="ph-cmd ss__out"
            data-testid="phone-step-detail"
          ><template v-if="detail.command"><span class="ph-cmd__p">$ </span>{{ detail.command }}
</template><span class="ss__result">{{ detail.text }}</span></pre>
        </template>
      </component>
    </div>
  </BottomSheet>
</template>

<style scoped>
.ss__back {
  margin-left: -10px;
}

.ss__diff {
  margin-top: 0;
  padding: 0;
  overflow: hidden;
  font-size: 12.5px;
}

.ss__file {
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  font-size: 12px;
  color: var(--muted);
}

.ss__lines {
  padding: 6px 0;
  overflow-x: auto;
  white-space: pre;
}

.ss__line {
  display: block;
  min-width: max-content;
  padding: 0 12px;
}

.ss__line--hunk {
  color: var(--muted);
}

.ss__line--del {
  background: color-mix(in srgb, var(--error) 10%, transparent);
}

.ss__line--add {
  background: color-mix(in srgb, var(--running) 10%, transparent);
}

.ss__out {
  max-height: none;
  margin-top: 0;
  font-size: 12.5px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}

.ss__result {
  color: var(--muted);
}
</style>
