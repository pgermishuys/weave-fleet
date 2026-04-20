<script setup lang="ts">
import type { WorkspaceRootItem, WorkspaceRootsResponse, AddWorkspaceRootResponse } from "@/lib/api-types";
import type { RegisteredSettingsSection } from "@/plugins/slots";
import { computed, onMounted, reactive, ref, shallowRef, watch } from "vue";
import { AlertCircle, FolderGit2, LoaderCircle, Plus, RefreshCw, Settings2, Trash2 } from "lucide-vue-next";
import CredentialsSection from "@/components/settings/CredentialsSection.vue";
import AppearanceSection from "@/components/settings/AppearanceSection.vue";
import SkillsSection from "@/components/settings/SkillsSection.vue";
import { useConfig } from "@/composables/use-config";
import { apiFetch } from "@/lib/api-client";
import {
  readWorkspacePreferences,
  writeWorkspacePreferences,
} from "@/lib/workspace-preferences";
import type { WorkspacePreferences } from "@/lib/workspace-preferences";
import { usePluginRuntime } from "@/plugins/composable";
import { getSettingsSections } from "@/plugins/slots";

interface DecoratedSettingsSection extends RegisteredSettingsSection {
  displayName: string;
}

const buttonPrimaryClass = "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-2 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-2 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";
const selectClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors focus:border-accent";

const pluginRuntime = usePluginRuntime();
const {
  config,
  error: configError,
  installedSkills,
  isLoading: isConfigLoading,
  paths,
  providers,
} = useConfig();
const workspaceRoots = ref<WorkspaceRootItem[]>([]);
const workspaceRootsLoading = shallowRef(true);
const workspaceRootsError = shallowRef<string | null>(null);
const newWorkspaceRoot = shallowRef("");
const isAddingWorkspaceRoot = shallowRef(false);
const isRefreshingWorkspaceRoots = shallowRef(false);
const deletingRootId = shallowRef<string | null>(null);
const addWorkspaceRootError = shallowRef<string | null>(null);

const workspacePreferences = reactive<WorkspacePreferences>(
  readWorkspacePreferences(typeof window !== "undefined" ? window.localStorage : null),
);

const pluginSections = computed<readonly DecoratedSettingsSection[]>(() => {
  const descriptorsById = new Map(
    pluginRuntime.descriptors.value.map((descriptor) => [descriptor.id, descriptor.displayName]),
  );

  return getSettingsSections(pluginRuntime.manifests.value).map((section) => ({
    ...section,
    displayName: descriptorsById.get(section.pluginId) ?? section.title,
  }));
});

const configuredAgentCount = computed(() => Object.keys(config.value?.agents ?? {}).length);
const connectedProviderCount = computed(() => providers.value.filter((provider) => provider.connected).length);
const totalModelCount = computed(() => providers.value.reduce((count, provider) => count + provider.models.length, 0));

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
      : "Failed to load workspace settings.";
  } finally {
    workspaceRootsLoading.value = false;
    isRefreshingWorkspaceRoots.value = false;
  }
}

async function refreshWorkspaceRoots(): Promise<void> {
  isRefreshingWorkspaceRoots.value = true;
  await loadWorkspaceRoots();
}

async function addWorkspaceRoot(): Promise<void> {
  const path = newWorkspaceRoot.value.trim();
  if (!path) {
    addWorkspaceRootError.value = "Workspace root path is required.";
    return;
  }

  isAddingWorkspaceRoot.value = true;
  addWorkspaceRootError.value = null;

  try {
    const response = await apiFetch("/api/workspace-roots", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path }),
    });

    const payload = await response.json().catch(() => ({})) as Partial<AddWorkspaceRootResponse> & { error?: string };
    if (!response.ok) {
      throw new Error(payload.error ?? "Failed to add workspace root.");
    }

    newWorkspaceRoot.value = "";
    await loadWorkspaceRoots();

    if (workspacePreferences.autoRefreshRepositories) {
      await apiFetch("/api/repositories/refresh", { method: "POST" });
    }
  } catch (error) {
    addWorkspaceRootError.value = error instanceof Error
      ? error.message
      : "Failed to add workspace root.";
  } finally {
    isAddingWorkspaceRoot.value = false;
  }
}

async function removeWorkspaceRoot(root: WorkspaceRootItem): Promise<void> {
  if (root.id === null) {
    return;
  }

  deletingRootId.value = root.id;

  try {
    const response = await apiFetch(`/api/workspace-roots/${encodeURIComponent(root.id)}`, {
      method: "DELETE",
    });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    if (workspacePreferences.preferredRootPath === root.path) {
      workspacePreferences.preferredRootPath = "";
    }

    await loadWorkspaceRoots();

    if (workspacePreferences.autoRefreshRepositories) {
      await apiFetch("/api/repositories/refresh", { method: "POST" });
    }
  } catch (error) {
    workspaceRootsError.value = error instanceof Error
      ? error.message
      : "Failed to remove workspace root.";
  } finally {
    deletingRootId.value = null;
  }
}
</script>

<template>
  <section class="grid gap-6">
    <div class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
      <div class="flex items-start gap-3">
        <div class="rounded-btn border border-border bg-main-bg p-2 text-text">
          <Settings2
            :size="18"
            aria-hidden="true"
          />
        </div>
        <div class="space-y-2">
          <h1 class="text-2xl font-semibold tracking-tight text-text">
            Settings
          </h1>
          <p class="max-w-3xl text-sm text-muted">
            Manage credentials, workspace preferences, appearance, skills, and any extra settings contributed by registered plugins.
          </p>
        </div>
      </div>
    </div>

    <div class="grid gap-6 2xl:grid-cols-[minmax(0,1.3fr)_minmax(0,0.9fr)]">
      <div class="grid gap-6">
        <CredentialsSection />

        <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
          <div class="flex flex-col gap-1">
            <h2 class="text-lg font-semibold text-text">
              Workspace settings
            </h2>
            <p class="text-sm text-muted">
              Control local workspace roots and the defaults used when browsing repositories.
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

          <div class="mt-6 flex items-center justify-between gap-3">
            <h3 class="text-sm font-semibold text-text">
              Configured roots
            </h3>
            <button
              type="button"
              :class="buttonSecondaryClass"
              :disabled="workspaceRootsLoading || isRefreshingWorkspaceRoots"
              @click="refreshWorkspaceRoots"
            >
              <RefreshCw
                :size="16"
                :class="isRefreshingWorkspaceRoots ? 'animate-spin' : ''"
                aria-hidden="true"
              />
              Refresh
            </button>
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

          <div
            v-else-if="workspaceRoots.length === 0"
            class="mt-4 rounded-card border border-dashed border-border p-6 text-center"
          >
            <FolderGit2
              :size="28"
              class="mx-auto text-muted"
              aria-hidden="true"
            />
            <p class="mt-3 text-sm font-medium text-text">
              No workspace roots configured
            </p>
            <p class="mt-1 text-xs text-muted">
              Add a local directory to enable repository browsing and workspace-backed sessions.
            </p>
          </div>

          <div
            v-else
            class="mt-4 grid gap-3"
          >
            <article
              v-for="root in workspaceRoots"
              :key="root.id ?? root.path"
              class="flex flex-col gap-3 rounded-card border border-border bg-main-bg p-4 md:flex-row md:items-center md:justify-between"
            >
              <div class="min-w-0">
                <p class="truncate text-sm font-medium text-text">
                  {{ root.path }}
                </p>
                <div class="mt-1 flex flex-wrap items-center gap-2 text-xs text-muted">
                  <span class="rounded-full border border-border px-2 py-1">
                    {{ root.source === "env" ? "Environment" : "Custom" }}
                  </span>
                  <span
                    v-if="!root.exists"
                    class="rounded-full border border-red-500/30 bg-red-500/10 px-2 py-1 text-red-200"
                  >
                    Missing on disk
                  </span>
                </div>
              </div>

              <button
                v-if="root.source === 'user' && root.id !== null"
                type="button"
                class="inline-flex items-center justify-center gap-2 rounded-btn border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm font-medium text-red-300 transition-colors hover:bg-red-500/20 disabled:cursor-not-allowed disabled:opacity-60"
                :disabled="deletingRootId === root.id"
                @click="removeWorkspaceRoot(root)"
              >
                <LoaderCircle
                  v-if="deletingRootId === root.id"
                  :size="16"
                  class="animate-spin"
                  aria-hidden="true"
                />
                <Trash2
                  v-else
                  :size="16"
                  aria-hidden="true"
                />
                <span>{{ deletingRootId === root.id ? "Removing…" : "Remove" }}</span>
              </button>
            </article>
          </div>

          <div class="mt-6 rounded-card border border-border bg-main-bg p-4">
            <div class="space-y-3">
              <div>
                <h3 class="text-sm font-semibold text-text">
                  Add workspace root
                </h3>
                <p class="mt-1 text-xs text-muted">
                  Provide an absolute path that Weave can use for directory discovery.
                </p>
              </div>

              <div class="flex flex-col gap-3 md:flex-row">
                <input
                  v-model="newWorkspaceRoot"
                  type="text"
                  :class="inputClass"
                  placeholder="/Users/you/projects"
                  :disabled="isAddingWorkspaceRoot"
                  @keydown.enter.prevent="addWorkspaceRoot"
                >

                <button
                  type="button"
                  :class="buttonPrimaryClass"
                  :disabled="isAddingWorkspaceRoot"
                  @click="addWorkspaceRoot"
                >
                  <LoaderCircle
                    v-if="isAddingWorkspaceRoot"
                    :size="16"
                    class="animate-spin"
                    aria-hidden="true"
                  />
                  <Plus
                    v-else
                    :size="16"
                    aria-hidden="true"
                  />
                  <span>{{ isAddingWorkspaceRoot ? "Adding…" : "Add root" }}</span>
                </button>
              </div>

              <div
                v-if="addWorkspaceRootError"
                class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
                role="alert"
              >
                <AlertCircle
                  :size="16"
                  class="mt-0.5 shrink-0"
                  aria-hidden="true"
                />
                <span>{{ addWorkspaceRootError }}</span>
              </div>
            </div>
          </div>
        </section>
      </div>

      <div class="grid gap-6">
        <AppearanceSection />
        <SkillsSection />

        <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
          <div class="flex flex-col gap-1">
            <h2 class="text-lg font-semibold text-text">
              Configuration overview
            </h2>
            <p class="text-sm text-muted">
              Review the active user config, installed skills, and provider connectivity reported by the API.
            </p>
          </div>

          <div
            v-if="isConfigLoading"
            class="mt-5 flex items-center gap-2 text-sm text-muted"
          >
            <LoaderCircle
              :size="16"
              class="animate-spin"
              aria-hidden="true"
            />
            <span>Loading configuration…</span>
          </div>

          <div
            v-else-if="configError"
            class="mt-5 flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
            role="alert"
          >
            <AlertCircle
              :size="16"
              class="mt-0.5 shrink-0"
              aria-hidden="true"
            />
            <span>{{ configError }}</span>
          </div>

          <div
            v-else
            class="mt-5 space-y-4"
          >
            <div class="grid gap-3 sm:grid-cols-2">
              <article class="rounded-card border border-border bg-main-bg p-4">
                <p class="text-xs font-medium uppercase tracking-wide text-muted">
                  User config
                </p>
                <p class="mt-2 break-all text-sm text-text">
                  {{ paths?.userConfig ?? "Unavailable" }}
                </p>
              </article>

              <article class="rounded-card border border-border bg-main-bg p-4">
                <p class="text-xs font-medium uppercase tracking-wide text-muted">
                  Skills directory
                </p>
                <p class="mt-2 break-all text-sm text-text">
                  {{ paths?.skillsDir ?? "Unavailable" }}
                </p>
              </article>

              <article class="rounded-card border border-border bg-main-bg p-4">
                <p class="text-xs font-medium uppercase tracking-wide text-muted">
                  Configured agents
                </p>
                <p class="mt-2 text-2xl font-semibold text-text">
                  {{ configuredAgentCount }}
                </p>
              </article>

              <article class="rounded-card border border-border bg-main-bg p-4">
                <p class="text-xs font-medium uppercase tracking-wide text-muted">
                  Installed skills
                </p>
                <p class="mt-2 text-2xl font-semibold text-text">
                  {{ installedSkills.length }}
                </p>
              </article>

              <article class="rounded-card border border-border bg-main-bg p-4">
                <p class="text-xs font-medium uppercase tracking-wide text-muted">
                  Connected providers
                </p>
                <p class="mt-2 text-2xl font-semibold text-text">
                  {{ connectedProviderCount }}
                </p>
              </article>

              <article class="rounded-card border border-border bg-main-bg p-4">
                <p class="text-xs font-medium uppercase tracking-wide text-muted">
                  Available models
                </p>
                <p class="mt-2 text-2xl font-semibold text-text">
                  {{ totalModelCount }}
                </p>
              </article>
            </div>

            <div class="rounded-card border border-border bg-main-bg p-4">
              <div class="flex flex-col gap-1">
                <h3 class="text-sm font-semibold text-text">
                  Provider status
                </h3>
                <p class="text-xs text-muted">
                  Shows connectivity and advertised models returned by <code>/api/config</code>.
                </p>
              </div>

              <div
                v-if="providers.length === 0"
                class="mt-4 rounded-card border border-dashed border-border p-4 text-sm text-muted"
              >
                No provider status reported.
              </div>

              <div
                v-else
                class="mt-4 grid gap-3"
              >
                <article
                  v-for="provider in providers"
                  :key="provider.id"
                  class="rounded-card border border-border bg-card-bg p-4"
                >
                  <div class="flex flex-wrap items-center justify-between gap-3">
                    <div>
                      <h4 class="text-sm font-semibold text-text">
                        {{ provider.name }}
                      </h4>
                      <p class="mt-1 text-xs text-muted">
                        {{ provider.authType ?? "No auth type reported" }} · {{ provider.models.length }} models
                      </p>
                    </div>

                    <span
                      :class="provider.connected
                        ? 'rounded-full border border-green-500/30 bg-green-500/10 px-2 py-1 text-[11px] font-medium text-green-300'
                        : 'rounded-full border border-border px-2 py-1 text-[11px] font-medium text-muted'"
                    >
                      {{ provider.connected ? "Connected" : "Disconnected" }}
                    </span>
                  </div>
                </article>
              </div>
            </div>
          </div>
        </section>

        <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
          <div class="flex flex-col gap-1">
            <h2 class="text-lg font-semibold text-text">
              Plugin settings
            </h2>
            <p class="text-sm text-muted">
              Extra sections registered by installed plugins appear here automatically.
            </p>
          </div>

          <div
            v-if="pluginSections.length === 0"
            class="mt-5 rounded-card border border-dashed border-border p-6 text-center"
          >
            <p class="text-sm font-medium text-text">
              No plugin settings available
            </p>
            <p class="mt-1 text-xs text-muted">
              Registered plugins can contribute settings sections through <code>getSettingsSections()</code>.
            </p>
          </div>

          <div
            v-else
            class="mt-5 grid gap-4"
          >
            <article
              v-for="section in pluginSections"
              :key="section.id"
              class="rounded-card border border-border bg-main-bg p-4"
            >
              <div class="mb-4 flex items-start gap-3">
                <div class="rounded-btn border border-border bg-card-bg p-2 text-text">
                  <component
                    :is="section.icon"
                    v-if="section.icon"
                    :size="16"
                    aria-hidden="true"
                  />
                  <Settings2
                    v-else
                    :size="16"
                    aria-hidden="true"
                  />
                </div>

                <div>
                  <h3 class="text-sm font-semibold text-text">
                    {{ section.displayName }}
                  </h3>
                  <p class="mt-1 text-xs text-muted">
                    {{ section.title }}
                  </p>
                </div>
              </div>

              <component :is="section.component" />
            </article>
          </div>
        </section>
      </div>
    </div>
  </section>
</template>
