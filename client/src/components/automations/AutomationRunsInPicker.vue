<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronDown, MessageSquare, Repeat } from "lucide-vue-next";
import { DropdownMenuItem } from "reka-ui";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";

/** Whether each run starts a new session or continues the one the first run started. */

const props = defineProps<{
  /** "new_session", "same_session", or an older automation's target type, which stays until changed. */
  targetType: string;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  "update:targetType": [targetType: string];
  closeAutoFocus: [event: Event];
}>();

const OLDER: Record<string, string> = {
  most_recent_session: "The most recent session",
  tagged_session: "The newest session with its tags",
};

const chipLabel = computed(() => {
  if (props.targetType === "same_session") return "Same session each run";
  return OLDER[props.targetType] ?? "New session each run";
});
</script>

<template>
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="automation-runs-in-chip"
        :disabled="disabled"
      >
        <Repeat
          v-if="targetType === 'same_session'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <MessageSquare
          v-else
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">{{ chipLabel }}</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </DropdownMenuTrigger>
    <DropdownMenuContent
      class="ns-pop"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <DropdownMenuItem
        class="ns-option"
        data-testid="automation-runs-in-new"
        @select="emit('update:targetType', 'new_session')"
      >
        <MessageSquare
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">New session each run</span>
          <span class="ns-option__detail">each run starts fresh</span>
        </span>
        <Check
          v-if="targetType === 'new_session'"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
      <DropdownMenuItem
        class="ns-option"
        data-testid="automation-runs-in-same"
        @select="emit('update:targetType', 'same_session')"
      >
        <Repeat
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Same session each run</span>
          <span class="ns-option__detail">later runs continue the first run's session</span>
        </span>
        <Check
          v-if="targetType === 'same_session'"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
      <DropdownMenuItem
        v-if="OLDER[targetType]"
        class="ns-option"
        @select="emit('update:targetType', targetType)"
      >
        <MessageSquare
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">{{ OLDER[targetType] }}</span>
          <span class="ns-option__detail">how this automation was set up</span>
        </span>
        <Check
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
