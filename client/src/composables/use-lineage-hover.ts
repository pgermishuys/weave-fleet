import { shallowRef } from "vue";

/**
 * The top-level session whose family the pointer (or keyboard focus) is on in the session list. Shared by every project
 * group, so a hover in one lights its family there and not the open session's family in another group.
 */
export const hoveredFamily = shallowRef<string | null>(null);
