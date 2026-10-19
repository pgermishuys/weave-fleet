<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { ShieldCheck } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import AllowedRulesPanel from "@/components/session/AllowedRulesPanel.vue";
import { useSessionPermissionRules } from "@/composables/use-session-permission-rules";

defineOptions({
  name: "AllowedChip",
});

/**
 * "Allowed · 2" in the composer's toolbar, only once the session has a rule: opens what "Don't ask again" allowed, to read
 * and remove. Flashes once when a rule is added, so the click that made it is seen to have done something.
 */
const props = defineProps<{
  sessionId: string;
  harnessType?: string;
}>();

const { rules, remove, removeAll } = useSessionPermissionRules(props.sessionId);
const open = ref(false);
const flash = ref(false);
const count = computed(() => rules.value.length);

watch(count, (now, before) => {
  if (now > before) {
    flash.value = true;
    setTimeout(() => { flash.value = false; }, 900);
  }
  if (now === 0) open.value = false;
});
</script>

<template>
  <Popover
    v-if="count > 0"
    v-model:open="open"
  >
    <PopoverTrigger as-child>
      <Button
        variant="toolbar-icon"
        size="toolbar"
        class="allowed-chip"
        :class="{ 'allowed-chip--flash': flash }"
        title="What you told the agent not to ask about again"
        data-testid="allowed-chip"
      >
        <ShieldCheck class="size-3.5" />
        <span>Allowed · {{ count }}</span>
      </Button>
    </PopoverTrigger>
    <PopoverContent
      side="top"
      align="start"
      :side-offset="8"
      class="w-[22rem]"
      data-testid="allowed-popover"
    >
      <AllowedRulesPanel
        :rules="rules"
        :harness-type="harnessType"
        @remove="remove"
        @remove-all="removeAll"
      />
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.allowed-chip { gap: 0.25rem; width: auto; flex-shrink: 0; padding: 0 0.5rem; font-size: 0.75rem; white-space: nowrap; }
.allowed-chip--flash { animation: allowed-flash 0.9s ease-out; }
@keyframes allowed-flash {
  0% { background: color-mix(in srgb, var(--primary) 35%, transparent); }
  100% { background: transparent; }
}
@media (prefers-reduced-motion: reduce) { .allowed-chip--flash { animation: none; } }
</style>
