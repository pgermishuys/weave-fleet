<script setup lang="ts">
import type { WorkspaceRootItem, WorkspaceRootsResponse } from "@/lib/api-types";
import { onMounted, reactive, ref, shallowRef, watch } from "vue";
import { AlertCircle, LoaderCircle } from "lucide-vue-next";
import { apiFetch } from "@/lib/api-client";
import {
  readWorkspacePreferences,
  writeWorkspacePreferences,
} from "@/lib/workspace-preferences";
import type { WorkspacePreferences } from "@/lib/workspace-preferences";

const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";
const selectClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors focus:border-accent disabled:cursor-not-allowed disabled:opacity-60";

const workspaceRoots = ref<WorkspaceRootItem[]>([]);
const workspaceRootsLoading = shallowRef(true);
const workspaceRootsError = shallowRef<string | null>(null);

const workspacePreferences = reactive<WorkspacePreferences>(
  readWorkspacePreferences(typeof window !== "undefined" ? window.localStorage : null),
);

onMounted(() => {
  void loadWorkspaceRoots();
});

watch(
  workspacePreferences,
  (next) => {
    writeWorkspacePreferences(next, typeof window !== "undefined" ? window.localStorage : null);
  },
  { deep: true },
);

async function loadWorkspaceRoots(): Promise<void> {
  workspaceRootsLoading.value = true;
  workspaceRootsError.value = null;

  try {
    const response = await apiFetch("/api/workspace-roots");
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const payload = await response.json() as WorkspaceRootsResponse;
    workspaceRoots.value = payload.roots;

    const preferredRootStillExists = payload.roots.some((root) => root.path === workspacePreferences.preferredRootPath);
    if (!preferredRootStillExists) {
      workspacePreferences.preferredRootPath = payload.roots[0]?.path ?? "";
    }
  } catch (error) {
    workspaceRootsError.value = error instanceof Error
      ? error.message
      : "Failed to load workspace roots.";
  } finally {
    workspaceRootsLoading.value = false;
  }
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        General settings
      </h2>
      <p class="text-sm text-muted">
        Set workspace defaults used across repository browsing and session creation.
      </p>
    </div>

    <div class="mt-5 grid gap-4 lg:grid-cols-2">
      <label class="grid gap-1 text-sm text-text">
        <span class="text-xs font-medium uppercase tracking-wide text-muted">Workspace label</span>
        <input
          v-model="workspacePreferences.displayName"
          type="text"
          :class="inputClass"
          placeholder="Workspace"
        >
      </label>

      <label class="grid gap-1 text-sm text-text">
        <span class="text-xs font-medium uppercase tracking-wide text-muted">Preferred root</span>
        <select
          v-model="workspacePreferences.preferredRootPath"
          :class="selectClass"
          :disabled="workspaceRootsLoading"
        >
          <option value="">Use first available root</option>
          <option
            v-for="root in workspaceRoots"
            :key="root.id ?? root.path"
            :value="root.path"
          >
            {{ root.path }}
          </option>
        </select>
      </label>
    </div>

    <div
      v-if="workspaceRootsLoading"
      class="mt-4 flex items-center gap-2 text-sm text-muted"
    >
      <LoaderCircle
        :size="16"
        class="animate-spin"
        aria-hidden="true"
      />
      <span>Loading workspace roots…</span>
    </div>

    <div
      v-else-if="workspaceRootsError"
      class="mt-4 flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ workspaceRootsError }}</span>
    </div>

    <label class="mt-4 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
      <div>
        <p class="text-sm font-medium text-text">Refresh repository index after root changes</p>
        <p class="mt-1 text-xs text-muted">
          When enabled, adding or removing roots refreshes the repository scanner automatically.
        </p>
      </div>
      <input
        v-model="workspacePreferences.autoRefreshRepositories"
        type="checkbox"
        class="mt-1 h-4 w-4 rounded border-border accent-[var(--accent)]"
      >
    </label>
  </section>
</template>
