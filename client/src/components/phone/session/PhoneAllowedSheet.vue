<script setup lang="ts">
import BottomSheet from "@/components/phone/BottomSheet.vue";
import AllowedRulesPanel from "@/components/session/AllowedRulesPanel.vue";
import type { AllowedRule } from "@/lib/tools";

/** What "Don't ask again" allowed in the session, in a sheet from the bottom, as the composer's Allowed chip opens on the desktop. */
defineProps<{ open: boolean; rules: readonly AllowedRule[]; harnessType?: string }>();
const emit = defineEmits<{ (event: "remove", ruleId: string): void; (event: "removeAll"): void; (event: "close"): void }>();
</script>

<template>
  <BottomSheet
    :open="open"
    label="Allowed"
    title="Allowed in this session"
    @close="emit('close')"
  >
    <div class="ph-card allowed-sheet">
      <AllowedRulesPanel
        :rules="rules"
        :harness-type="harnessType"
        @remove="emit('remove', $event)"
        @remove-all="emit('removeAll')"
      />
    </div>
  </BottomSheet>
</template>

<style scoped>
.allowed-sheet { padding: 0.75rem 1rem; }
</style>
