<script setup lang="ts">
import { computed, watch } from "vue";
import ModDraftCard from "@/components/mods/review/ModDraftCard.vue";
import type { AccumulatedMessage } from "@/lib/client-types";
import { useModsStore } from "@/stores/mods";

/**
 * The drafts of one session, at the end of its conversation. Nothing when the Mods switch is off.
 */
const props = defineProps<{
  sessionId: string;
  /** The session's messages: a finished `fleet_mod_*` call means the agent wrote a draft, so the list is read again. */
  messages?: readonly AccumulatedMessage[];
}>();

const store = useModsStore();
const drafts = computed(() => (store.isSwitchedOn ? store.draftsFor(props.sessionId) : []));

/** Until the server raises an event for a written draft, a completed `fleet_mod_*` call is the signal. */
const MOD_TOOL = /^(mcp__fleet__)?fleet_mod_/;
const finishedModCalls = computed(() => {
  let count = 0;
  for (const message of props.messages ?? []) {
    for (const part of message.parts) {
      if (part.type !== "tool" || !MOD_TOOL.test(part.tool)) continue;
      const status = (part.state as { status?: unknown } | null)?.status;
      if (status === "completed") count++;
    }
  }
  return count;
});

watch(
  () => [props.sessionId, store.isSwitchedOn] as const,
  async ([, on]) => {
    if (store.modsSwitch === null) await store.loadSwitch();
    if (on || store.isSwitchedOn) void store.loadDrafts(props.sessionId);
  },
  { immediate: true },
);

watch(finishedModCalls, (now, before) => {
  if (now > before && store.isSwitchedOn) void store.loadDrafts(props.sessionId);
});
</script>

<template>
  <div
    v-if="drafts.length > 0"
    class="mod-draft-cards"
    data-testid="mod-draft-cards"
  >
    <ModDraftCard
      v-for="draft in drafts"
      :key="draft.name"
      :draft="draft"
    />
  </div>
</template>
