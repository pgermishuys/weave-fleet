import { computed, ref, shallowRef } from "vue";
import { defineStore } from "pinia";
import { api } from "@/api/client";
import { extractApiError } from "@/lib/api-error";
import {
  defaultWorktreeNaming,
  type WorktreeNamingLayer,
  type WorktreeNamingTemplates,
} from "@/lib/worktree-naming";

/** A layer's own templates. An absent field is one that layer doesn't set. */
export interface WorktreeNamingLayerTemplates {
  branch?: string | null;
  root?: string | null;
  folder?: string | null;
  capture?: Record<string, string> | null;
  initials?: string | null;
}

/**
 * The worktree naming templates in force, per repository. The server resolves them for real; this
 * is what Settings edits and what the composer previews with.
 */
export const useWorktreeNamingStore = defineStore("worktree-naming", () => {
  const effective = ref<WorktreeNamingTemplates>({ ...defaultWorktreeNaming });
  const user = ref<WorktreeNamingLayerTemplates>({});
  const defaults = ref<WorktreeNamingTemplates>({ ...defaultWorktreeNaming });
  const layers = ref<Record<string, WorktreeNamingLayer>>({});
  const isLoading = shallowRef(false);
  const isSaving = shallowRef(false);
  const error = shallowRef<string | null>(null);
  /** The repository the loaded templates are for, so the composer can tell a stale answer. */
  const loadedFor = shallowRef<string | null>(null);

  /** True when this repository ships its own convention, which locks those fields in Settings. */
  const hasProjectConvention = computed(() =>
    Object.values(layers.value).some((layer) => layer === "project"));

  function apply(data: {
    effective: WorktreeNamingLayerTemplates;
    user: WorktreeNamingLayerTemplates;
    defaults: WorktreeNamingLayerTemplates;
    layers: Record<string, string>;
  }): void {
    effective.value = {
      branch: data.effective.branch ?? defaultWorktreeNaming.branch,
      root: data.effective.root ?? defaultWorktreeNaming.root,
      folder: data.effective.folder ?? defaultWorktreeNaming.folder,
      capture: data.effective.capture ?? null,
      initials: data.effective.initials ?? null,
    };
    user.value = { ...data.user };
    defaults.value = {
      branch: data.defaults.branch ?? defaultWorktreeNaming.branch,
      root: data.defaults.root ?? defaultWorktreeNaming.root,
      folder: data.defaults.folder ?? defaultWorktreeNaming.folder,
    };
    layers.value = data.layers as Record<string, WorktreeNamingLayer>;
  }

  /** Loads the templates for a repository, or the user's own when no repository is given. */
  async function load(directory?: string | null): Promise<void> {
    isLoading.value = true;
    error.value = null;

    try {
      const { data, error: apiError } = await api.GET("/api/worktrees/naming", {
        params: { query: directory ? { directory } : {} },
      });

      if (apiError || !data) {
        error.value = extractApiError(apiError, "Couldn't load worktree naming.");
        return;
      }

      apply(data);
      loadedFor.value = directory ?? null;
    } catch (cause) {
      // The composer previews with whatever it has, so a naming request that never lands leaves
      // Fleet's defaults in place rather than taking the composer down with it.
      error.value = extractApiError(cause, "Couldn't load worktree naming.");
    } finally {
      isLoading.value = false;
    }
  }

  /** Saves the user's layer. The server refuses a template that can't name a worktree. */
  async function save(layer: WorktreeNamingLayerTemplates): Promise<boolean> {
    isSaving.value = true;
    error.value = null;

    try {
      const { data, error: apiError } = await api.PUT("/api/worktrees/naming", {
        body: {
          branch: layer.branch ?? null,
          root: layer.root ?? null,
          folder: layer.folder ?? null,
          capture: layer.capture ?? null,
          initials: layer.initials ?? null,
        },
      });

      if (apiError || !data) {
        error.value = extractApiError(apiError, "Couldn't save worktree naming.");
        return false;
      }

      apply(data);
      loadedFor.value = null;
      return true;
    } catch (cause) {
      error.value = extractApiError(cause, "Couldn't save worktree naming.");
      return false;
    } finally {
      isSaving.value = false;
    }
  }

  return {
    effective,
    user,
    defaults,
    layers,
    isLoading,
    isSaving,
    error,
    loadedFor,
    hasProjectConvention,
    load,
    save,
  };
});
