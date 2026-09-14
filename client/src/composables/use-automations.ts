import { storeToRefs } from "pinia";
import type { Ref, ShallowRef } from "vue";
import {
  useAutomationsStore,
  type Automation,
  type CreateAutomationRequest,
  type UpdateAutomationRequest,
} from "@/stores/automations";

export type { Automation, CreateAutomationRequest, UpdateAutomationRequest };

export interface UseAutomationsResult {
  automations: Readonly<Ref<readonly Automation[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  refresh: () => Promise<void>;
  createAutomation: (request: CreateAutomationRequest) => Promise<Automation>;
  updateAutomation: (id: string, request: UpdateAutomationRequest) => Promise<void>;
  deleteAutomation: (id: string) => Promise<void>;
  enableAutomation: (id: string) => Promise<void>;
  disableAutomation: (id: string) => Promise<void>;
  runAutomation: (id: string) => Promise<void>;
  fetchEventCatalog: () => Promise<string[]>;
}

/** The shared automations list (see stores/automations.ts), reloaded whenever a view that shows it mounts. */
export function useAutomations(): UseAutomationsResult {
  const store = useAutomationsStore();
  const { automations, isLoading, error } = storeToRefs(store);
  void store.refresh();

  return {
    automations,
    isLoading,
    error,
    refresh: store.refresh,
    createAutomation: store.createAutomation,
    updateAutomation: store.updateAutomation,
    deleteAutomation: store.deleteAutomation,
    enableAutomation: store.enableAutomation,
    disableAutomation: store.disableAutomation,
    runAutomation: store.runAutomation,
    fetchEventCatalog: store.fetchEventCatalog,
  };
}
