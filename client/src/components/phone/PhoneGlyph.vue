<script setup lang="ts">
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";

/**
 * Desktop's status glyphs at the phone's size: the working dots, the amber diamond, the red triangle, a running
 * pulse, the idle ring, and the quiet dot a finished session gets in the session list.
 */
defineProps<{ kind: "working" | "waiting" | "error" | "running" | "idle" | "quiet"; label?: string }>();

const STATUS = { working: "active", waiting: "waiting_input", error: "error", running: "running", idle: "idle" } as const;
</script>

<template>
  <span
    v-if="kind === 'quiet'"
    class="ph-glyph ph-glyph--quiet"
    aria-hidden="true"
  />
  <span
    v-else
    class="ph-glyph"
  >
    <StatusGlyph
      :status="STATUS[kind]"
      :label="label"
    />
  </span>
</template>
