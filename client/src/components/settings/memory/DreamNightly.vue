<script setup lang="ts">
import { computed, ref, shallowRef, watch } from "vue";
import { useNavigate } from "@tanstack/vue-router";
import { LoaderCircle } from "lucide-vue-next";
import ModelSelector from "@/components/session/ModelSelector.vue";
import { useDreamingFeature } from "@/composables/use-dreaming-feature";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useHarnessCatalog } from "@/composables/use-harness-catalog";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { keyForModel, modelFromKey, modelFromPath, modelToPath } from "@/lib/agent-model-choice";
import {
  DREAM_BUDGETS,
  NIGHTLY_HARNESSES,
  formatTokens,
  getNightlyDreaming,
  setNightlyDreaming,
  type NightlyDreamingChange,
  type NightlyDreamingView,
} from "@/lib/dreaming";

/** The harness Dream each night uses, so Dream now can follow it. */
const emit = defineEmits<{ harness: [type: string | null] }>();

const { isDreamingEnabled } = useDreamingFeature();
const { enabledHarnesses } = useEnabledHarnesses();
const navigate = useNavigate();
const { setActiveAutomation } = useAutomationsNav();

const nightly = shallowRef<NightlyDreamingView | null>(null);
const error = ref<string | null>(null);
const isSaving = ref(false);

const choices = computed(() => enabledHarnesses.value.filter((harness) => (NIGHTLY_HARNESSES as readonly string[]).includes(harness.type)));
const harnessType = computed(() => {
  const saved = nightly.value?.harnessType;
  if (saved && choices.value.some((harness) => harness.type === saved)) return saved;
  return choices.value.find((harness) => harness.type === "opencode2")?.type ?? choices.value[0]?.type ?? "";
});
const { models, catalog } = useHarnessCatalog(harnessType, ref(null));
const modelKey = computed(() => {
  const model = modelFromPath(nightly.value?.model);
  return model ? keyForModel(model) : "";
});

async function load(): Promise<void> {
  if (!isDreamingEnabled.value) {
    nightly.value = null;
    return;
  }
  try {
    nightly.value = await getNightlyDreaming();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't load nightly dreaming.";
  }
}

async function save(change: NightlyDreamingChange): Promise<void> {
  if (isSaving.value) return;
  isSaving.value = true;
  error.value = null;
  try {
    nightly.value = await setNightlyDreaming(change);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't save nightly dreaming.";
  } finally {
    isSaving.value = false;
  }
}

const zone = (): string => Intl.DateTimeFormat().resolvedOptions().timeZone;
const toggle = (): Promise<void> => save({ enabled: !nightly.value?.enabled, timeZone: zone() });
const pickTime = (time: string): Promise<void> => (time ? save({ time, timeZone: zone() }) : Promise.resolve());
// A model belongs to its harness, so changing the harness clears it.
const pickHarness = (type: string): Promise<void> => save({ harnessType: type, model: null });
const pickModel = (key: string): Promise<void> => save({ model: modelToPath(modelFromKey(key)) });

function openAutomation(): void {
  if (nightly.value?.automationId) setActiveAutomation(nightly.value.automationId);
  void navigate({ to: "/automations" });
}

watch(isDreamingEnabled, () => void load(), { immediate: true });
watch(() => nightly.value?.enabled && harnessType.value, (type) => emit("harness", type || null), { immediate: true });
</script>

<template>
  <div
    v-if="isDreamingEnabled && nightly"
    class="mt-5 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4"
    data-testid="dream-nightly"
  >
    <div class="min-w-0 flex-1">
      <p class="flex items-center gap-2 text-sm font-medium text-text">
        Dream each night
        <span class="rounded-full border border-border px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-muted">Experimental</span>
      </p>
      <p class="mt-1 text-xs text-muted">
        Fleet reads the day's sessions in each repository you worked in and updates its notes. Every change says why, and you can undo it. Notes you wrote yourself are never rewritten or removed.
      </p>

      <div
        v-if="nightly.enabled"
        class="mt-3 flex flex-wrap items-center gap-2 text-sm"
      >
        <input
          type="time"
          :value="nightly.time"
          aria-label="Time to dream"
          data-testid="dream-time"
          class="rounded-btn border border-border bg-main-bg px-2 py-1 text-text outline-none focus:border-accent"
          @change="pickTime(($event.target as HTMLInputElement).value)"
        >
        <select
          :value="harnessType"
          aria-label="Harness for the nightly dream"
          data-testid="dream-nightly-harness"
          class="rounded-btn border border-border bg-main-bg px-2 py-1 text-text outline-none focus:border-accent"
          @change="pickHarness(($event.target as HTMLSelectElement).value)"
        >
          <option
            v-for="harness in choices"
            :key="harness.type"
            :value="harness.type"
          >
            {{ harness.displayName }}
          </option>
        </select>
        <ModelSelector
          v-if="catalog?.supported"
          :model-value="modelKey"
          :models="models"
          :harness-type="harnessType"
          :show-recent="false"
          :disabled="isSaving"
          test-id="dream-nightly-model"
          @update:model-value="pickModel"
        />
        <select
          :value="nightly.budgetTokens"
          aria-label="Tokens a night"
          data-testid="dream-budget"
          class="rounded-btn border border-border bg-main-bg px-2 py-1 text-text outline-none focus:border-accent"
          @change="save({ budgetTokens: Number(($event.target as HTMLSelectElement).value) })"
        >
          <option
            v-for="budget in DREAM_BUDGETS"
            :key="budget"
            :value="budget"
          >
            Up to {{ formatTokens(budget) }} tokens a night
          </option>
        </select>
        <button
          v-if="nightly.automationId"
          type="button"
          class="text-xs text-accent hover:underline"
          data-testid="dream-automation"
          @click="openAutomation"
        >
          Runs as the built-in automation Dreaming
        </button>
        <span
          v-if="nightly.paused"
          class="text-xs text-amber-300"
          data-testid="dream-paused"
        >
          Paused in Automations
        </span>
      </div>
      <p
        v-if="error"
        class="mt-2 text-xs text-red-300"
        role="alert"
      >
        {{ error }}
      </p>
    </div>
    <div class="flex items-center gap-2">
      <LoaderCircle
        v-if="isSaving"
        :size="16"
        class="animate-spin text-muted"
        aria-hidden="true"
      />
      <button
        type="button"
        role="switch"
        :aria-checked="nightly.enabled"
        :disabled="isSaving"
        aria-label="Dream each night"
        data-testid="dream-nightly-switch"
        class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
        :class="nightly.enabled ? 'bg-accent' : 'bg-border'"
        @click="toggle"
      >
        <span
          class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
          :class="nightly.enabled ? 'translate-x-5' : 'translate-x-0'"
        />
      </button>
    </div>
  </div>
</template>
