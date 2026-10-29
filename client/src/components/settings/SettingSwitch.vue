<script setup lang="ts">
import { LoaderCircle } from "lucide-vue-next";

/** The on/off switch every settings row uses. `aria-label` and `data-testid` fall through to the button. */
defineOptions({ inheritAttrs: false });

defineProps<{
  checked: boolean;
  disabled?: boolean;
  /** Shows a spinner beside the switch while the change is being saved. */
  saving?: boolean;
  /** A ring that says the switch is waiting for an answer elsewhere (Mods asking to install Bun). */
  pending?: boolean;
}>();

defineEmits<{ click: [] }>();
</script>

<template>
  <div class="flex items-center gap-2">
    <LoaderCircle
      v-if="saving"
      :size="16"
      class="animate-spin text-muted"
      aria-hidden="true"
    />
    <button
      v-bind="$attrs"
      type="button"
      role="switch"
      :aria-checked="checked"
      :disabled="disabled"
      class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-card-bg disabled:cursor-not-allowed disabled:opacity-60"
      :class="[checked ? 'bg-accent' : 'bg-border', pending ? 'ring-2 ring-accent ring-offset-2 ring-offset-card-bg' : '']"
      @click="$emit('click')"
    >
      <span
        class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
        :class="checked ? 'translate-x-5' : 'translate-x-0'"
      />
    </button>
  </div>
</template>
