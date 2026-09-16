<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import { useBoardFeature } from "@/composables/use-board-feature";
import { SESSION_RECAP_PREFERENCE_KEY } from "@/composables/use-session-recap";
import {
  DESKTOP_NOTIFICATIONS_PREFERENCE_KEY,
  notificationPermission,
  requestNotificationPermission,
} from "@/composables/use-session-notifications";
import { usePreferencesStore } from "@/stores/preferences";

const preferencesStore = usePreferencesStore();
preferencesStore.ensureLoaded();
const { isBoardFeatureEnabled, setBoardFeatureEnabled } = useBoardFeature();

const isSavingBoardFeature = shallowRef(false);
const boardFeatureError = shallowRef<string | null>(null);

const isSessionRecapEnabled = computed(
  () => preferencesStore.get(SESSION_RECAP_PREFERENCE_KEY, "false") === "true",
);
const isSavingSessionRecap = shallowRef(false);

async function toggleSessionRecap(): Promise<void> {
  isSavingSessionRecap.value = true;
  try {
    await preferencesStore.set(SESSION_RECAP_PREFERENCE_KEY, isSessionRecapEnabled.value ? "false" : "true");
  } finally {
    isSavingSessionRecap.value = false;
  }
}

const isNotificationsEnabled = computed(
  () => preferencesStore.get(DESKTOP_NOTIFICATIONS_PREFERENCE_KEY, "false") === "true",
);
const isSavingNotifications = shallowRef(false);
const notificationsError = shallowRef<string | null>(null);

async function toggleNotifications(): Promise<void> {
  const turningOn = !isNotificationsEnabled.value;
  isSavingNotifications.value = true;
  notificationsError.value = null;

  try {
    // The browser only asks when a click asks it to, so this has to happen before the preference is saved.
    if (turningOn) {
      const permission = await requestNotificationPermission();
      if (permission === "unsupported") {
        notificationsError.value = "This browser can't show desktop notifications.";
        return;
      }
      if (permission !== "granted") {
        notificationsError.value = "Your browser is blocking notifications for Fleet. Allow them in its site settings, then try again.";
        return;
      }
    }

    await preferencesStore.set(DESKTOP_NOTIFICATIONS_PREFERENCE_KEY, turningOn ? "true" : "false");
  } finally {
    isSavingNotifications.value = false;
  }
}

async function toggleBoardFeature(): Promise<void> {
  const enabled = !isBoardFeatureEnabled.value;

  isSavingBoardFeature.value = true;
  boardFeatureError.value = null;

  try {
    await setBoardFeatureEnabled(enabled);
  } catch (error) {
    boardFeatureError.value = error instanceof Error
      ? error.message
      : "Failed to update Board feature setting.";
  } finally {
    isSavingBoardFeature.value = false;
  }
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Features
      </h2>
      <p class="text-sm text-muted">
        Enable or hide optional workspace features.
      </p>
    </div>

    <div class="mt-5 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
      <div>
        <p class="text-sm font-medium text-text">
          Board
        </p>
        <p class="mt-1 text-xs text-muted">
          Show the Board entry in the left rail and enable the board workspace panels.
        </p>
        <p
          v-if="boardFeatureError"
          class="mt-2 text-xs text-red-300"
          role="alert"
        >
          {{ boardFeatureError }}
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingBoardFeature"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isBoardFeatureEnabled"
          :disabled="preferencesStore.isLoading || isSavingBoardFeature"
          aria-label="Enable Board feature"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isBoardFeatureEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleBoardFeature"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isBoardFeatureEnabled ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>

    <div class="mt-3 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
      <div>
        <p class="text-sm font-medium text-text">
          Session recap
        </p>
        <p class="mt-1 text-xs text-muted">
          When a turn finishes while you're looking at something else, write a one-line recap above the
          composer for when you come back. Each recap is one extra request to the session's model.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingSessionRecap"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isSessionRecapEnabled"
          :disabled="preferencesStore.isLoading || isSavingSessionRecap"
          aria-label="Enable session recap"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isSessionRecapEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleSessionRecap"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isSessionRecapEnabled ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>

    <div class="mt-3 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
      <div>
        <p class="text-sm font-medium text-text">
          Desktop notifications
        </p>
        <p class="mt-1 text-xs text-muted">
          Tell me when a session needs an answer, or finishes, while I'm looking at something else. Nothing
          is sent about the session in front of you.
        </p>
        <p
          v-if="notificationsError"
          class="mt-2 text-xs text-red-300"
          role="alert"
        >
          {{ notificationsError }}
        </p>
        <p
          v-else-if="isNotificationsEnabled && notificationPermission() !== 'granted'"
          class="mt-2 text-xs text-red-300"
          role="alert"
        >
          Your browser is no longer allowing notifications for Fleet.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingNotifications"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isNotificationsEnabled"
          :disabled="preferencesStore.isLoading || isSavingNotifications"
          aria-label="Enable desktop notifications"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isNotificationsEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleNotifications"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isNotificationsEnabled ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>
  </section>
</template>
