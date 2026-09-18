import { defineStore } from "pinia";
import { shallowRef } from "vue";

/** The harness setup wizard's steps: a welcome, installing a harness, and ready. */
export type HarnessSetupStep = "welcome" | "harnesses" | "ready";

/** Set once the first-run wizard was finished or skipped, so it doesn't open on every launch. */
export const HARNESS_SETUP_DONE_PREFERENCE = "harnessSetup.done";

/**
 * Whether the harness setup wizard is open, and on which step. It opens by itself on the first launch of a
 * local Fleet with no harness ready, and from the dashboard banner, the new-session box and Settings.
 */
export const useHarnessSetupStore = defineStore("harness-setup", () => {
  const isOpen = shallowRef(false);
  const step = shallowRef<HarnessSetupStep>("welcome");

  function open(at: HarnessSetupStep = "harnesses"): void {
    step.value = at;
    isOpen.value = true;
  }

  function close(): void {
    isOpen.value = false;
  }

  return { isOpen, step, open, close };
});
