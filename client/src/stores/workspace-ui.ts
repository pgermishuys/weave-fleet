import { defineStore } from "pinia";
import { shallowRef } from "vue";
import type { GitHubSessionSourcePreset } from "@/lib/github-session-source";

export const useWorkspaceUiStore = defineStore("workspace-ui", () => {
  const inlineToolDiffs = shallowRef(false);
  const newSessionDialogOpen = shallowRef(false);
  const newSessionDialogProjectId = shallowRef<string | null>(null);
  const newSessionDialogInitialSource = shallowRef<GitHubSessionSourcePreset | null>(null);

  function setInlineToolDiffs(enabled: boolean): void {
    inlineToolDiffs.value = enabled;
  }

  function toggleInlineToolDiffs(): void {
    inlineToolDiffs.value = !inlineToolDiffs.value;
  }

  function openNewSessionDialog(projectId: string | null = null, initialSource: GitHubSessionSourcePreset | null = null): void {
    newSessionDialogProjectId.value = projectId;
    newSessionDialogInitialSource.value = initialSource;
    newSessionDialogOpen.value = true;
  }

  function closeNewSessionDialog(): void {
    setNewSessionDialogOpen(false);
  }

  function setNewSessionDialogOpen(open: boolean): void {
    newSessionDialogOpen.value = open;

    if (!open) {
      newSessionDialogProjectId.value = null;
      newSessionDialogInitialSource.value = null;
    }
  }

  return {
    inlineToolDiffs,
    newSessionDialogOpen,
    newSessionDialogProjectId,
    newSessionDialogInitialSource,
    setInlineToolDiffs,
    toggleInlineToolDiffs,
    openNewSessionDialog,
    closeNewSessionDialog,
    setNewSessionDialogOpen,
  };
});
