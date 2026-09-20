<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "@tanstack/vue-router";
import { CheckCircle2, Plus } from "lucide-vue-next";
import weaveLogo from "@/assets/weave_logo.png";
import HarnessSetupRows from "@/components/harness-setup/HarnessSetupRows.vue";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { refreshAllHarnesses } from "@/composables/use-harnesses";
import { harnessState } from "@/lib/harness-display";
import { useAppShellStore } from "@/stores/app-shell";
import { HARNESS_SETUP_DONE_PREFERENCE, useHarnessSetupStore, type HarnessSetupStep } from "@/stores/harness-setup";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * Setting up a harness: a welcome, installing OpenCode or Claude Code, and ready. It opens by itself on the
 * first launch of a local Fleet when no harness is ready; the dashboard banner, the new-session box and
 * Settings open it at the harness step. Cloud mode never shows it: a terminal there runs on the server.
 */

const STEPS: readonly HarnessSetupStep[] = ["welcome", "harnesses", "ready"];

const router = useRouter();
const setup = useHarnessSetupStore();
const { isOpen, step } = storeToRefs(setup);
const { config } = storeToRefs(useAppShellStore());
const preferences = usePreferencesStore();
const { harnesses, defaultHarnessType, noHarnessReason } = useEnabledHarnesses();
const rows = shallowRef<InstanceType<typeof HarnessSetupRows> | null>(null);

preferences.ensureLoaded();

const readyHarnesses = computed(() => harnesses.value.filter((harness) => harnessState(harness) === "ready"));
const readyNames = computed(() => {
  const names = readyHarnesses.value.map((harness) => harness.version ? `${harness.displayName} ${harness.version}` : harness.displayName);
  return names.join(" and ");
});

// First launch: open once, when the answer is in and there's nothing to start a session with.
let offered = false;
watch(
  () => [preferences.hasFetched, noHarnessReason.value, config.value.cloudMode] as const,
  ([prefsLoaded, reason, cloud]) => {
    if (offered || cloud || !prefsLoaded || reason === null) return;
    offered = true;
    if (preferences.get(HARNESS_SETUP_DONE_PREFERENCE, "false") !== "true") setup.open("welcome");
  },
  { immediate: true },
);

async function leave(): Promise<void> {
  await rows.value?.closeTerminal();
  setup.close();
  if (preferences.get(HARNESS_SETUP_DONE_PREFERENCE, "false") !== "true") {
    await preferences.set(HARNESS_SETUP_DONE_PREFERENCE, "true");
  }
}

async function toReady(): Promise<void> {
  await rows.value?.closeTerminal();
  // A ready harness someone turned off can't start sessions; setting it up here means wanting it on.
  for (const harness of readyHarnesses.value) {
    if (!harness.userEnabled) await preferences.set(`${harness.type}.enabled`, "true");
  }
  refreshAllHarnesses();
  step.value = "ready";
}

async function startSession(): Promise<void> {
  await leave();
  await router.navigate({ to: "/sessions/new", search: { projectId: undefined, source: undefined } });
}

function onOpenChange(open: boolean): void {
  if (!open) void leave();
}
</script>

<template>
  <Dialog
    :open="isOpen && !config.cloudMode"
    modal
    @update:open="onOpenChange"
  >
    <DialogContent
      class="max-h-[calc(100dvh-2rem)] w-full max-w-[calc(100%-2rem)] overflow-y-auto border-border bg-card-bg p-0 shadow-2xl"
      :class="step === 'harnesses' ? 'sm:max-w-2xl' : 'sm:max-w-md'"
      :show-close-button="false"
      data-testid="harness-setup-wizard"
      @interact-outside="(event) => event.preventDefault()"
      @escape-key-down="(event) => event.preventDefault()"
    >
      <DialogHeader class="sr-only">
        <DialogTitle>Set up a harness</DialogTitle>
        <DialogDescription>Install OpenCode or Claude Code so Fleet can run sessions.</DialogDescription>
      </DialogHeader>

      <div class="harness-setup">
        <section
          v-if="step === 'welcome'"
          class="harness-setup__center"
        >
          <div class="harness-setup__logo">
            <img
              :src="weaveLogo"
              alt=""
            >
          </div>
          <div>
            <h2 class="harness-setup__title">
              Welcome to Weave Fleet
            </h2>
            <p class="harness-setup__lede">
              Fleet runs your sessions through a harness: a coding tool like OpenCode or Claude Code, installed on
              this computer. Let's get one ready. It takes a minute or two.
            </p>
          </div>
          <button
            type="button"
            class="harness-setup__primary harness-setup__primary--wide"
            data-testid="harness-setup-get-started"
            @click="step = 'harnesses'"
          >
            Get started
          </button>
          <button
            type="button"
            class="harness-setup__text-btn"
            data-testid="harness-setup-skip"
            @click="void leave()"
          >
            Skip setup
          </button>
        </section>

        <section
          v-else-if="step === 'harnesses'"
          class="harness-setup__harnesses"
        >
          <div>
            <h2 class="harness-setup__title harness-setup__title--small">
              Install a harness
            </h2>
            <p class="harness-setup__lede harness-setup__lede--left">
              You need at least one. You can add the other later in Settings → Harnesses.
            </p>
          </div>
          <HarnessSetupRows
            ref="rows"
            :harnesses="harnesses"
            :default-harness-type="defaultHarnessType"
          />
          <div class="harness-setup__foot">
            <button
              type="button"
              class="harness-setup__text-btn"
              data-testid="harness-setup-skip"
              @click="void leave()"
            >
              {{ readyHarnesses.length > 0 ? "Close" : "Skip for now" }}
            </button>
            <button
              type="button"
              class="harness-setup__primary"
              :disabled="readyHarnesses.length === 0"
              data-testid="harness-setup-continue"
              @click="void toReady()"
            >
              Continue
            </button>
          </div>
        </section>

        <section
          v-else
          class="harness-setup__center"
        >
          <div class="harness-setup__badge">
            <CheckCircle2
              :size="30"
              aria-hidden="true"
            />
          </div>
          <div>
            <h2 class="harness-setup__title">
              You're all set
            </h2>
            <p class="harness-setup__lede">
              {{ readyNames }} {{ readyHarnesses.length > 1 ? "are" : "is" }} ready. Add or update harnesses any time
              in Settings → Harnesses.
            </p>
          </div>
          <button
            type="button"
            class="harness-setup__primary harness-setup__primary--wide"
            data-testid="harness-setup-start-session"
            @click="void startSession()"
          >
            <Plus
              :size="16"
              aria-hidden="true"
            />
            Start a session
          </button>
        </section>

        <div
          class="harness-setup__dots"
          aria-hidden="true"
        >
          <span
            v-for="each in STEPS"
            :key="each"
            :class="{ 'harness-setup__dot--on': each === step }"
          />
        </div>
      </div>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.harness-setup {
  display: flex;
  flex-direction: column;
  gap: 20px;
  padding: 24px;
}

.harness-setup__center {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 18px;
  padding: 12px 0;
  text-align: center;
}

.harness-setup__harnesses {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.harness-setup__logo {
  width: 64px;
  height: 64px;
  border-radius: var(--radius-card);
  padding: 10px;
  border: 1px solid var(--border);
  background: var(--card-bg);
}

.harness-setup__logo img {
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.harness-setup__badge {
  display: grid;
  place-items: center;
  width: 64px;
  height: 64px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--running) 12%, transparent);
  color: var(--running);
}

.harness-setup__title {
  margin: 0;
  color: var(--text);
  font-size: 20px;
  font-weight: 600;
}

.harness-setup__title--small {
  font-size: 18px;
}

.harness-setup__lede {
  max-width: 360px;
  margin: 6px auto 0;
  color: var(--muted);
  font-size: 13.5px;
  line-height: 1.55;
}

.harness-setup__lede--left {
  max-width: none;
  margin-left: 0;
}

.harness-setup__primary {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  border: 0;
  border-radius: var(--radius-btn);
  padding: 9px 16px;
  background: var(--accent);
  color: var(--primary-foreground);
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  transition: opacity var(--transition);
}

.harness-setup__primary:hover {
  opacity: 0.9;
}

.harness-setup__primary:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}

.harness-setup__primary--wide {
  width: 100%;
  max-width: 320px;
  padding: 10px 16px;
}

.harness-setup__text-btn {
  border: 0;
  padding: 4px;
  background: none;
  color: var(--muted);
  font-size: 12.5px;
  text-decoration: underline;
  text-underline-offset: 3px;
  cursor: pointer;
}

.harness-setup__text-btn:hover {
  color: var(--text);
}

.harness-setup__foot {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.harness-setup__dots {
  display: flex;
  justify-content: center;
  gap: 6px;
}

.harness-setup__dots span {
  width: 24px;
  height: 6px;
  border-radius: 999px;
  background: var(--border);
}

.harness-setup__dots .harness-setup__dot--on {
  background: var(--accent);
}
</style>
