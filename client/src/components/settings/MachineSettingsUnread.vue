<script setup lang="ts">
import { Button } from "@/components/ui/button";
import { useSettingsMachine } from "@/composables/use-settings-machine";

/** Settings shows another machine and couldn't read its settings: its rows are off until it answers. */
defineEmits<{ retry: [] }>();
const settingsMachine = useSettingsMachine();
</script>

<template>
  <div
    class="flex flex-wrap items-center gap-3 rounded-btn border border-border px-4 py-3 text-sm text-muted"
    role="status"
    data-testid="machine-settings-unread"
  >
    <span>
      Couldn't read <span class="font-mono font-semibold text-[var(--coral)]">{{ settingsMachine.entry.value.name }}</span>'s
      settings, so its rows can't be changed until it answers. Rows marked This app still can.
    </span>
    <Button
      variant="outline"
      size="sm"
      data-testid="machine-settings-retry"
      @click="$emit('retry')"
    >
      Try again
    </Button>
  </div>
</template>
