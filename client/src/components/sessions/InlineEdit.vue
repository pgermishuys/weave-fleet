<script setup lang="ts">
import { nextTick, onMounted, shallowRef, useTemplateRef } from "vue";
import { cn } from "@/lib/utils";

interface Props {
  initialValue: string;
  disabled?: boolean;
  placeholder?: string;
}

interface Emits {
  commit: [value: string];
  cancel: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const draftValue = shallowRef(props.initialValue);
const hasCompleted = shallowRef(false);
const inputRef = useTemplateRef<HTMLInputElement>("input");

function commit(): void {
  if (props.disabled || hasCompleted.value) {
    return;
  }

  hasCompleted.value = true;
  emit("commit", draftValue.value);
}

function cancel(): void {
  if (hasCompleted.value) {
    return;
  }

  hasCompleted.value = true;
  emit("cancel");
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter") {
    event.preventDefault();
    commit();
    return;
  }

  if (event.key === "Escape") {
    event.preventDefault();
    cancel();
  }
}

function handleBlur(): void {
  commit();
}

onMounted(async () => {
  await nextTick();
  inputRef.value?.focus();
  inputRef.value?.select();
});
</script>

<template>
  <input
    ref="input"
    v-model="draftValue"
    type="text"
    :disabled="disabled"
    :placeholder="placeholder"
    class="session-inline-edit"
    :class="cn(
      'file:text-foreground placeholder:text-muted-foreground selection:bg-primary selection:text-primary-foreground dark:bg-input/30 border-input h-8 w-full min-w-0 rounded-md border bg-transparent px-2 py-1 text-sm shadow-xs transition-[color,box-shadow] outline-none disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50',
      'focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]',
      'aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive',
    )"
    @blur="handleBlur"
    @keydown="handleKeydown"
  >
</template>
