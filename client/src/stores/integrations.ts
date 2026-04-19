import { defineStore } from "pinia";
import { ref } from "vue";

export interface IntegrationSummary {
  id: string;
  name: string;
  connected: boolean;
}

export const useIntegrationsStore = defineStore("integrations", () => {
  const integrations = ref<IntegrationSummary[]>([]);

  return {
    integrations,
  };
});
