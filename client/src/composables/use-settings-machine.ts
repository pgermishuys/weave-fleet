import { createMachineChoice } from "@/composables/use-machine-choice";

const choice = createMachineChoice();

/** The machine Settings shows (see `createMachineChoice`). */
export const useSettingsMachine = choice.useChoice;

/** Settings opens on the machine you're on: forget a pick made on an earlier visit. */
export const resetSettingsMachine = choice.reset;
