import { createMachineChoice } from "@/composables/use-machine-choice";

const choice = createMachineChoice();

/** The machine Analytics shows (see `createMachineChoice`). */
export const useAnalyticsMachine = choice.useChoice;

/** Analytics opens on the machine you're on: forget a pick made on an earlier visit. */
export const resetAnalyticsMachine = choice.reset;
