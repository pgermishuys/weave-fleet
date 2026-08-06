<script setup lang="ts">
import { computed } from "vue";
import { storeToRefs } from "pinia";
import { useSessionsStore } from "@/stores/sessions";
import { useSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { useArtifactViewer } from "@/composables/use-artifact-viewer";
import { buildArtifactTree } from "@/lib/artifact-tree";
import ArtifactsTreeNode from "./ArtifactsTreeNode.vue";
import type { FileDiffItem } from "@/api/client";

const sessionsStore = useSessionsStore();
const { activeSessionId } = storeToRefs(sessionsStore);
const sessionDiffsContext = useSessionDiffsContext();
const { openFile } = useArtifactViewer();

const diffs = computed<readonly FileDiffItem[]>(() => {
  const context = sessionDiffsContext.value;
  return context?.diffState.diffs.value ?? [];
});

const tree = computed(() => buildArtifactTree([...diffs.value]));

const summary = computed(() => {
  let newCount = 0;
  let modifiedCount = 0;
  let sourceCount = 0;

  for (const diff of diffs.value) {
    if (diff.status === "added") {
      newCount++;
    } else if (diff.status === "modified") {
      modifiedCount++;
    } else if (diff.status === "deleted") {
      sourceCount++;
    }
  }

  const parts: string[] = [];
  if (newCount > 0) parts.push(`${newCount} new`);
  if (modifiedCount > 0) parts.push(`${modifiedCount} edit`);
  if (sourceCount > 0) parts.push(`${sourceCount} source`);

  return parts.join(" · ") || "No files";
});

function handleFileClick(path: string): void {
  openFile(path);
}
</script>

<template>
  <div class="artifacts-panel">
    <div class="artifacts-summary">
      {{ summary }}
    </div>

    <ArtifactsTreeNode
      v-for="node in tree"
      :key="node.fullPath"
      :node="node"
      :depth="0"
      @select-file="handleFileClick"
    />

    <div
      v-if="diffs.length === 0"
      class="artifacts-empty"
    >
      <p class="artifacts-empty__text">
        No artifacts yet
      </p>
      <p class="artifacts-empty__hint">
        Files created, modified, or read during this session will appear here.
      </p>
    </div>
  </div>
</template>

<style scoped>
.artifacts-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 0;
}

.artifacts-summary {
  padding: 8px 0;
  font-size: 11px;
  font-weight: 600;
  color: var(--muted);
  text-align: center;
  border-bottom: 1px solid var(--border);
}

.artifacts-empty {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 24px 16px;
  text-align: center;
}

.artifacts-empty__text {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.artifacts-empty__hint {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--muted);
}
</style>
