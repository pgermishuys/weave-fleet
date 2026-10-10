<script setup lang="ts">
import { shallowRef } from "vue";
import { TriangleAlert } from "lucide-vue-next";
import type { ModsRuntimeBun, ModsRuntimeRelease } from "@/lib/mods-runtime";
import { CODE, SECONDARY_BUTTON } from "./classes";

/**
 * The user's own Bun needs a security fix (step 10). Fleet doesn't replace a Bun it didn't install: it says to run
 * `bun upgrade`, picks the new version up by itself, and offers its own Bun instead.
 */
defineProps<{
  bun: ModsRuntimeBun;
  release: ModsRuntimeRelease;
  /** Fleet's configuration sets the path, so it can't be switched here. */
  locked?: boolean;
  busy?: boolean;
}>();

defineEmits<{ "use-fleets": [] }>();

const copied = shallowRef(false);

async function copy(): Promise<void> {
  try {
    await navigator.clipboard?.writeText("bun upgrade");
    copied.value = true;
    setTimeout(() => (copied.value = false), 1500);
  } catch {
    // The command is on screen to select by hand.
  }
}
</script>

<template>
  <section
    aria-labelledby="mods-own-old-heading"
    class="space-y-3"
  >
    <h4
      id="mods-own-old-heading"
      class="flex items-center gap-2 text-sm font-medium text-idle"
    >
      <TriangleAlert
        :size="16"
        aria-hidden="true"
      />
      Your Bun {{ bun.version }} needs a security fix
    </h4>
    <p class="max-w-xl text-sm leading-6 text-muted">
      Bun {{ release.version }} fixes a security problem in {{ bun.version }}. Update your Bun and Fleet picks up the new version by itself. Mods keep running meanwhile.
    </p>
    <div class="flex flex-wrap items-center gap-2">
      <code
        :class="CODE"
        class="rounded-md border border-border bg-card-bg px-3 py-1.5"
      >bun upgrade</code>
      <button
        type="button"
        :class="SECONDARY_BUTTON"
        @click="copy"
      >
        {{ copied ? "Copied" : "Copy" }}
      </button>
      <button
        v-if="!locked"
        type="button"
        :class="SECONDARY_BUTTON"
        :disabled="busy"
        @click="$emit('use-fleets')"
      >
        Use Fleet's own Bun instead
      </button>
    </div>
  </section>
</template>
