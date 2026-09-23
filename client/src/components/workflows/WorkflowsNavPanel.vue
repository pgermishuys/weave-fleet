<script setup lang="ts">
import { computed, onMounted } from "vue";
import { AlertTriangle, GitPullRequest, Loader2, MessageSquareText } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import type { Workflow } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

const nav = useWorkflowsNav();
const store = useWorkflowsStore();

onMounted(() => {
  if (!nav.library.value) void nav.reload();
});

const repoLabel = computed(() => (nav.library.value?.repositoryName ? `${nav.library.value.repositoryName} · .weave/workflows` : "This repo · .weave/workflows"));

/** A workflow's latest run, for the word at the end of its row. */
function latestRun(workflow: Workflow) {
  return store.orderedRuns.find((run) => run.workflowId === workflow.id) ?? null;
}

function rowMeta(workflow: Workflow): { label: string; tone: string } {
  if (workflow.errors.length > 0) return { label: "Can't read", tone: "error" };
  const run = latestRun(workflow);
  if (run?.status === "waiting") return { label: "Needs you", tone: "wait" };
  if (run?.status === "running") return { label: "Running", tone: "working" };
  return { label: `${workflow.steps.length} steps`, tone: "" };
}
</script>

<template>
  <section
    class="workflows-nav-panel"
    aria-label="Workflows navigation"
  >
    <div class="panel-header-row">
      <p class="panel-header">
        Workflows
      </p>
    </div>

    <nav
      v-if="nav.library.value"
      class="workflows-nav"
      aria-label="Workflows list"
    >
      <div class="group-label">
        <span>Built into Fleet</span><span>{{ nav.builtIns.value.length }}</span>
      </div>
      <button
        v-for="workflow in nav.builtIns.value"
        :key="workflow.id"
        type="button"
        class="workflow-row"
        :class="{ 'workflow-row--active': nav.activeWorkflowId.value === workflow.id }"
        :aria-current="nav.activeWorkflowId.value === workflow.id ? 'page' : undefined"
        data-testid="workflow-row"
        @click="nav.setActiveWorkflow(workflow.id)"
      >
        <StatusGlyph
          v-if="rowMeta(workflow).tone === 'working'"
          status="active"
          label="Running"
        />
        <MessageSquareText
          v-else
          class="workflow-row__icon"
          aria-hidden="true"
        />
        <span class="workflow-row__title">{{ workflow.name }}</span>
        <span
          class="workflow-row__meta"
          :class="rowMeta(workflow).tone && `workflow-row__meta--${rowMeta(workflow).tone}`"
        >{{ rowMeta(workflow).label }}</span>
      </button>

      <div class="group-label">
        <span>{{ repoLabel }}</span><span>{{ nav.repoWorkflows.value.length }}</span>
      </div>
      <button
        v-for="workflow in nav.repoWorkflows.value"
        :key="workflow.id"
        type="button"
        class="workflow-row"
        :class="{ 'workflow-row--active': nav.activeWorkflowId.value === workflow.id }"
        :aria-current="nav.activeWorkflowId.value === workflow.id ? 'page' : undefined"
        data-testid="workflow-row"
        @click="nav.setActiveWorkflow(workflow.id)"
      >
        <AlertTriangle
          v-if="workflow.errors.length > 0"
          class="workflow-row__icon workflow-row__icon--error"
          aria-hidden="true"
        />
        <GitPullRequest
          v-else
          class="workflow-row__icon"
          aria-hidden="true"
        />
        <span class="workflow-row__title">{{ workflow.name }}</span>
        <span
          class="workflow-row__meta"
          :class="rowMeta(workflow).tone && `workflow-row__meta--${rowMeta(workflow).tone}`"
        >{{ rowMeta(workflow).label }}</span>
      </button>
      <p
        v-if="nav.repoWorkflows.value.length === 0"
        class="empty-message"
      >
        {{ nav.library.value.repository ? "No workflow files in this repo yet." : "Pick a repository in the Run box to see its workflows." }}
      </p>
    </nav>

    <div
      v-else-if="nav.isLoadingLibrary.value"
      class="workflows-nav-empty"
    >
      <Loader2
        :size="20"
        class="spinner"
        aria-hidden="true"
      />
      <span class="sr-only">Loading workflows...</span>
    </div>

    <p
      v-else-if="nav.libraryError.value"
      class="empty-message workflows-nav-error"
      role="alert"
    >
      {{ nav.libraryError.value }}
    </p>
  </section>
</template>

<style scoped>
.workflows-nav-panel {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  background: transparent;
}

.panel-header-row {
  padding-top: 4px;
}

.panel-header {
  margin: 0;
  padding: 14px 16px 10px;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.workflows-nav {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 1px;
  padding: 0 8px 12px;
  overflow-y: auto;
}

.group-label {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 12px 10px 6px;
  font-size: 11px;
  font-weight: 500;
  color: var(--muted);
}

/* The same row as a session's: glyph slot, title, and a word at the end. */
.workflow-row {
  display: flex;
  width: 100%;
  min-width: 0;
  min-height: 32px;
  align-items: center;
  gap: 9px;
  border: 0;
  border-radius: var(--radius-btn);
  padding: 0 10px;
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  text-align: left;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.workflow-row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.workflow-row--active {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.workflow-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.workflow-row__icon {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
  color: var(--muted);
}

.workflow-row__icon--error {
  color: var(--error);
}

.workflow-row__title {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  font-size: 13px;
  line-height: 1.3;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workflow-row__meta {
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-size: 12px;
  font-weight: 400;
  white-space: nowrap;
}

.workflow-row__meta--working {
  color: var(--running);
}

.workflow-row__meta--wait {
  color: var(--idle);
}

.workflow-row__meta--error {
  color: var(--error);
}

.workflows-nav-empty {
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: center;
  padding: 24px 12px;
}

.spinner {
  animation: spin 1s linear infinite;
  color: var(--muted);
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.empty-message {
  margin: 0;
  padding: 4px 10px;
  font-size: 12px;
  color: var(--muted);
}

.workflows-nav-error {
  padding: 16px;
  color: var(--error);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border-width: 0;
}
</style>
