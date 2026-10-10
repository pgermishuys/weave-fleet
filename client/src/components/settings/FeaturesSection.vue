<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import { useBoardFeature } from "@/composables/use-board-feature";
import { SESSION_RECAP_PREFERENCE_KEY } from "@/composables/use-session-recap";
import { RETRY_AFTER_LIMITS_PREFERENCE_KEY } from "@/composables/use-session-retry";
import { MODS_PREFERENCE_KEY } from "@/lib/mods";
import { SESSION_MESSAGES_PREFERENCE_KEY } from "@/lib/session-messages";
import { AGENT_HANDOFF_PREFERENCE_KEY, LIVE_MACHINES_PREFERENCE_KEY } from "@/lib/machines";
import {
  DESKTOP_NOTIFICATIONS_PREFERENCE_KEY,
  notificationPermission,
  requestNotificationPermission,
} from "@/composables/use-session-notifications";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

const preferencesStore = usePreferencesStore();
const machines = useMachinesStore();
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

// On unless turned off: a turn a model provider's limit stopped carries on by itself.
const isRetryAfterLimitsEnabled = computed(
  () => preferencesStore.get(RETRY_AFTER_LIMITS_PREFERENCE_KEY, "true") !== "false",
);
const isSavingRetryAfterLimits = shallowRef(false);

async function toggleRetryAfterLimits(): Promise<void> {
  isSavingRetryAfterLimits.value = true;
  try {
    await preferencesStore.set(RETRY_AFTER_LIMITS_PREFERENCE_KEY, isRetryAfterLimitsEnabled.value ? "false" : "true");
  } finally {
    isSavingRetryAfterLimits.value = false;
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

const isSessionMessagesEnabled = computed(
  () => preferencesStore.get(SESSION_MESSAGES_PREFERENCE_KEY, "false") === "true",
);
const isSavingSessionMessages = shallowRef(false);

async function toggleSessionMessages(): Promise<void> {
  isSavingSessionMessages.value = true;
  try {
    await preferencesStore.set(SESSION_MESSAGES_PREFERENCE_KEY, isSessionMessagesEnabled.value ? "false" : "true");
  } finally {
    isSavingSessionMessages.value = false;
  }
}

const isLiveMachinesEnabled = computed(
  () => preferencesStore.get(LIVE_MACHINES_PREFERENCE_KEY, "false") === "true",
);
const isSavingLiveMachines = shallowRef(false);

const isAgentHandoffEnabled = computed(
  () => preferencesStore.get(AGENT_HANDOFF_PREFERENCE_KEY, "false") === "true",
);
const isSavingAgentHandoff = shallowRef(false);

async function toggleAgentHandoff(): Promise<void> {
  isSavingAgentHandoff.value = true;
  try {
    await preferencesStore.set(AGENT_HANDOFF_PREFERENCE_KEY, isAgentHandoffEnabled.value ? "false" : "true");
  } finally {
    isSavingAgentHandoff.value = false;
  }
}

async function toggleLiveMachines(): Promise<void> {
  isSavingLiveMachines.value = true;
  try {
    await preferencesStore.set(LIVE_MACHINES_PREFERENCE_KEY, isLiveMachinesEnabled.value ? "false" : "true");
  } finally {
    isSavingLiveMachines.value = false;
  }
}

const isModsEnabled = computed(
  () => preferencesStore.get(MODS_PREFERENCE_KEY, "false") === "true",
);
const isSavingMods = shallowRef(false);

async function toggleMods(): Promise<void> {
  isSavingMods.value = true;
  try {
    await preferencesStore.set(MODS_PREFERENCE_KEY, isModsEnabled.value ? "false" : "true");
  } finally {
    isSavingMods.value = false;
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
          Try again when a limit resets
        </p>
        <p class="mt-1 text-xs text-muted">
          When a model provider's rate limit or usage limit stops a turn, send "Continue where you left off."
          when the limit resets, or after a wait when the provider doesn't say. What you queued waits for it.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingRetryAfterLimits"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isRetryAfterLimitsEnabled"
          :disabled="preferencesStore.isLoading || isSavingRetryAfterLimits"
          aria-label="Try again when a limit resets"
          data-testid="retry-after-limits-switch"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isRetryAfterLimitsEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleRetryAfterLimits"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isRetryAfterLimitsEnabled ? 'translate-x-5' : 'translate-x-0'"
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

    <div class="mt-3 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
      <div>
        <p class="flex items-center gap-2 text-sm font-medium text-text">
          Messages between sessions
          <span class="rounded-full border border-border px-2 py-px text-[0.7rem] font-medium uppercase tracking-wide text-muted">
            Experimental
          </span>
        </p>
        <p class="mt-1 text-xs text-muted">
          Let an agent send a message to another session with the <code>fleet_message</code> tool. The message
          says which session sent it, and the agent treats it as a teammate's request, not yours. Agents can't
          send prompts through Fleet's API while this is on. Applies to sessions started afterwards.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingSessionMessages"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isSessionMessagesEnabled"
          :disabled="preferencesStore.isLoading || isSavingSessionMessages"
          aria-label="Enable messages between sessions"
          data-testid="session-messages-switch"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isSessionMessagesEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleSessionMessages"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isSessionMessagesEnabled ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>

    <div
      v-if="machines.hasMachines"
      class="mt-3 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4"
    >
      <div>
        <p class="flex items-center gap-2 text-sm font-medium text-text">
          Keep every machine live
          <span class="rounded-full border border-border px-2 py-px text-[0.7rem] font-medium uppercase tracking-wide text-muted">
            Experimental
          </span>
        </p>
        <p class="mt-1 text-xs text-muted">
          Other machines' sessions update as they change, instead of every 15 seconds, and open here without a reload.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingLiveMachines"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isLiveMachinesEnabled"
          :disabled="preferencesStore.isLoading || isSavingLiveMachines"
          aria-label="Keep every machine live"
          data-testid="live-machines-switch"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isLiveMachinesEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleLiveMachines"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isLiveMachinesEnabled ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>

    <div
      v-if="machines.hasMachines"
      class="mt-3 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4"
    >
      <div>
        <p class="flex items-center gap-2 text-sm font-medium text-text">
          Hand work to other machines
          <span class="rounded-full border border-border px-2 py-px text-[0.7rem] font-medium uppercase tracking-wide text-muted">
            Experimental
          </span>
        </p>
        <p class="mt-1 text-xs text-muted">
          Let an agent start a session on another machine, give it a task, and message and read it there. Only on the
          machines you allow in Settings → Machines. This Fleet makes every call there, so agents never see a
          machine's token. OpenCode and Claude Code sessions get it; Pi sessions don't. Applies to sessions started
          afterwards.
        </p>
        <p
          v-if="!isSessionMessagesEnabled"
          class="mt-1 text-xs text-muted"
          data-testid="agent-handoff-needs-messages"
        >
          Turn on Messages between sessions first: the hand-off uses its tools.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingAgentHandoff"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isAgentHandoffEnabled && isSessionMessagesEnabled"
          :disabled="preferencesStore.isLoading || isSavingAgentHandoff || !isSessionMessagesEnabled"
          aria-label="Let agents hand work to other machines"
          data-testid="agent-handoff-switch"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isAgentHandoffEnabled && isSessionMessagesEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleAgentHandoff"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isAgentHandoffEnabled && isSessionMessagesEnabled ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>
    <div class="mt-3 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
      <div>
        <p class="flex items-center gap-2 text-sm font-medium text-text">
          Mods
          <span class="rounded-full border border-border px-2 py-px text-[0.7rem] font-medium uppercase tracking-wide text-muted">
            Experimental
          </span>
        </p>
        <p class="mt-1 text-xs text-muted">
          Agents can write small add-ons that draw in Fleet: counts on tool rows, a band above the composer, a status-bar chip. You review each one before it's kept.
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingMods"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isModsEnabled"
          :disabled="preferencesStore.isLoading || isSavingMods"
          aria-label="Enable Mods"
          data-testid="mods-switch"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isModsEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleMods"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isModsEnabled ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>
  </section>
</template>
