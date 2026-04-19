<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef } from "vue";
import type { Component } from "vue";
import { AlertCircle, Download, RefreshCw } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Progress } from "@/components/ui/progress";
import {
  isTauri,
  tauriGetUpdateState,
  tauriInvoke,
  tauriListen,
  tauriSetUpdatePreferences,
} from "@/lib/tauri";
import { useUpdatePreferences } from "@/lib/update-preferences";

interface UpdateAvailablePayload {
  version: string;
  current_version: string;
}

interface UpdateProgressPayload {
  downloaded: number;
  total: number | null;
}

type UpdateDialogState = "idle" | "available" | "downloading" | "ready" | "error";

const [updatePreferences] = useUpdatePreferences();
const isTauriContext = isTauri();

const dialogState = shallowRef<UpdateDialogState>("idle");
const version = shallowRef("");
const currentVersion = shallowRef("");
const progress = shallowRef(0);
const errorMessage = shallowRef<string | null>(null);
const open = shallowRef(false);

const showDialog = computed(() => isTauriContext && dialogState.value !== "idle");
const isDownloading = computed(() => dialogState.value === "downloading");
const isReady = computed(() => dialogState.value === "ready");
const isError = computed(() => dialogState.value === "error");

const dialogTitle = computed(() => {
  if (dialogState.value === "error") {
    return "Update Failed";
  }

  if (dialogState.value === "ready") {
    return "Update Ready";
  }

  return "Update Available";
});

const dialogDescription = computed(() => {
  if (dialogState.value === "error") {
    return "Something went wrong while installing the update.";
  }

  if (dialogState.value === "ready") {
    return `Weave Fleet v${version.value} has been downloaded and will be applied on next start.`;
  }

  return `Weave Fleet v${version.value} is available (you have v${currentVersion.value}).`;
});

const statusIcon = computed<Component>(() => {
  if (dialogState.value === "error") {
    return AlertCircle;
  }

  return Download;
});

const statusIconClass = computed(() => {
  if (dialogState.value === "error") {
    return "h-5 w-5 text-destructive";
  }

  return "h-5 w-5";
});

function showUpdate(payload: UpdateAvailablePayload): void {
  version.value = payload.version;
  currentVersion.value = payload.current_version;
  dialogState.value = "available";
  open.value = true;
}

function setReadyState(payload: UpdateAvailablePayload): void {
  version.value = payload.version;
  currentVersion.value = payload.current_version;
  dialogState.value = "ready";
  open.value = true;
}

function handleUpdateAvailable(payload: UpdateAvailablePayload): void {
  if (!updatePreferences.autoUpdate) {
    showUpdate(payload);
    return;
  }

  version.value = payload.version;
  currentVersion.value = payload.current_version;
  dialogState.value = "downloading";
}

function handleOpenChange(nextOpen: boolean): void {
  if (nextOpen) {
    open.value = true;
    return;
  }

  if (dialogState.value === "downloading") {
    return;
  }

  open.value = false;
  dialogState.value = "idle";
}

async function handleInstall(): Promise<void> {
  if (!isTauriContext) {
    return;
  }

  dialogState.value = "downloading";
  progress.value = 0;
  errorMessage.value = null;

  try {
    await tauriInvoke("install_update");
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : String(error);
    dialogState.value = "error";
  }
}

async function syncUpdatePreferences(): Promise<void> {
  if (!isTauriContext) {
    return;
  }

  try {
    await tauriSetUpdatePreferences(updatePreferences.autoUpdate, updatePreferences.channel);
  } catch {
    // Command unavailable or not ready; ignore and keep client preference.
  }
}

async function hydrateUpdateState(): Promise<void> {
  if (!isTauriContext) {
    return;
  }

  try {
    const updateState = await tauriGetUpdateState();

    if (updateState.update_available) {
      version.value = updateState.update_available.version;
      currentVersion.value = updateState.update_available.current_version;
      dialogState.value = updateState.download_in_progress ? "downloading" : "available";

      if (!updatePreferences.autoUpdate) {
        open.value = true;
      }
    }

    if (updateState.update_ready_for_restart) {
      dialogState.value = "ready";
      open.value = true;
    }
  } catch {
    // Ignore when command is unavailable.
  }
}

async function checkForPendingUpdate(): Promise<void> {
  if (!isTauriContext) {
    return;
  }

  try {
    const payload = await tauriInvoke<UpdateAvailablePayload | null>("check_for_update");

    if (payload && !updatePreferences.autoUpdate) {
      showUpdate(payload);
    }
  } catch {
    // Not in Tauri or command not available — ignore.
  }
}

const unlisteners = shallowRef<Array<() => void>>([]);

onMounted(() => {
  if (!isTauriContext) {
    return;
  }

  void syncUpdatePreferences();
  void hydrateUpdateState();
  void checkForPendingUpdate();

  void tauriListen<UpdateAvailablePayload>("update-available", (payload) => {
    handleUpdateAvailable(payload);
  }).then((unlisten) => {
    if (unlisten) {
      unlisteners.value = [...unlisteners.value, unlisten];
    }
  });

  void tauriListen<UpdateAvailablePayload>("update-ready-for-restart", (payload) => {
    setReadyState(payload);
  }).then((unlisten) => {
    if (unlisten) {
      unlisteners.value = [...unlisteners.value, unlisten];
    }
  });

  void tauriListen<UpdateProgressPayload>("update-download-progress", (payload) => {
    if (payload.total && payload.total > 0) {
      progress.value = Math.round((payload.downloaded / payload.total) * 100);
    }
  }).then((unlisten) => {
    if (unlisten) {
      unlisteners.value = [...unlisteners.value, unlisten];
    }
  });
});

onUnmounted(() => {
  for (const unlisten of unlisteners.value) {
    unlisten();
  }

  unlisteners.value = [];
});
</script>

<template>
  <Dialog
    v-if="showDialog"
    :open="open"
    @update:open="handleOpenChange"
  >
    <DialogContent
      class="sm:max-w-md"
      :show-close-button="!isDownloading"
    >
      <DialogHeader>
        <DialogTitle class="flex items-center gap-2">
          <component
            :is="statusIcon"
            :class="statusIconClass"
          />
          {{ dialogTitle }}
        </DialogTitle>
        <DialogDescription>
          {{ dialogDescription }}
        </DialogDescription>
      </DialogHeader>

      <div class="space-y-4 py-2">
        <div
          v-if="isDownloading"
          class="space-y-2"
        >
          <div class="flex items-center justify-between text-sm text-muted-foreground">
            <span class="flex items-center gap-1.5">
              <RefreshCw class="h-3.5 w-3.5 animate-spin" />
              Downloading update...
            </span>
            <span>{{ progress }}%</span>
          </div>
          <Progress :model-value="progress" />
          <p class="text-xs text-muted-foreground">
            {{ updatePreferences.autoUpdate
              ? "The update is downloading in the background."
              : "The app will restart automatically once the update is installed." }}
          </p>
        </div>

        <div
          v-if="isReady"
          class="rounded-md bg-emerald-500/10 p-3 text-sm text-emerald-700 dark:text-emerald-400"
        >
          Update downloaded. Restart the app to switch to v{{ version }}.
        </div>

        <div
          v-if="isError && errorMessage"
          class="rounded-md bg-red-500/10 p-3 text-sm text-red-600 dark:text-red-400"
        >
          {{ errorMessage }}
        </div>
      </div>

      <DialogFooter>
        <template v-if="dialogState === 'available'">
          <Button
            variant="outline"
            @click="handleOpenChange(false)"
          >
            Not Now
          </Button>
          <Button
            class="gap-1.5"
            @click="handleInstall"
          >
            <Download class="h-3.5 w-3.5" />
            Install &amp; Restart
          </Button>
        </template>

        <template v-else-if="dialogState === 'error'">
          <Button
            variant="outline"
            @click="handleOpenChange(false)"
          >
            Dismiss
          </Button>
          <Button
            class="gap-1.5"
            @click="handleInstall"
          >
            <RefreshCw class="h-3.5 w-3.5" />
            Retry
          </Button>
        </template>

        <Button
          v-else-if="dialogState === 'ready'"
          variant="outline"
          @click="handleOpenChange(false)"
        >
          Got it
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
