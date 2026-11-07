import { createMachineChoice } from "@/composables/use-machine-choice";

const choice = createMachineChoice();

/** The machine Workflows shows (see `createMachineChoice`). */
export const useWorkflowsMachine = choice.useChoice;

/** Workflows opens on the machine you're on: forget a pick made on an earlier visit. */
export const resetWorkflowsMachine = choice.reset;
