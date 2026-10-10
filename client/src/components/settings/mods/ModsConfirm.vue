<script setup lang="ts">
import { computed } from "vue";
import { megabytes, type BunCandidate, type ModsRuntimeRelease } from "@/lib/mods-runtime";
import { CODE, PRIMARY_BUTTON, SECONDARY_BUTTON } from "./classes";

/**
 * What Fleet will do, said before it does anything (steps 2 and 2c): download its own Bun, or, when it found one on
 * this computer, offer that first. The switch stays off until the user chooses.
 */
const props = defineProps<{
  release: ModsRuntimeRelease;
  /** A Bun already on this computer that Mods can run on. */
  candidate: BunCandidate | null;
  busy?: boolean;
}>();

defineEmits<{
  install: [];
  "use-bun": [path: string];
  own: [];
  cancel: [];
}>();

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
      v-else-if="candidate"
      class="max-w-xl text-sm leading-6 text-muted"
    >
      Fleet found Bun {{ candidate.version }} at <code :class="CODE">{{ candidate.displayPath ?? candidate.path }}</code>. Mods can run on it, and you keep it up to date with <code :class="CODE">bun upgrade</code>. Or Fleet can download its own Bun {{ release.version }}{{ size }} from <code :class="CODE">{{ release.source }}</code>, check it, install it in <code :class="CODE">{{ release.installFolder }}</code> and keep it up to date.
    </p>
    <p
      v-else
      class="max-w-xl text-sm leading-6 text-muted"
    >
      Mods run in a small Bun program beside Fleet. To turn them on, Fleet downloads Bun {{ release.version }}{{ size }} from <code :class="CODE">{{ release.source }}</code>, checks it against a checksum built into Fleet, and installs it in <code :class="CODE">{{ release.installFolder }}</code>. Nothing else on your computer changes.
    </p>

    <div class="flex flex-wrap items-center gap-2">
      <template v-if="!release.hasBuild">
        <button
          type="button"
          :class="PRIMARY_BUTTON"
          :disabled="busy"
          @click="$emit('own')"
        >
          Use my own Bun…
        </button>
      </template>
      <template v-else-if="candidate">
        <button
          type="button"
          :class="PRIMARY_BUTTON"
          :disabled="busy"
          @click="$emit('use-bun', candidate.path)"
        >
          Use my Bun {{ candidate.version }}
        </button>
        <button
          type="button"
          :class="SECONDARY_BUTTON"
          :disabled="busy"
          @click="$emit('install')"
        >
          Download Fleet's own
        </button>
      </template>
      <button
        v-else
        type="button"
        :class="PRIMARY_BUTTON"
        :disabled="busy"
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
