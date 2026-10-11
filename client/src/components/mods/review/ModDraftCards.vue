<script setup lang="ts">
import { computed, watch } from "vue";
import ModDraftCard from "@/components/mods/review/ModDraftCard.vue";
import { useModsStore } from "@/stores/mods";

/**
 * The drafts of one session, at the end of its conversation. Nothing when the Mods switch is off.
 */
const props = defineProps<{
  sessionId: string;
  /** On the phone: its own buttons and sheets. */
  phone?: boolean;
}>();

const store = useModsStore();
const drafts = computed(() => (store.isSwitchedOn ? store.draftsFor(props.sessionId) : []));

watch(
  () => [props.sessionId, store.isSwitchedOn] as const,
  async ([, on]) => {
    if (store.modsSwitch === null) await store.loadSwitch();
    if (on || store.isSwitchedOn) void store.loadDrafts(props.sessionId);
  },
  { immediate: true },
);
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
      :phone="phone"
    />
  </div>
</template>
