<script setup lang="ts">
import { megabytes, type ModsRuntimeBun, type ModsRuntimeRelease } from "@/lib/mods-runtime";
import { CODE } from "./classes";

/** The quiet lines: Mods are on and which Bun they run on (steps 4, 6, 8b), or off with Bun kept (step 7). */
defineProps<{
  kind: "ready" | "off-again";
  bun: ModsRuntimeBun | null;
  release: ModsRuntimeRelease;
  installedSize: number;
}>();
</script>

<template>
  <div
    v-if="kind === 'ready' && bun"
    class="space-y-1.5"
  >
    <p class="flex items-center gap-2 text-sm text-text">
      <span
        class="h-2.5 w-2.5 shrink-0 rounded-full bg-running"
        aria-hidden="true"
      />
      Mods on · {{ bun.source === "configured" ? "your Bun" : "Bun" }} {{ bun.version }}
    </p>
    <p
      :class="CODE"
      class="break-all text-muted"
    >
      {{ bun.displayPath }}
    </p>
  </div>
  <p
    v-else-if="kind === 'off-again'"
    class="max-w-2xl text-sm leading-6 text-muted"
  >
    Mods are off. Bun stays installed in <code :class="CODE">{{ release.installFolder }}</code> (about {{ megabytes(installedSize) }} MB), so turning mods on again is instant.
  </p>
</template>
