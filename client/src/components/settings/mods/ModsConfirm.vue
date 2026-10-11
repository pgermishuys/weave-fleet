<script setup lang="ts">
import { computed } from "vue";
import { megabytes, type ModsRuntimeRelease } from "@/lib/mods-runtime";
import { CODE, PRIMARY_BUTTON, SECONDARY_BUTTON } from "./classes";

/** What Fleet will do, said before it does anything (step 2). The switch stays off until the user chooses. */
const props = defineProps<{ release: ModsRuntimeRelease }>();

defineEmits<{ install: []; cancel: [] }>();

const size = computed(() => (props.release.size ? ` (about ${megabytes(props.release.size)} MB)` : ""));
</script>

<template>
  <section
    aria-labelledby="mods-confirm-heading"
    class="space-y-3"
  >
    <h4
      id="mods-confirm-heading"
      class="text-sm font-medium text-text"
    >
      Mods need Bun
    </h4>

    <p
      v-if="!release.hasBuild"
      class="max-w-xl text-sm leading-6 text-muted"
    >
      Fleet has no Bun build for this computer. Install Bun yourself and point Fleet at it.
    </p>
    <p
      v-else
      class="max-w-xl text-sm leading-6 text-muted"
    >
      Mods run in a small Bun program beside Fleet. To turn them on, Fleet downloads Bun {{ release.version }}{{ size }} from <code :class="CODE">{{ release.source }}</code>, checks it against a checksum built into Fleet, and installs it in <code :class="CODE">{{ release.installFolder }}</code>. Nothing else on your computer changes.
    </p>

    <div class="flex flex-wrap items-center gap-2">
      <button
        v-if="release.hasBuild"
        type="button"
        :class="PRIMARY_BUTTON"
        @click="$emit('install')"
      >
        Turn on and install
      </button>
      <button
        type="button"
        :class="SECONDARY_BUTTON"
        @click="$emit('cancel')"
      >
        Cancel
      </button>
    </div>
  </section>
</template>
