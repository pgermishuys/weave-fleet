<script setup lang="ts">
import { computed } from "vue";
import { AlertCircle, CheckCircle2, Download, LoaderCircle, RefreshCw } from "lucide-vue-next";
import { useDesktopUpdates } from "@/composables/use-desktop-updates";

const { state, isChecking, check, install } = useDesktopUpdates();

const statusLabel = computed(() => {
  const s = state.value;
  switch (s?.status) {
    case "idle":
      return "Up to date";
    case "checking":
      return "Checking for updates…";
    case "downloading":
      return `Downloading v${s.version}…${s.percent != null ? ` ${s.percent}%` : ""}`;
    case "ready":
      return `v${s.version} is ready`;
    case "available":
      return `v${s.version} is available`;
    case "error":
      return "Update check failed";
    case "off":
      return "This build doesn't update itself";
    default:
      return "Loading…";
  }
});

const isBusy = computed(() => isChecking.value || state.value?.status === "checking" || state.value?.status === "downloading");
const canCheck = computed(() => state.value?.status === "idle" || state.value?.status === "error");
const actionLabel = computed(() => {
  if (state.value?.status === "ready") return "Restart to update";
  if (state.value?.status === "available") return "Download";
  return null;
});
</script>

<template>
  <div
    class="flex flex-col gap-2 rounded-card border border-border bg-main-bg px-4 py-3"
    data-testid="desktop-app-updates"
  >
    <div class="flex items-center justify-between gap-3">
      <div>
        <p class="text-sm font-medium text-text">
          Fleet app
        </p>
        <p
          v-if="state"
          class="mt-0.5 font-mono text-xs text-muted"
        >
          v{{ state.currentVersion }}
        </p>
      </div>

      <div class="flex items-center gap-2">
        <button
          v-if="actionLabel"
          type="button"
          class="rounded-btn border border-accent bg-accent px-3 py-1.5 text-xs font-medium text-white transition-colors hover:opacity-90"
          @click="install"
        >
          {{ actionLabel }}
        </button>
        <button
          v-if="canCheck"
          type="button"
          :disabled="isBusy"
          class="flex items-center gap-1.5 rounded-btn border border-border bg-card-bg px-3 py-1.5 text-xs font-medium text-text transition-colors hover:bg-main-bg disabled:cursor-not-allowed disabled:opacity-50"
          @click="check"
        >
          <RefreshCw
            :size="12"
            :class="{ 'animate-spin': isBusy }"
            aria-hidden="true"
          />
          Check for updates
        </button>
      </div>
    </div>

    <div class="flex items-center gap-2">
      <LoaderCircle
        v-if="isBusy"
        :size="15"
        class="animate-spin text-muted"
        aria-hidden="true"
      />
      <CheckCircle2
        v-else-if="state?.status === 'idle'"
        :size="15"
        class="text-success"
        aria-hidden="true"
      />
      <Download
        v-else-if="state?.status === 'ready' || state?.status === 'available'"
        :size="15"
        class="text-accent"
        aria-hidden="true"
      />
      <AlertCircle
        v-else-if="state?.status === 'error'"
        :size="15"
        class="text-danger"
        aria-hidden="true"
      />
      <p
        class="text-sm"
        :class="state?.status === 'error' ? 'text-danger' : state?.status === 'ready' || state?.status === 'available' ? 'text-accent' : 'text-text'"
        data-testid="desktop-app-update-status"
      >
        {{ statusLabel }}
      </p>
    </div>

    <div
      v-if="state?.status === 'downloading'"
      class="h-1.5 w-full overflow-hidden rounded-full bg-border"
    >
      <div
        class="h-full rounded-full bg-accent transition-all duration-300"
        :style="{ width: `${state.percent ?? 0}%` }"
      />
    </div>

    <p
      v-if="state?.status === 'ready'"
      class="text-xs text-muted"
    >
      Fleet restarts to install it. Working sessions stop, so it asks first if any are running.
    </p>
    <p
      v-else-if="state?.status === 'available'"
      class="text-xs text-muted"
    >
      This copy of Fleet can't install updates itself yet. Download the new version and install it over this one.
    </p>
    <p
      v-else-if="state?.status === 'error' && state.error"
      class="text-xs text-danger"
    >
      {{ state.error }}
    </p>
  </div>
</template>
