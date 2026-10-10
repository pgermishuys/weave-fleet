<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronDown, ChevronRight } from "lucide-vue-next";
import PhoneGlyph from "@/components/phone/PhoneGlyph.vue";
import ModTree from "@/components/mods/ModTree.vue";
import { toolRowViewId, toolRowViews } from "@/lib/mods/points";
import { resolvedViewsById } from "@/lib/mods/resolve";
import { stepRow, visibleSteps, type FoldedStep, type PhoneBlock } from "@/lib/phone/fold-steps";
import { getToolIcon } from "@/lib/tool-icons";

/**
 * A turn's tools as the desktop draws them: one box, a row per call — its icon, Read / Edit / Bash, the file or
 * command, and how it ended (✓, +1 −1, failed, still running, or Needs you while it waits on an ask) — and a row per
 * subagent (its name, its task, the working dots while it works; a tap opens its session). Of each run of calls the
 * first three show; the rest are "N more steps". Tapping a call opens it.
 */
type Part = Extract<PhoneBlock, { kind: "steps" }> | Extract<PhoneBlock, { kind: "subagent" }>;
const props = withDefaults(
  defineProps<{ parts: readonly Part[]; waitingCalls?: ReadonlySet<string>; all?: boolean; sessionId?: string }>(),
  { waitingCalls: () => new Set(), all: false, sessionId: undefined },
);
const emit = defineEmits<{
  (event: "open", step: FoldedStep, run: readonly FoldedStep[]): void;
  (event: "more", steps: readonly FoldedStep[]): void;
  (event: "child", sessionId: string): void;
}>();

const sections = computed(() => props.parts.map((part) => {
  if (part.kind === "subagent") return { kind: "subagent" as const, key: part.key, part };
  const view = props.all ? { rows: [...part.steps], more: [] } : visibleSteps(part.steps);
  return {
    kind: "steps" as const,
    key: part.key,
    steps: part.steps,
    more: view.more,
    rows: view.rows.map((step) => ({
      step,
      row: stepRow(step),
      waiting: props.waitingCalls.has(step.part.callId),
    })),
  };
}));

// A mod's tree for a call's line. Not part of `sections`: that would rebuild every row whenever any view changes.
// Each call's view is read on its own, so a contribution for one call redraws only the run that holds it.
const lineViews = resolvedViewsById(toolRowViews, "ToolUse");
function modOf(step: FoldedStep) {
  return lineViews(props.sessionId ? toolRowViewId("ToolUse", props.sessionId, step.part.callId) : undefined);
}
/** The tree to draw under a row, or null where no mod draws one. */
function lineOf(step: FoldedStep) {
  const resolved = modOf(step);
  return resolved.draws && resolved.tree ? resolved : null;
}
</script>

<template>
  <div
    class="ph-tools"
    data-testid="phone-steps-row"
  >
    <template
      v-for="section in sections"
      :key="section.key"
    >
      <button
        v-if="section.kind === 'subagent'"
        type="button"
        class="ph-tool ph-tool--who"
        :disabled="!section.part.childSessionId"
        data-testid="phone-subagent"
        @click="section.part.childSessionId && emit('child', section.part.childSessionId)"
      >
        <component
          :is="getToolIcon('task')"
          class="ph-tool__ic"
          aria-hidden="true"
        />
        <span class="ph-tool__l">{{ section.part.agent }}</span>
        <span class="ph-tool__d">{{ section.part.title }}</span>
        <span class="ph-tool__r">
          <PhoneGlyph
            v-if="section.part.running"
            kind="working"
            label="Working"
          />
          <ChevronRight
            v-else-if="section.part.childSessionId"
            class="ph-tool__ic"
            aria-label="Open"
          />
          <Check
            v-else
            class="ph-tool__ok"
            aria-label="Done"
          />
        </span>
      </button>
      <template v-else>
        <template
          v-for="{ step, row, waiting } in section.rows"
          :key="step.id"
        >
          <button
            type="button"
            class="ph-tool"
            data-testid="phone-step"
            @click="emit('open', step, section.steps)"
          >
            <component
              :is="getToolIcon(step.tool)"
              class="ph-tool__ic"
              aria-hidden="true"
            />
            <span class="ph-tool__l">{{ row.label }}</span>
            <span class="ph-tool__d"><span
              v-if="row.pattern"
              class="ph-tool__pat"
            >{{ row.detail }}</span><template v-else>{{ row.detail }}</template></span>
            <span class="ph-tool__r">
              <span
                v-if="waiting"
                class="ph-tool__needs"
              >Needs you</span>
              <PhoneGlyph
                v-else-if="row.result === 'running'"
                kind="running"
                label="Running"
              />
              <span
                v-else-if="row.result === 'failed'"
                class="ph-tool__bad"
              >failed</span>
              <template v-else-if="row.result === 'diff'">
                <span class="ph-add">+{{ row.adds }}</span><span class="ph-del">−{{ row.dels }}</span>
              </template>
              <Check
                v-else-if="!modOf(step).draws"
                class="ph-tool__ok"
                aria-label="Done"
              />
            </span>
          </button>
          <span
            v-if="lineOf(step)"
            class="ph-tool__mod"
          >
            <ModTree
              :tree="lineOf(step)?.tree ?? null"
              site="ToolUse"
              :session-id="sessionId"
              @action="lineOf(step)?.view.onAction?.($event, 'phone')"
            />
          </span>
        </template>
        <button
          v-if="section.more.length"
          type="button"
          class="ph-tool ph-tool--more"
          data-testid="phone-steps-more"
          @click="emit('more', section.more)"
        >
          <ChevronDown
            class="ph-tool__ic"
            aria-hidden="true"
          />
          <span class="ph-tool__l">{{ section.more.length }} more steps</span>
        </button>
      </template>
    </template>
  </div>
</template>

<!-- Not scoped: the DOM of a row no mod draws on stays exactly what it was. -->
<style>
/* A mod's tree is a second line under its row, a sibling of the row's button (a button can't hold buttons), indented to the label column. */
.ph-tool__mod {
  box-sizing: border-box;
  min-width: 0;
  max-width: 100%;
  padding: 0 10px 6px 36px;
  overflow-wrap: anywhere;
}

.ph-tool__mod .mod-tree {
  flex-wrap: wrap;
  min-width: 0;
  max-width: 100%;
  white-space: normal;
}
</style>
