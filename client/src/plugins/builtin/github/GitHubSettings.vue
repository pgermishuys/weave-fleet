<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { AlertCircle, CheckCircle2, ChevronDown, Copy, ExternalLink, Github, LoaderCircle, PlugZap, Unplug } from "lucide-vue-next";
import { formatRelativeTime } from "@/lib/format-utils";
import { useGitHubAuth } from "./composables/use-github-auth";
import { useGitHubRepos } from "./composables/use-github-repos";

const buttonPrimaryClass = "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-2 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-2 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const {
  deviceState,
  isConnected,
  isLoadingStatus,
  isAwaitingAuthorization,
  personalAccessToken,
  patOpen,
  patState,
  connectWithDeviceFlow,
  resetDeviceFlow,
  copyUserCode,
  testPersonalAccessToken,
  connectWithPersonalAccessToken,
  disconnectGitHub,
} = useGitHubAuth();
const {
  repos,
  isLoading: isLoadingRepos,
  error: reposError,
  lastUpdated,
  refresh: refreshRepos,
  clear: clearRepos,
} = useGitHubRepos({ autoLoad: false });

const isDisconnecting = shallowRef(false);
const isConnectingWithPat = shallowRef(false);

watch(
  isConnected,
  (connected) => {
    if (connected) {
      void refreshRepos();
      return;
    }

    clearRepos();
  },
  { immediate: true },
);

const pollStatusLabel = computed(() => {
  if (deviceState.value.status !== "awaiting-auth") {
    return null;
  }

  const secondsRemaining = Math.max(0, Math.ceil((deviceState.value.expiresAt - Date.now()) / 1000));
  const minutes = Math.floor(secondsRemaining / 60);
  const seconds = secondsRemaining % 60;
  const formattedTime = `${minutes}:${seconds.toString().padStart(2, "0")}`;

  return `Polling GitHub every ${deviceState.value.interval}s. Code expires in ${formattedTime}.`;
});

const patMessage = computed(() => {
  switch (patState.value.status) {
    case "error":
      return patState.value.message;
    case "success":
      return patState.value.username ? `Connected as @${patState.value.username}` : "GitHub connected successfully.";
    default:
      return null;
  }
});

const isPatMessageError = computed(() => patState.value.status === "error");
const repoCacheSummary = computed(() => {
  if (isLoadingRepos.value && repos.value.length === 0) {
    return "Loading repositories…";
  }

  if (reposError.value && repos.value.length === 0) {
    return reposError.value;
  }

  if (repos.value.length === 0) {
    return "No repositories cached yet.";
  }

  if (lastUpdated.value !== null) {
    return `${repos.value.length} repos loaded · Updated ${formatRelativeTime(lastUpdated.value)}`;
  }

  return `${repos.value.length} repos loaded`;
});

async function handleDisconnectGitHub(): Promise<void> {
  isDisconnecting.value = true;

  try {
    await disconnectGitHub();
  } finally {
    isDisconnecting.value = false;
  }
}

async function handleConnectWithPersonalAccessToken(): Promise<void> {
  isConnectingWithPat.value = true;

  try {
    await connectWithPersonalAccessToken();
  } finally {
    isConnectingWithPat.value = false;
  }
}

async function handleRefreshRepos(): Promise<void> {
  await refreshRepos(true);
}
</script>

<template>
  <div class="space-y-4">
    <div v-if="isLoadingStatus" class="flex items-center gap-2 text-sm text-muted">
      <LoaderCircle :size="16" class="animate-spin" aria-hidden="true" />
      <span>Checking GitHub connection…</span>
    </div>

    <div v-else-if="isConnected" class="space-y-3 rounded-card border border-green-500/30 bg-green-500/10 p-4">
      <div class="flex items-start gap-3">
        <CheckCircle2 :size="18" class="mt-0.5 text-green-300" aria-hidden="true" />
        <div>
          <p class="text-sm font-semibold text-text">GitHub is connected</p>
          <p class="mt-1 text-xs text-muted">
            Repositories and issue context are available to plugin surfaces that depend on GitHub.
          </p>
        </div>
      </div>

      <div class="rounded-card border border-border/70 bg-main-bg/70 p-3">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <div class="space-y-1">
            <p class="text-sm font-medium text-text">Repository cache</p>
            <p class="text-xs text-muted">
              {{ repoCacheSummary }}
            </p>
            <p v-if="reposError && repos.length > 0" class="text-xs text-red-200">
              {{ reposError }}
            </p>
          </div>

          <button
            type="button"
            :class="buttonSecondaryClass"
            :disabled="isLoadingRepos || isDisconnecting"
            @click="handleRefreshRepos"
          >
            <LoaderCircle v-if="isLoadingRepos" :size="16" class="animate-spin" aria-hidden="true" />
            <Github v-else :size="16" aria-hidden="true" />
            <span>{{ isLoadingRepos ? "Refreshing…" : "Refresh repos" }}</span>
          </button>
        </div>
      </div>

      <button
        type="button"
        :class="buttonSecondaryClass"
        :disabled="isDisconnecting"
        @click="handleDisconnectGitHub"
      >
        <LoaderCircle v-if="isDisconnecting" :size="16" class="animate-spin" aria-hidden="true" />
        <Unplug v-else :size="16" aria-hidden="true" />
        <span>{{ isDisconnecting ? "Disconnecting…" : "Disconnect" }}</span>
      </button>
    </div>

    <div v-else class="space-y-4">
      <div class="rounded-card border border-border bg-card-bg p-4">
        <div class="flex items-start gap-3">
          <Github :size="18" class="mt-0.5 text-text" aria-hidden="true" />
          <div>
            <p class="text-sm font-semibold text-text">Connect with device flow</p>
            <p class="mt-1 text-xs text-muted">
              The recommended option. Authorize in your browser and Weave finishes the connection automatically.
            </p>
          </div>
        </div>

        <div class="mt-4 space-y-3">
          <button
            v-if="deviceState.status === 'idle'"
            type="button"
            :class="buttonPrimaryClass"
            @click="connectWithDeviceFlow"
          >
            <PlugZap :size="16" aria-hidden="true" />
            Connect with GitHub
          </button>

          <button
            v-else-if="deviceState.status === 'initiating'"
            type="button"
            :class="buttonPrimaryClass"
            disabled
          >
            <LoaderCircle :size="16" class="animate-spin" aria-hidden="true" />
            Starting authorization…
          </button>

          <div v-else-if="isAwaitingAuthorization && deviceState.status === 'awaiting-auth'" class="space-y-3 rounded-card border border-border bg-main-bg p-4">
            <div class="space-y-2">
              <p class="text-xs font-medium uppercase tracking-wide text-muted">Device code</p>
              <div class="flex flex-wrap items-center gap-2">
                <code class="rounded-btn border border-border bg-card-bg px-3 py-2 text-sm font-semibold tracking-[0.2em] text-text">
                  {{ deviceState.userCode }}
                </code>

                <button type="button" :class="buttonSecondaryClass" @click="copyUserCode(deviceState.userCode)">
                  <Copy :size="16" aria-hidden="true" />
                  Copy code
                </button>
              </div>
            </div>

            <a
              :href="deviceState.verificationUri"
              target="_blank"
              rel="noreferrer noopener"
              class="inline-flex items-center gap-2 text-sm font-medium text-text underline-offset-4 hover:underline"
            >
              <ExternalLink :size="16" aria-hidden="true" />
              Open GitHub
            </a>

            <div class="flex items-center gap-2 text-sm text-muted">
              <LoaderCircle :size="16" class="animate-spin" aria-hidden="true" />
              <span>{{ pollStatusLabel }}</span>
            </div>

            <button type="button" :class="buttonSecondaryClass" @click="resetDeviceFlow">
              Cancel
            </button>
          </div>

          <div
            v-else-if="deviceState.status === 'complete'"
            class="flex items-start gap-2 rounded-card border border-green-500/30 bg-green-500/10 px-3 py-2 text-sm text-green-300"
          >
            <CheckCircle2 :size="16" class="mt-0.5 shrink-0" aria-hidden="true" />
            <span>GitHub connected successfully.</span>
          </div>

          <div
            v-else-if="deviceState.status === 'expired' || deviceState.status === 'denied' || deviceState.status === 'error'"
            class="space-y-3"
          >
            <div class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200">
              <AlertCircle :size="16" class="mt-0.5 shrink-0" aria-hidden="true" />
              <span>
                {{
                  deviceState.status === 'expired'
                    ? 'The GitHub code expired. Please try again.'
                    : deviceState.status === 'denied'
                      ? 'GitHub authorization was denied.'
                      : deviceState.message
                }}
              </span>
            </div>

            <button type="button" :class="buttonSecondaryClass" @click="resetDeviceFlow">
              Try again
            </button>
          </div>
        </div>
      </div>

      <div class="rounded-card border border-border bg-card-bg p-4">
        <button
          type="button"
          class="flex w-full items-center justify-between gap-3 text-left text-sm font-semibold text-text"
          @click="patOpen = !patOpen"
        >
          <span>Advanced: personal access token</span>
          <ChevronDown
            :size="16"
            class="text-muted transition-transform"
            :class="{ 'rotate-180': patOpen }"
            aria-hidden="true"
          />
        </button>

        <div v-if="patOpen" class="mt-4 space-y-3">
          <p class="text-xs text-muted">
            Use a PAT when device flow is unavailable. The token is validated before it is stored server-side.
          </p>

          <input
            v-model="personalAccessToken"
            type="password"
            :class="inputClass"
            placeholder="ghp_xxxxxxxxxxxx"
            :disabled="isConnectingWithPat"
          >

          <div
            v-if="patMessage"
            class="flex items-start gap-2 rounded-card px-3 py-2 text-sm"
            :class="isPatMessageError
              ? 'border border-red-500/30 bg-red-500/10 text-red-200'
              : 'border border-green-500/30 bg-green-500/10 text-green-300'"
          >
            <AlertCircle v-if="isPatMessageError" :size="16" class="mt-0.5 shrink-0" aria-hidden="true" />
            <CheckCircle2 v-else :size="16" class="mt-0.5 shrink-0" aria-hidden="true" />
            <span>{{ patMessage }}</span>
          </div>

          <div class="flex flex-wrap gap-2">
            <button
              type="button"
              :class="buttonSecondaryClass"
              :disabled="!personalAccessToken.trim() || patState.status === 'testing' || isConnectingWithPat"
              @click="testPersonalAccessToken"
            >
              <LoaderCircle
                v-if="patState.status === 'testing'"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
              <span>{{ patState.status === 'testing' ? 'Testing…' : 'Test connection' }}</span>
            </button>

            <button
              type="button"
              :class="buttonPrimaryClass"
              :disabled="!personalAccessToken.trim() || isConnectingWithPat"
              @click="handleConnectWithPersonalAccessToken"
            >
              <LoaderCircle
                v-if="isConnectingWithPat"
                :size="16"
                class="animate-spin"
                aria-hidden="true"
              />
              <span>{{ isConnectingWithPat ? 'Connecting…' : 'Connect' }}</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
