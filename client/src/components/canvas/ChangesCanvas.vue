<script setup lang="ts">
import { computed, inject, shallowRef } from "vue";
import { GitBranch, History } from "lucide-vue-next";
import ChangesReview from "@/components/canvas/ChangesReview.vue";
import type { UseDiffsResult } from "@/composables/use-diffs";
import { diffBaseLabel } from "@/lib/diff-base";

defineProps<{
  sessionId: string;
}>();

const sharedDiffs = inject<UseDiffsResult>("sharedDiffs");

const base = computed(() => sharedDiffs?.base.value ?? null);
const refusal = computed(() => sharedDiffs?.refusal?.value ?? null);
const copied = shallowRef(false);

function copyFixCommand(): void {
  const command = refusal.value?.fixCommand;
  if (command) void navigator.clipboard?.writeText(command).then(() => { copied.value = true; }).catch(() => {});
}

const baseLabel = computed(() => diffBaseLabel(base.value));

const hasChanges = computed(() => (sharedDiffs?.diffs.value.length ?? 0) > 0);
</script>

<template>
  <div class="changes-canvas">
    <!-- With changes showing, the review's scope select names what they're compared with (its tooltip says it). -->
    <p
      v-if="baseLabel && !hasChanges"
      class="changes-canvas__base"
      :title="baseLabel.detail"
      data-testid="changes-base"
    >
      <component
        :is="base?.kind === 'branch' ? GitBranch : History"
        :size="12"
        class="changes-canvas__base-icon"
        aria-hidden="true"
      />
      <span>{{ baseLabel.text }}</span>
      <span
        v-if="baseLabel.commit"
        class="changes-canvas__base-commit"
      >{{ baseLabel.commit }}</span>
    </p>
    <p
      v-if="!hasChanges && sharedDiffs && !sharedDiffs.hasLoaded.value"
      class="changes-canvas__empty"
      data-testid="changes-loading"
    >
      Loading changes…
    </p>
    <div
      v-else-if="!hasChanges && refusal"
      class="changes-canvas__refused"
      data-testid="changes-git-refused"
    >
      <p class="changes-canvas__refused-title">
        {{ refusal.message }}
      </p>
      <p class="changes-canvas__refused-hint">
        To let git read it, run this once:
      </p>
      <code
        class="changes-canvas__refused-command"
        data-testid="git-fix-command"
      >{{ refusal.fixCommand }}</code>
      <button
        type="button"
        class="changes-canvas__refused-copy"
        @click="copyFixCommand"
      >
        {{ copied ? "Copied" : "Copy command" }}
      </button>
    </div>
    <p
      v-else-if="!hasChanges"
      class="changes-canvas__empty"
    >
      {{ base?.kind === "branch" ? "No changes on this branch yet." : "No changes in this session yet." }}
    </p>
    <ChangesReview
      v-else
      :session-id="sessionId"
    />
  </div>
</template>

<style scoped>
.changes-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* Lines up with the rows' icons below it. */
.changes-canvas__base {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
  margin: 0;
  padding: 10px 16px 0;
  font-size: 12px;
  color: var(--muted);
}

.changes-canvas__base-icon {
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 75%, transparent);
}

.changes-canvas__base-commit {
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
}

.changes-canvas__empty {
  margin: 0;
  padding: 14px 18px;
  font-size: 13px;
  color: var(--muted);
}

.changes-canvas__refused {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
  padding: 14px 18px;
  font-size: 13px;
}

.changes-canvas__refused-title,
.changes-canvas__refused-hint {
  margin: 0;
}

.changes-canvas__refused-hint {
  color: var(--muted);
}

.changes-canvas__refused-command {
  max-width: 100%;
  padding: 6px 8px;
  border: 1px solid var(--border);
  background: color-mix(in srgb, var(--muted) 10%, transparent);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  overflow-wrap: anywhere;
  user-select: all;
}

.changes-canvas__refused-copy {
  min-height: 28px;
  padding: 4px 12px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  cursor: pointer;
  font: inherit;
}

</style>
