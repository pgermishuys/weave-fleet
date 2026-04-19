<script setup lang="ts">
import { computed, onMounted, reactive, ref, shallowRef } from "vue";
import { AlertCircle, Check, KeyRound, LoaderCircle, Pencil, Plus, Trash2, X } from "lucide-vue-next";
import { apiFetch } from "@/lib/api-client";
import type { CredentialSummary, StoreCredentialRequest } from "@/lib/api-types";

interface ProviderPreset {
  label: string;
  namespace: string;
  kind: string;
}

interface UpdateCredentialRequest {
  value: string;
  metadata?: Record<string, string>;
}

const commonProviders: readonly ProviderPreset[] = [
  { label: "Anthropic", namespace: "anthropic", kind: "api-key" },
  { label: "OpenAI", namespace: "openai", kind: "api-key" },
] as const;

const buttonPrimaryClass = "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-2 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-2 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const buttonDangerClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm font-medium text-red-300 transition-colors hover:bg-red-500/20 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const credentials = ref<CredentialSummary[]>([]);
const isLoading = shallowRef(true);
const loadError = shallowRef<string | null>(null);
const showAddForm = shallowRef(false);
const isSaving = shallowRef(false);
const formError = shallowRef<string | null>(null);
const editingId = shallowRef<string | null>(null);
const editingValue = shallowRef("");
const updatingId = shallowRef<string | null>(null);
const deletingId = shallowRef<string | null>(null);
const actionError = shallowRef<string | null>(null);

const form = reactive<StoreCredentialRequest>({
  label: "",
  namespace: "",
  kind: "api-key",
  value: "",
});

const hasCredentials = computed(() => credentials.value.length > 0);

onMounted(() => {
  void loadCredentials();
});

function resetForm(): void {
  form.label = "";
  form.namespace = "";
  form.kind = "api-key";
  form.value = "";
  formError.value = null;
}

function applyPreset(preset: ProviderPreset): void {
  form.namespace = preset.namespace;
  form.kind = preset.kind;

  if (!form.label.trim()) {
    form.label = `My ${preset.label} API Key`;
  }
}

function openAddForm(): void {
  showAddForm.value = true;
  formError.value = null;
}

function closeAddForm(): void {
  showAddForm.value = false;
  resetForm();
}

function beginEdit(id: string): void {
  editingId.value = id;
  editingValue.value = "";
  actionError.value = null;
}

function cancelEdit(): void {
  editingId.value = null;
  editingValue.value = "";
  actionError.value = null;
}

function getHintText(credential: CredentialSummary): string {
  return credential.displayHint.trim() ? `•••• ${credential.displayHint}` : "Stored securely";
}

async function loadCredentials(): Promise<void> {
  isLoading.value = true;
  loadError.value = null;

  try {
    const response = await apiFetch("/api/credentials");
    if (!response.ok) {
      const payload = await response.json().catch(() => ({})) as { error?: string };
      throw new Error(payload.error ?? `HTTP ${response.status}`);
    }

    credentials.value = await response.json() as CredentialSummary[];
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : "Failed to load API keys.";
  } finally {
    isLoading.value = false;
  }
}

async function storeCredential(): Promise<void> {
  if (!form.label.trim() || !form.namespace.trim() || !form.kind.trim() || !form.value.trim()) {
    formError.value = "All fields are required.";
    return;
  }

  isSaving.value = true;
  formError.value = null;

  try {
    const response = await apiFetch("/api/credentials", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        label: form.label.trim(),
        namespace: form.namespace.trim(),
        kind: form.kind.trim(),
        value: form.value.trim(),
      } satisfies StoreCredentialRequest),
    });

    if (!response.ok) {
      const payload = await response.json().catch(() => ({})) as { error?: string };
      throw new Error(payload.error ?? `HTTP ${response.status}`);
    }

    await loadCredentials();
    closeAddForm();
  } catch (error) {
    formError.value = error instanceof Error ? error.message : "Failed to save API key.";
  } finally {
    isSaving.value = false;
  }
}

async function updateCredential(id: string): Promise<void> {
  if (!editingValue.value.trim()) {
    actionError.value = "API key value is required.";
    return;
  }

  updatingId.value = id;
  actionError.value = null;

  try {
    const response = await apiFetch(`/api/credentials/${encodeURIComponent(id)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ value: editingValue.value.trim() } satisfies UpdateCredentialRequest),
    });

    if (!response.ok) {
      const payload = await response.json().catch(() => ({})) as { error?: string };
      throw new Error(payload.error ?? `HTTP ${response.status}`);
    }

    await loadCredentials();
    cancelEdit();
  } catch (error) {
    actionError.value = error instanceof Error ? error.message : "Failed to update API key.";
  } finally {
    updatingId.value = null;
  }
}

async function removeCredential(id: string): Promise<void> {
  deletingId.value = id;
  actionError.value = null;

  try {
    const response = await apiFetch(`/api/credentials/${encodeURIComponent(id)}`, {
      method: "DELETE",
    });

    if (!response.ok) {
      const payload = await response.json().catch(() => ({})) as { error?: string };
      throw new Error(payload.error ?? `HTTP ${response.status}`);
    }

    if (editingId.value === id) {
      cancelEdit();
    }

    await loadCredentials();
  } catch (error) {
    actionError.value = error instanceof Error ? error.message : "Failed to remove API key.";
  } finally {
    deletingId.value = null;
  }
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
      <div class="space-y-1">
        <h2 class="text-lg font-semibold text-text">Credentials</h2>
        <p class="text-sm text-muted">
          Store encrypted provider credentials for sessions that require authenticated access.
        </p>
      </div>

      <button
        v-if="!showAddForm"
        type="button"
        :class="buttonPrimaryClass"
        @click="openAddForm"
      >
        <Plus :size="16" aria-hidden="true" />
        Add API Key
      </button>
    </div>

    <div v-if="showAddForm" class="mt-5 rounded-card border border-border bg-main-bg p-4">
      <div class="space-y-4">
        <div>
          <h3 class="text-sm font-semibold text-text">Add API key</h3>
          <p class="mt-1 text-xs text-muted">
            The secret value is never returned after it is saved.
          </p>
        </div>

        <div class="space-y-2">
          <p class="text-xs font-medium uppercase tracking-wide text-muted">Quick select</p>
          <div class="flex flex-wrap gap-2">
            <button
              v-for="preset in commonProviders"
              :key="preset.namespace"
              type="button"
              class="rounded-full border border-border px-3 py-1 text-xs text-text transition-colors hover:border-accent/50"
              @click="applyPreset(preset)"
            >
              {{ preset.label }}
            </button>
          </div>
        </div>

        <form class="grid gap-3" @submit.prevent="storeCredential">
          <label class="grid gap-1 text-sm text-text">
            <span class="text-xs font-medium uppercase tracking-wide text-muted">Label</span>
            <input
              v-model="form.label"
              type="text"
              :class="inputClass"
              placeholder="My Anthropic API Key"
              :disabled="isSaving"
            >
          </label>

          <div class="grid gap-3 md:grid-cols-2">
            <label class="grid gap-1 text-sm text-text">
              <span class="text-xs font-medium uppercase tracking-wide text-muted">Provider</span>
              <input
                v-model="form.namespace"
                type="text"
                :class="inputClass"
                placeholder="anthropic"
                :disabled="isSaving"
              >
            </label>

            <label class="grid gap-1 text-sm text-text">
              <span class="text-xs font-medium uppercase tracking-wide text-muted">Type</span>
              <input
                v-model="form.kind"
                type="text"
                :class="inputClass"
                placeholder="api-key"
                :disabled="isSaving"
              >
            </label>
          </div>

          <label class="grid gap-1 text-sm text-text">
            <span class="text-xs font-medium uppercase tracking-wide text-muted">Secret value</span>
            <input
              v-model="form.value"
              type="password"
              :class="inputClass"
              placeholder="Paste your API key"
              :disabled="isSaving"
            >
          </label>

          <div
            v-if="formError"
            class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
            role="alert"
          >
            <AlertCircle :size="16" class="mt-0.5 shrink-0" aria-hidden="true" />
            <span>{{ formError }}</span>
          </div>

          <div class="flex flex-wrap gap-2">
            <button type="submit" :class="buttonPrimaryClass" :disabled="isSaving">
              <LoaderCircle v-if="isSaving" :size="16" class="animate-spin" aria-hidden="true" />
              <span>{{ isSaving ? "Saving…" : "Save API Key" }}</span>
            </button>

            <button type="button" :class="buttonSecondaryClass" :disabled="isSaving" @click="closeAddForm">
              Cancel
            </button>
          </div>
        </form>
      </div>
    </div>

    <div v-if="isLoading" class="mt-5 flex items-center gap-2 text-sm text-muted">
      <LoaderCircle :size="16" class="animate-spin" aria-hidden="true" />
      <span>Loading credentials…</span>
    </div>

    <div
      v-else-if="loadError"
      class="mt-5 flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle :size="16" class="mt-0.5 shrink-0" aria-hidden="true" />
      <span>{{ loadError }}</span>
    </div>

    <div v-else-if="!hasCredentials && !showAddForm" class="mt-5 rounded-card border border-dashed border-border p-6 text-center">
      <KeyRound :size="28" class="mx-auto text-muted" aria-hidden="true" />
      <p class="mt-3 text-sm font-medium text-text">No API keys stored</p>
      <p class="mt-1 text-xs text-muted">
        Add a credential to unlock provider-backed sessions and integrations.
      </p>
    </div>

    <div v-else-if="hasCredentials" class="mt-5 grid gap-3 xl:grid-cols-2">
      <article
        v-for="credential in credentials"
        :key="credential.id"
        class="rounded-card border border-border bg-main-bg p-4"
      >
        <div class="flex items-start justify-between gap-3">
          <div class="min-w-0">
            <div class="flex items-center gap-2">
              <KeyRound :size="16" class="shrink-0 text-muted" aria-hidden="true" />
              <h3 class="truncate text-sm font-semibold text-text">{{ credential.label }}</h3>
            </div>
            <p class="mt-1 truncate text-xs text-muted">
              {{ credential.namespace }} · {{ credential.kind }}
            </p>
          </div>

          <span class="rounded-full border border-green-500/30 bg-green-500/10 px-2 py-1 text-[11px] font-medium text-green-300">
            Stored
          </span>
        </div>

        <p class="mt-4 font-mono text-xs text-muted">{{ getHintText(credential) }}</p>

        <div v-if="editingId === credential.id" class="mt-4 space-y-3">
          <label class="grid gap-1 text-sm text-text">
            <span class="text-xs font-medium uppercase tracking-wide text-muted">New value</span>
            <input
              v-model="editingValue"
              type="password"
              :class="inputClass"
              placeholder="Enter updated API key value"
              :disabled="updatingId === credential.id"
            >
          </label>

          <div
            v-if="actionError"
            class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
            role="alert"
          >
            <AlertCircle :size="16" class="mt-0.5 shrink-0" aria-hidden="true" />
            <span>{{ actionError }}</span>
          </div>

          <div class="flex flex-wrap gap-2">
            <button
              type="button"
              :class="buttonPrimaryClass"
              :disabled="updatingId === credential.id"
              @click="updateCredential(credential.id)"
            >
              <LoaderCircle
                v-if="updatingId === credential.id"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
              <Check v-else :size="16" aria-hidden="true" />
              <span>{{ updatingId === credential.id ? "Saving…" : "Save" }}</span>
            </button>

            <button type="button" :class="buttonSecondaryClass" @click="cancelEdit">
              <X :size="16" aria-hidden="true" />
              Cancel
            </button>
          </div>
        </div>

        <div v-else class="mt-4 flex flex-wrap gap-2">
          <button
            type="button"
            :class="buttonSecondaryClass"
            :disabled="deletingId === credential.id"
            @click="beginEdit(credential.id)"
          >
            <Pencil :size="16" aria-hidden="true" />
            Update key
          </button>

          <button
            type="button"
            :class="buttonDangerClass"
            :disabled="deletingId === credential.id"
            @click="removeCredential(credential.id)"
          >
            <LoaderCircle
              v-if="deletingId === credential.id"
              :size="16"
              class="animate-spin"
              aria-hidden="true"
            />
            <Trash2 v-else :size="16" aria-hidden="true" />
            <span>{{ deletingId === credential.id ? "Removing…" : "Remove" }}</span>
          </button>
        </div>
      </article>
    </div>

    <div
      v-if="actionError && editingId === null"
      class="mt-4 flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle :size="16" class="mt-0.5 shrink-0" aria-hidden="true" />
      <span>{{ actionError }}</span>
    </div>
  </section>
</template>
