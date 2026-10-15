<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import { refreshAllHarnesses } from "@/composables/use-harnesses";
import { usePreferencesStore } from "@/stores/preferences";
import type { HarnessInfo } from "@/api/client";

/** Where Fleet finds the program a harness runs, for when it isn't on PATH: the path, a Check button, and what Fleet found. */
const props = defineProps<{
  harness: HarnessInfo;
  /** The preference that holds the path (`copilot.path`). */
  preferenceKey: string;
  /** What the program is called, e.g. `weave-copilot`. */
  program: string;
}>();

const prefs = usePreferencesStore();
const saved = computed(() => prefs.get(props.preferenceKey, ""));
const draft = shallowRef(saved.value);
const checking = shallowRef(false);
const error = shallowRef<string | null>(null);

// The saved path arrives after the preferences load.
watch(saved, (value) => { draft.value = value; });

const found = computed(() => [props.harness.version, props.harness.executablePath].filter(Boolean).join(" · "));

async function check(): Promise<void> {
  checking.value = true;
  error.value = null;
  try {
    const path = draft.value.trim();
    if (path !== saved.value) await prefs.set(props.preferenceKey, path);
    refreshAllHarnesses();
  } catch (e) {
    error.value = e instanceof Error ? e.message : `Fleet couldn't save the path to ${props.program}.`;
  } finally {
    checking.value = false;
  }
}
</script>

<template>
  <div
    class="rounded-card border border-border bg-main-bg p-4"
    data-testid="harness-path-field"
  >
    <label
      class="text-sm font-medium text-text"
      :for="`path-${preferenceKey}`"
    >Path to {{ program }}</label>
    <p class="mt-1 text-xs text-muted">
      Leave it empty to find {{ program }} on PATH.
    </p>
    <div class="mt-2 flex flex-wrap items-center gap-2">
      <input
        :id="`path-${preferenceKey}`"
        v-model="draft"
        type="text"
        spellcheck="false"
        :placeholder="program"
        class="min-w-0 flex-1 rounded-btn border border-border bg-card-bg px-2.5 py-1.5 font-mono text-xs text-text focus:outline-none focus:ring-2 focus:ring-accent"
        data-testid="harness-path-input"
        @keydown.enter="void check()"
      >
      <button
        type="button"
        class="inline-flex items-center gap-1.5 rounded-btn border border-border bg-card-bg px-3 py-1.5 text-xs font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60"
        :disabled="checking"
        data-testid="harness-path-check"
        @click="void check()"
      >
        <LoaderCircle
          v-if="checking"
          :size="12"
          class="animate-spin"
          aria-hidden="true"
        />
        Check
      </button>
    </div>
    <p
      v-if="error"
      class="mt-2 text-xs text-red-300"
      role="alert"
    >
      {{ error }}
    </p>
    <p
      v-else-if="harness.available && found"
      class="mt-2 break-all font-mono text-[11px] text-muted"
      data-testid="harness-path-found"
    >
      Found: {{ found }}
    </p>
    <p
      v-else-if="!harness.available && harness.reason"
      class="mt-2 text-xs text-muted"
      data-testid="harness-path-reason"
    >
      {{ harness.reason }}
    </p>
  </div>
</template>
