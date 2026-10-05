<script setup lang="ts">
import { computed } from "vue";
import { commandWords } from "@/lib/phone/command";

/**
 * A command in a code block that wraps only between words, so `dotnet test *` stays together and `--filter` never
 * breaks in two; the folder it runs in comes first, dimmed.
 */
const props = defineProps<{ command: string; directory?: string | null; oneLine?: boolean }>();
const words = computed(() => commandWords(props.command));
</script>

<template>
  <p
    class="ph-code"
    :class="oneLine ? 'ph-code--one' : 'ph-code--wrap'"
  >
    <template v-if="oneLine">
      {{ command }}
    </template>
    <template v-else>
      <span
        v-if="directory"
        class="ph-prompt"
      >{{ directory }} $ </span><span
        v-for="(word, index) in words"
        :key="index"
        class="ph-word"
      >{{ word }}</span>
    </template>
  </p>
</template>
