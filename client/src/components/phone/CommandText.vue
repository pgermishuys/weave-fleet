<script setup lang="ts">
import { computed } from "vue";
import { commandWords } from "@/lib/phone/command";

/**
 * What an ask wants, in the desktop's command box: the folder it runs in first, dimmed, then `$ ` and the command,
 * wrapped only between words so `dotnet test *` stays together and `--filter` never breaks in two. One line, cut with
 * an ellipsis, where there's only room for that (an ask card in the inbox).
 */
const props = defineProps<{ command: string; directory?: string | null; oneLine?: boolean; prompt?: boolean }>();
const words = computed(() => commandWords(props.command));
</script>

<template>
  <div
    class="ph-cmd"
    :class="{ 'ph-cmd--one': oneLine }"
  >
    <span
      v-if="directory && !oneLine"
      class="ph-cmd__cwd"
    >{{ directory }}</span><span
      v-if="prompt"
      class="ph-cmd__p"
      aria-hidden="true"
    >$ </span><span v-if="oneLine">{{ command }}</span><template v-else>
      <span
        v-for="(word, index) in words"
        :key="index"
        class="ph-word"
      >{{ word }}</span>
    </template>
  </div>
</template>
