import { createMachineChoice } from "@/composables/use-machine-choice";

const choice = createMachineChoice();

/** The machine Automations shows (see `createMachineChoice`). */
export const useAutomationsMachine = choice.useChoice;

/** Automations opens on the machine you're on: forget a pick made on an earlier visit. */
export const resetAutomationsMachine = choice.reset;
