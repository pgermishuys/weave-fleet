import { ref } from "vue";

export type AutomationViewMode = "list" | "create" | "edit";

const activeAutomationId = ref<string | null>(null);
const viewMode = ref<AutomationViewMode>("list");

export function useAutomationsNav() {
  function setActiveAutomation(id: string): void {
    activeAutomationId.value = id;
    viewMode.value = "edit";
  }

  function startCreate(): void {
    activeAutomationId.value = null;
    viewMode.value = "create";
  }

  function clearSelection(): void {
    activeAutomationId.value = null;
    viewMode.value = "list";
  }

  return {
    activeAutomationId,
    viewMode,
    setActiveAutomation,
    startCreate,
    clearSelection,
  };
}
