<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { jobRunning, progressTitle } from "@/lib/mods-runtime";
import { useModsRuntimeStore } from "@/stores/mods-runtime";
import { usePreferencesStore } from "@/stores/preferences";
import ModsConfirm from "./ModsConfirm.vue";
import ModsFailed from "./ModsFailed.vue";
import ModsProgress from "./ModsProgress.vue";
import { CODE } from "./classes";

/**
 * What sits under the Mods description: the question before installing, the install's progress, why it failed, and
 * the quiet line once Mods are on. The switch stays in FeaturesSection; this shows what it needs and does what the
 * buttons say. The install itself runs on the server.
 */
const props = defineProps<{ enabled: boolean }>();

const store = useModsRuntimeStore();
const preferences = usePreferencesStore();

const view = computed(() => store.view);
const actionError = shallowRef<string | null>(null);

// An install that was already running when the row first loaded "kept going while you were away".
const seenFirstView = shallowRef(false);
const cameBack = shallowRef(false);
watch(
  view,
  (next) => {
    if (!next || seenFirstView.value) return;
    seenFirstView.value = true;
    cameBack.value = jobRunning(next.job);
  },
  { immediate: true },
);

type State = "confirm" | "progress" | "failed" | "ready";
const SPOKEN = { confirm: "Mods need Bun", progress: "", failed: "Couldn't download Bun", ready: "" };

const state = computed<State | null>(() => {
  const current = view.value;
  if (!current) return null;
  if (store.confirming) return "confirm";
  const job = current.job;
  if (jobRunning(job)) return "progress";
  // Mods can't run without a Bun, so a failed first install shows why, whichever way the switch is.
  if (job?.phase === "failed" && job.reason !== "cancelled" && !current.bun) return "failed";
  return props.enabled && current.bun ? "ready" : null;
});

/** What a screen reader hears when the state changes (not every megabyte). */
const announcement = computed(() => {
  const current = view.value;
  if (state.value === "progress" && current?.job) return progressTitle(current.job);
  if (state.value === "ready") return `Mods on, Bun ${current?.bun?.version}`;
  return state.value ? SPOKEN[state.value] : "";
});

async function run(action: () => Promise<string | null>): Promise<void> {
  actionError.value = await action();
  // The server may have turned the switch on or off: show what it did.
  await preferences.refresh();
}

async function install(): Promise<void> {
  cameBack.value = false;
  store.confirming = false;
  await run(() => store.install());
}
</script>

<template>
  <div
    v-if="state || actionError"
    class="mt-3 border-t border-border pt-4"
    data-testid="mods-panel"
    :data-state="state ?? undefined"
  >
    <p
      class="sr-only"
      aria-live="polite"
    >
      {{ announcement }}
    </p>

    <template v-if="view">
      <ModsConfirm
        v-if="state === 'confirm'"
        :release="view.release"
        @install="install"
        @cancel="store.confirming = false"
      />
      <ModsProgress
        v-else-if="state === 'progress' && view.job"
        :job="view.job"
        :release="view.release"
        :came-back="cameBack"
        @cancel="run(() => store.cancel())"
      />
      <ModsFailed
        v-else-if="state === 'failed' && view.job"
        :job="view.job"
        :release="view.release"
        @retry="install"
      />
      <div
        v-else-if="state === 'ready' && view.bun"
        class="space-y-1.5"
      >
        <p class="flex items-center gap-2 text-sm text-text">
          <span
            class="h-2.5 w-2.5 shrink-0 rounded-full bg-running"
            aria-hidden="true"
          />
          Mods on · {{ view.bun.source === "configured" ? "your Bun" : "Bun" }} {{ view.bun.version }}
        </p>
        <p
          :class="CODE"
          class="break-all text-muted"
        >
          {{ view.bun.displayPath }}
        </p>
      </div>
    </template>

    <p
      v-if="actionError"
      role="alert"
      class="mt-3 text-sm text-error"
    >
      {{ actionError }}
    </p>
  </div>
</template>
