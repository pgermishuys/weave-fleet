<script setup lang="ts">
import type { ModLogLine } from "@/lib/mods/kept";

/** A mod's recent log lines, newest last. `lines` is null while the server has no log for the mod. */

defineProps<{ lines: ModLogLine[] | null }>();

const clock = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const time = (at: string) => clock.format(new Date(at));
</script>

<template>
  <p
    v-if="lines === null"
    class="text-xs text-muted"
  >
    The mod's log shows here once the mod runtime is running.
  </p>
  <p
    v-else-if="lines.length === 0"
    class="text-xs text-muted"
  >
    Nothing logged yet.
  </p>
  <ol
    v-else
    class="m-0 grid list-none gap-1 p-0 font-mono text-xs"
  >
    <li
      v-for="(line, index) in lines"
      :key="index"
      :data-level="line.level"
      class="break-words"
      :class="line.level === 'error' ? 'text-error' : line.level === 'warn' ? 'text-warn' : 'text-text'"
    >
      <span class="text-muted">{{ time(line.at) }}</span> {{ line.text }}
    </li>
  </ol>
</template>
