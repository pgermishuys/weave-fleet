<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import {
  AlertCircle,
  ArrowRight,
  Check,
  CheckCircle2,
  KeyRound,
  LoaderCircle,
  Rocket,
  Sparkles,
} from "lucide-vue-next";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { apiFetch } from "@/lib/api-client";
import type { StoreCredentialRequest } from "@/lib/api-types";

type WizardStep = "welcome" | "credentials" | "ready";

interface ProviderPreset {
  label: string;
  namespace: string;
  kind: string;
  placeholder: string;
}

interface Props {
  credentialsOptional: boolean;
}

interface Emits {
  complete: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const router = useRouter();

const wizardSteps: readonly WizardStep[] = ["welcome", "credentials", "ready"] as const;
const providerPresets: readonly ProviderPreset[] = [
  { label: "Anthropic", namespace: "anthropic", kind: "api-key", placeholder: "sk-ant-..." },
  { label: "OpenAI", namespace: "openai", kind: "api-key", placeholder: "sk-proj-..." },
] as const;

const buttonPrimaryClass = "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-4 py-2.5 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-4 py-2.5 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2.5 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const step = shallowRef<WizardStep>("welcome");
const selectedNamespace = shallowRef(providerPresets[0].namespace);
const credentialValue = shallowRef("");
const isSaving = shallowRef(false);
const isFinishing = shallowRef(false);
const isCredentialSaved = shallowRef(false);
const errorMessage = shallowRef<string | null>(null);

const selectedProvider = computed<ProviderPreset>(() => {
  return providerPresets.find((provider) => provider.namespace === selectedNamespace.value) ?? providerPresets[0];
});

const currentStepIndex = computed(() => wizardSteps.indexOf(step.value));

function handleNextFromWelcome(): void {
  step.value = "credentials";
}

function selectProvider(namespace: string): void {
  selectedNamespace.value = namespace;
  credentialValue.value = "";
  isCredentialSaved.value = false;
  errorMessage.value = null;
}

async function saveCredential(): Promise<void> {
  if (!credentialValue.value.trim()) {
    errorMessage.value = "Enter an API key value.";
    return;
  }

  isSaving.value = true;
  errorMessage.value = null;

  try {
    const requestBody: StoreCredentialRequest = {
      label: `My ${selectedProvider.value.label} API Key`,
      namespace: selectedProvider.value.namespace,
      kind: selectedProvider.value.kind,
      value: credentialValue.value.trim(),
    };

    const response = await apiFetch("/api/credentials", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(requestBody),
    });

    if (!response.ok) {
      const payload = await response.json().catch(() => ({})) as { error?: string };
      throw new Error(payload.error ?? `HTTP ${response.status}`);
    }

    isCredentialSaved.value = true;
    credentialValue.value = "";
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : "Failed to save API key.";
  } finally {
    isSaving.value = false;
  }
}

function handleCredentialNext(): void {
  errorMessage.value = null;
  step.value = "ready";
}

async function finishOnboarding(): Promise<void> {
  isFinishing.value = true;

  try {
    await apiFetch("/api/user/me/complete-onboarding", { method: "POST" });
  } catch {
    // Best-effort only — keep the user moving.
  } finally {
    isFinishing.value = false;
  }

  emit("complete");
  await router.navigate({ to: "/" });
}
</script>

<template>
  <Dialog
    :open="true"
    modal
  >
    <DialogContent
      class="w-full max-w-[calc(100%-2rem)] border-border bg-card-bg p-0 shadow-2xl sm:max-w-md"
      :show-close-button="false"
      @interact-outside="(event) => event.preventDefault()"
      @escape-key-down="(event) => event.preventDefault()"
    >
      <DialogHeader class="sr-only">
        <DialogTitle>Weave onboarding</DialogTitle>
        <DialogDescription>Complete a short setup flow to finish your account onboarding.</DialogDescription>
      </DialogHeader>

      <div class="flex min-h-[320px] flex-col justify-between gap-6 p-6">
        <section
          v-if="step === 'welcome'"
          class="flex flex-1 flex-col items-center justify-center gap-6 py-4 text-center"
        >
          <div class="flex h-16 w-16 items-center justify-center rounded-full bg-accent/10 text-accent">
            <Sparkles
              :size="32"
              aria-hidden="true"
            />
          </div>

          <div class="space-y-2">
            <h2 class="text-xl font-semibold text-text">
              Welcome to Weave
            </h2>
            <p class="mx-auto max-w-sm text-sm leading-6 text-muted">
              Weave connects AI coding tools to your projects. Let’s get you set up in just a few steps.
            </p>
          </div>

          <button
            type="button"
            :class="`${buttonPrimaryClass} w-full max-w-xs`"
            @click="handleNextFromWelcome"
          >
            Get Started
          </button>
        </section>

        <section
          v-else-if="step === 'credentials'"
          class="flex flex-1 flex-col gap-5 py-2"
        >
          <div class="space-y-1.5">
            <h2 class="text-lg font-semibold text-text">
              Connect your API keys
            </h2>
            <p class="text-sm leading-6 text-muted">
              Add API keys to use AI providers in your sessions. You can add or change these later in Settings.
            </p>
            <p
              v-if="props.credentialsOptional"
              class="text-xs italic text-muted"
            >
              Built-in access is available for the default tool, so you can skip this step for now.
            </p>
          </div>

          <template v-if="!isCredentialSaved">
            <div class="flex flex-wrap gap-2">
              <button
                v-for="provider in providerPresets"
                :key="provider.namespace"
                type="button"
                class="rounded-full border px-3 py-1 text-xs font-medium transition-colors"
                :class="provider.namespace === selectedNamespace
                  ? 'border-accent bg-accent/10 text-accent'
                  : 'border-border text-text hover:border-accent/50'"
                @click="selectProvider(provider.namespace)"
              >
                {{ provider.label }}
              </button>
            </div>

            <label class="grid gap-2 text-sm text-text">
              <span class="text-xs font-medium uppercase tracking-wide text-muted">
                {{ selectedProvider.label }} API Key
              </span>
              <input
                id="onboard-api-key"
                v-model="credentialValue"
                type="password"
                :class="inputClass"
                :placeholder="selectedProvider.placeholder"
                :disabled="isSaving"
              >
            </label>

            <div
              v-if="errorMessage"
              class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300"
              role="alert"
            >
              <AlertCircle
                :size="16"
                class="mt-0.5 shrink-0"
                aria-hidden="true"
              />
              <span>{{ errorMessage }}</span>
            </div>

            <div class="flex flex-wrap gap-2">
              <button
                type="button"
                :class="buttonPrimaryClass"
                :disabled="isSaving"
                @click="void saveCredential()"
              >
                <LoaderCircle
                  v-if="isSaving"
                  :size="16"
                  class="animate-spin"
                  aria-hidden="true"
                />
                <KeyRound
                  v-else
                  :size="16"
                  aria-hidden="true"
                />
                Save API Key
              </button>

              <button
                v-if="props.credentialsOptional"
                type="button"
                :class="buttonSecondaryClass"
                @click="handleCredentialNext"
              >
                Skip for now
              </button>
            </div>
          </template>

          <template v-else>
            <div class="flex items-center gap-2 rounded-card border border-green-500/30 bg-green-500/10 px-3 py-2 text-sm text-green-300">
              <Check
                :size="16"
                aria-hidden="true"
              />
              <span>API key saved.</span>
            </div>

            <button
              type="button"
              :class="`${buttonPrimaryClass} w-full`"
              @click="handleCredentialNext"
            >
              Continue
              <ArrowRight
                :size="16"
                aria-hidden="true"
              />
            </button>
          </template>
        </section>

        <section
          v-else
          class="flex flex-1 flex-col items-center justify-center gap-6 py-4 text-center"
        >
          <div class="flex h-16 w-16 items-center justify-center rounded-full bg-green-500/10 text-green-400">
            <CheckCircle2
              :size="32"
              aria-hidden="true"
            />
          </div>

          <div class="space-y-2">
            <h2 class="text-xl font-semibold text-text">
              You're all set!
            </h2>
            <p class="mx-auto max-w-sm text-sm leading-6 text-muted">
              Start your first session and let Weave handle the rest. You can manage your API keys anytime in Settings.
            </p>
          </div>

          <button
            type="button"
            :class="`${buttonPrimaryClass} w-full max-w-xs`"
            :disabled="isFinishing"
            @click="void finishOnboarding()"
          >
            <LoaderCircle
              v-if="isFinishing"
              :size="16"
              class="animate-spin"
              aria-hidden="true"
            />
            <Rocket
              v-else
              :size="16"
              aria-hidden="true"
            />
            Start a Session
          </button>
        </section>

        <div class="flex justify-center gap-1.5 pt-2">
          <span
            v-for="(wizardStep, index) in wizardSteps"
            :key="wizardStep"
            class="h-1.5 w-6 rounded-full transition-colors"
            :class="index === currentStepIndex ? 'bg-accent' : 'bg-border'"
          />
        </div>
      </div>
    </DialogContent>
  </Dialog>
</template>
