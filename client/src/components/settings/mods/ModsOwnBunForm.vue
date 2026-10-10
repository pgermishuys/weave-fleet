<script setup lang="ts">
import { shallowRef } from "vue";
import { BUN_PATH_HINT, isAbsoluteBunPath } from "@/lib/mods-runtime";
import { CODE, PRIMARY_BUTTON, SECONDARY_BUTTON } from "./classes";

/** Use my own Bun… (step 5c): the full path to a Bun the user installed. Fleet checks it runs before it's kept. */
defineProps<{
  /** The server's reason it refused the path. */
  serverError?: string | null;
  busy?: boolean;
}>();

const emit = defineEmits<{ submit: [path: string]; back: [] }>();

const path = shallowRef("");
const localError = shallowRef<string | null>(null);

const hintId = "mods-own-bun-hint";
const errorId = "mods-own-bun-error";

function submit(): void {
  const value = path.value.trim();
  if (!isAbsoluteBunPath(value)) {
    localError.value = BUN_PATH_HINT;
    return;
  }
  localError.value = null;
  emit("submit", value);
}
</script>

<template>
  <section
    aria-labelledby="mods-own-bun-heading"
    class="space-y-3"
  >
    <h4
      id="mods-own-bun-heading"
      class="text-sm font-medium text-text"
    >
      Use my own Bun
    </h4>
    <p class="max-w-xl text-sm leading-6 text-muted">
      Install Bun 1.4 or later yourself, then give Fleet the full path to it. Fleet runs <code :class="CODE">bun --version</code> to check it, and never downloads one while this is set.
    </p>
    <form
      class="space-y-2"
      @submit.prevent="submit"
    >
      <div class="flex flex-wrap items-center gap-2">
        <label
          for="mods-own-bun-input"
          class="sr-only"
        >Path to Bun</label>
        <input
          id="mods-own-bun-input"
          v-model="path"
          type="text"
          spellcheck="false"
          autocomplete="off"
          placeholder="/opt/tools/bun/bin/bun"
          :aria-describedby="localError || serverError ? errorId : hintId"
          :aria-invalid="localError || serverError ? 'true' : undefined"
          class="min-w-0 flex-1 rounded-md border border-border bg-card-bg px-3 py-1.5 font-mono text-sm text-text focus:outline-none focus:ring-2 focus:ring-accent"
        >
        <button
          type="submit"
          :class="PRIMARY_BUTTON"
          :disabled="busy"
        >
          Use this Bun
        </button>
        <button
          type="button"
          :class="SECONDARY_BUTTON"
          @click="emit('back')"
        >
          Back
        </button>
      </div>
      <p
        v-if="localError || serverError"
        :id="errorId"
        role="alert"
        class="text-sm text-error"
      >
        {{ localError ?? serverError }}
      </p>
      <p
        v-else
        :id="hintId"
        class="text-sm text-muted"
      >
        The path must start at the root, like <code :class="CODE">/opt/tools/bun/bin/bun</code> or <code :class="CODE">C:\Tools\bun\bun.exe</code>.
      </p>
    </form>
  </section>
</template>
