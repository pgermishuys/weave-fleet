<script setup lang="ts">
import { computed } from "vue";
import { AlertCircle, CheckCircle2, Download, LoaderCircle, RefreshCw } from "lucide-vue-next";
import { useUpdateStatus } from "@/composables/use-update-status";

const { updateStatus, isLoading, checkForUpdate, downloadUpdate } = useUpdateStatus();

const statusLabel = computed(() => {
  switch (updateStatus.value?.status) {
    case "uptodate":
      return "Up to date";
    case "available":
      return `Update available: v${updateStatus.value.latestVersion}`;
    case "downloading":
      return `Downloading v${updateStatus.value.latestVersion}…`;
    case "staged":
      return `v${updateStatus.value.latestVersion} ready — restart to apply`;
    case "error":
      return "Update check failed";
    default:
      return "Checking for updates…";
  }
});

const statusVariant = computed<"ok" | "info" | "warn" | "error" | "muted">(() => {
  switch (updateStatus.value?.status) {
    case "uptodate":
      return "ok";
    case "available":
    case "downloading":
      return "info";
    case "staged":
      return "warn";
    case "error":
      return "error";
    default:
      return "muted";
  }
});

const isCheckingOrDownloading = computed(
  () => updateStatus.value?.status === "downloading" || isLoading.value,
);

const canCheck = computed(
  () =>
    updateStatus.value?.status !== "downloading" &&
    updateStatus.value?.status !== "staged",
);

const canDownload = computed(() => updateStatus.value?.status === "available");
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        System
      </h2>
      <p class="text-sm text-muted">
        Version information and software updates.
      </p>
    </div>

    <div class="mt-5 grid gap-4">
      <!-- Version row -->
      <div class="flex items-center justify-between rounded-card border border-border bg-main-bg px-4 py-3">
        <div>
          <p class="text-sm font-medium text-text">
            Installed version
          </p>
          <p
            v-if="updateStatus"
            class="mt-0.5 font-mono text-xs text-muted"
          >
            v{{ updateStatus.currentVersion }}
          </p>
          <p
            v-else
            class="mt-0.5 text-xs text-muted"
          >
            Loading…
          </p>
        </div>
      </div>

      <!-- Update status row -->
      <div class="flex items-center justify-between rounded-card border border-border bg-main-bg px-4 py-3">
        <div class="flex items-center gap-2">
          <LoaderCircle
            v-if="isCheckingOrDownloading"
            :size="15"
            class="animate-spin text-muted"
            aria-hidden="true"
          />
          <CheckCircle2
            v-else-if="statusVariant === 'ok'"
            :size="15"
            class="text-success"
            aria-hidden="true"
          />
          <Download
            v-else-if="statusVariant === 'warn'"
            :size="15"
            class="text-warn"
            aria-hidden="true"
          />
          <AlertCircle
            v-else-if="statusVariant === 'error'"
            :size="15"
            class="text-danger"
            aria-hidden="true"
          />
          <Download
            v-else-if="statusVariant === 'info'"
            :size="15"
            class="text-accent"
            aria-hidden="true"
          />

          <p
            class="text-sm"
            :class="{
              'text-text': statusVariant === 'ok' || statusVariant === 'muted',
              'text-accent': statusVariant === 'info',
              'text-warn': statusVariant === 'warn',
              'text-danger': statusVariant === 'error',
            }"
          >
            {{ statusLabel }}
          </p>
        </div>

        <div class="flex items-center gap-2">
          <button
            v-if="canDownload"
            type="button"
            class="rounded-btn border border-border bg-card-bg px-3 py-1.5 text-xs font-medium text-text transition-colors hover:bg-main-bg disabled:cursor-not-allowed disabled:opacity-50"
            @click="downloadUpdate"
          >
            Download
          </button>

          <button
            v-if="canCheck"
            type="button"
            :disabled="isCheckingOrDownloading"
            class="flex items-center gap-1.5 rounded-btn border border-border bg-card-bg px-3 py-1.5 text-xs font-medium text-text transition-colors hover:bg-main-bg disabled:cursor-not-allowed disabled:opacity-50"
            @click="checkForUpdate"
          >
            <RefreshCw
              :size="12"
              :class="{ 'animate-spin': isCheckingOrDownloading }"
              aria-hidden="true"
            />
            Check for updates
          </button>
        </div>
      </div>

      <!-- Error detail -->
      <div
        v-if="updateStatus?.status === 'error' && updateStatus.error"
        class="rounded-card border border-border border-danger/30 bg-danger/5 px-4 py-3"
      >
        <p class="text-xs text-danger">
          {{ updateStatus.error }}
        </p>
      </div>

      <!-- Staged restart note -->
      <div
        v-if="updateStatus?.status === 'staged'"
        class="rounded-card border border-border bg-main-bg px-4 py-3"
      >
        <p class="text-xs text-muted">
          The update has been downloaded and is ready to install. Restart Fleet (<code class="font-mono">fleet</code>) to apply it automatically.
        </p>
      </div>
    </div>
  </section>
</template>
