import { defineStore } from "pinia";
import { shallowRef } from "vue";

export const useWorkspaceUiStore = defineStore("workspace-ui", () => {
  const inlineToolDiffs = shallowRef(false);
  const newSessionDialogOpen = shallowRef(false);
  const newSessionDialogProjectId = shallowRef<string | null>(null);

  function setInlineToolDiffs(enabled: boolean): void {
    inlineToolDiffs.value = enabled;
  }

  function toggleInlineToolDiffs(): void {
    inlineToolDiffs.value = !inlineToolDiffs.value;
  }

  function openNewSessionDialog(projectId: string | null = null): void {
    newSessionDialogProjectId.value = projectId;
    newSessionDialogOpen.value = true;
  }

  function closeNewSessionDialog(): void {
    newSessionDialogOpen.value = false;
  }

  function setNewSessionDialogOpen(open: boolean): void {
    newSessionDialogOpen.value = open;

    if (!open) {
      newSessionDialogProjectId.value = null;
    }
  }

  return {
    inlineToolDiffs,
    newSessionDialogOpen,
    newSessionDialogProjectId,
    setInlineToolDiffs,
    toggleInlineToolDiffs,
    openNewSessionDialog,
    closeNewSessionDialog,
    setNewSessionDialogOpen,
  };
});
