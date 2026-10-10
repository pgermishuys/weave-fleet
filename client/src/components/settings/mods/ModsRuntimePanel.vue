<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { jobRunning, progressTitle } from "@/lib/mods-runtime";
import { useModsRuntimeStore } from "@/stores/mods-runtime";
import { usePreferencesStore } from "@/stores/preferences";
import ModsConfirm from "./ModsConfirm.vue";
import ModsFailed from "./ModsFailed.vue";
import ModsOwnBunForm from "./ModsOwnBunForm.vue";
import ModsOwnOld from "./ModsOwnOld.vue";
import ModsProgress from "./ModsProgress.vue";
import ModsSecurityFailed from "./ModsSecurityFailed.vue";
import ModsStatusLine from "./ModsStatusLine.vue";

/**
 * What sits under the Mods description (every step of the Turning on Mods mockup): the question before installing,
 * the install's progress, why it failed, and the quiet line once Mods are on. The switch stays in FeaturesSection;
 * this shows what it needs and does what the buttons say. The install itself runs on the server.
 */
const props = defineProps<{ enabled: boolean }>();

const store = useModsRuntimeStore();
const preferences = usePreferencesStore();

const view = computed(() => store.view);
const busy = shallowRef(false);
const actionError = shallowRef<string | null>(null);
const ownBunError = shallowRef<string | null>(null);

// An install that was already running when Settings opened "kept going while you were away".
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
const cameBackNow = computed(() => {
  const started = view.value?.job?.startedAt ? Date.parse(view.value.job.startedAt) : Number.NaN;
  return cameBack.value && Date.now() - started >= 5000;
});

type State = "confirm" | "found" | "own-bun" | "progress" | "failed" | "security-failed" | "own-old" | "ready" | "off-again";

const state = computed<State | null>(() => {
  const current = view.value;
  if (!current) return null;
  if (store.panel === "own-bun") return "own-bun";
  if (store.panel === "confirm") return store.candidate ? "found" : "confirm";
  const job = current.job;
  if (jobRunning(job) && (job.kind === "install" || props.enabled)) return "progress";
  if (job?.phase === "failed" && job.reason !== "cancelled") {
    if (job.kind === "install" && !props.enabled) return "failed";
    if (job.kind === "security" && props.enabled && current.bun) return "security-failed";
  }
  if (props.enabled) {
    if (current.bun?.source === "configured" && !current.bun.safe) return "own-old";
    return current.bun ? "ready" : null;
  }
  return current.installedSize > 0 ? "off-again" : null;
});

/** What a screen reader hears when the state changes (not every megabyte). */
const announcement = computed(() => {
  const current = view.value;
  switch (state.value) {
    case "confirm":
    case "found":
      return "Mods need Bun";
    case "own-bun":
      return "Use my own Bun";
    case "progress":
      return current?.job ? progressTitle(current.job) : "";
    case "failed":
      return "Couldn't download Bun";
    case "security-failed":
      return `Bun ${current?.bun?.version} needs a security fix`;
    case "own-old":
      return `Your Bun ${current?.bun?.version} needs a security fix`;
    case "ready":
      return `Mods on, Bun ${current?.bun?.version}`;
    case "off-again":
      return "Mods are off. Bun stays installed.";
    default:
      return "";
  }
});

async function run(action: () => Promise<string | null>): Promise<string | null> {
  busy.value = true;
  actionError.value = null;
  try {
    const error = await action();
    // The server may have turned the switch on or off: show what it did.
    await preferences.refresh();
    return error;
  } finally {
    busy.value = false;
  }
}

async function install(): Promise<void> {
  cameBack.value = false;
  store.panel = "none";
  actionError.value = await run(() => store.install());
}

async function useBun(path: string): Promise<void> {
  store.panel = "none";
  actionError.value = await run(() => store.setBunPath(path));
}

async function submitOwnBun(path: string): Promise<void> {
  ownBunError.value = null;
  const error = await run(() => store.setBunPath(path));
  if (error) ownBunError.value = error;
  else store.panel = "none";
}

async function useFleetsOwn(): Promise<void> {
  cameBack.value = false;
  actionError.value =
    (await run(() => store.setBunPath(null))) ?? (await run(() => store.install()));
}

async function cancelInstall(): Promise<void> {
  actionError.value = await run(() => store.cancel());
}

function openOwnBun(): void {
  ownBunError.value = null;
  store.panel = "own-bun";
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
        v-if="state === 'confirm' || state === 'found'"
        :release="view.release"
        :candidate="store.candidate"
        :busy="busy"
        @install="install"
        @use-bun="useBun"
        @own="openOwnBun"
        @cancel="store.panel = 'none'"
      />
      <ModsOwnBunForm
        v-else-if="state === 'own-bun'"
        :server-error="ownBunError"
        :busy="busy"
        @submit="submitOwnBun"
        @back="store.panel = 'none'"
      />
      <ModsProgress
        v-else-if="state === 'progress' && view.job"
        :job="view.job"
        :release="view.release"
        :came-back="cameBackNow"
        @cancel="cancelInstall"
      />
      <ModsFailed
        v-else-if="state === 'failed' && view.job"
        :job="view.job"
        :release="view.release"
        :busy="busy"
        @retry="install"
        @own="openOwnBun"
      />
      <ModsSecurityFailed
        v-else-if="state === 'security-failed' && view.job && view.bun"
        :bun="view.bun"
        :job="view.job"
        :busy="busy"
        @retry="install"
      />
      <ModsOwnOld
        v-else-if="state === 'own-old' && view.bun"
        :bun="view.bun"
        :release="view.release"
        :locked="view.configuredInConfig"
        :busy="busy"
        @use-fleets="useFleetsOwn"
      />
      <ModsStatusLine
        v-else-if="state === 'ready' || state === 'off-again'"
        :kind="state"
        :bun="view.bun"
        :release="view.release"
        :installed-size="view.installedSize"
      />
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
