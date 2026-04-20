import { defineStore } from "pinia";
import { ref } from "vue";

export interface SlashCommand {
  id: string;
  name: string;
  description: string;
}

export const useSlashCommandsStore = defineStore("slash-commands", () => {
  const registeredSlashCommands = ref<SlashCommand[]>([]);

  return {
    registeredSlashCommands,
  };
});
