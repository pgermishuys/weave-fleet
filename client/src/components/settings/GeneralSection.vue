<script setup lang="ts">
import { computed, shallowRef } from "vue";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import SettingSwitch from "@/components/settings/SettingSwitch.vue";
import { useBoardFeature } from "@/composables/use-board-feature";
import { usePreferenceSwitch } from "@/composables/use-preference-switch";
import { SESSION_RECAP_PREFERENCE_KEY } from "@/composables/use-session-recap";
import { RETRY_AFTER_LIMITS_PREFERENCE_KEY } from "@/composables/use-session-retry";
import { USAGE_LIMITS_CHIP_PREFERENCE_KEY } from "@/lib/usage-providers";
import { LARGE_PASTES_PREFERENCE_KEY } from "@/lib/attached-files";
import { STARTERS_PREFERENCE_KEY } from "@/lib/starters";
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

// On unless turned off: the new-session page offers a few sentences to start from.
const starters = usePreferenceSwitch(STARTERS_PREFERENCE_KEY, { defaultOn: true });
// Off unless turned on.
const recap = usePreferenceSwitch(SESSION_RECAP_PREFERENCE_KEY);
// On unless turned off: a turn a model provider's limit stopped carries on by itself.
const retryAfterLimits = usePreferenceSwitch(RETRY_AFTER_LIMITS_PREFERENCE_KEY, { defaultOn: true });
// On unless turned off.
const usageChip = usePreferenceSwitch(USAGE_LIMITS_CHIP_PREFERENCE_KEY, { defaultOn: true });

const largePastes = computed(() => preferencesStore.get(LARGE_PASTES_PREFERENCE_KEY, "ask"));

async function setLargePastes(event: Event): Promise<void> {
  await preferencesStore.set(LARGE_PASTES_PREFERENCE_KEY, (event.target as HTMLSelectElement).value);
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
  <div
    class="flex flex-col gap-6"
    data-testid="general-section"
  >
    <SettingGroup
      id="new-sessions"
      title="New sessions"
    >
      <SettingRow
        id="starters"
        title="Show starters"
        summary="A few sentences to start from on the new-session page."
      >
        <template #more>
          On the new-session page, offer a few sentences to start from, such as "Explore the code". A starter
          fills the box; nothing is sent until you press Enter.
        </template>
        <template #control>
          <SettingSwitch
            :checked="starters.enabled.value"
            :saving="starters.saving.value"
            :disabled="preferencesStore.isLoading || starters.saving.value"
            aria-label="Show starters"
            data-testid="starters-switch"
            @click="starters.toggle"
          />
        </template>
      </SettingRow>
      <SettingRow
        id="large-pastes"
        title="Large pastes"
        summary="When you paste 2,000 characters or 25 lines or more."
      >
        <template #control>
          <select
            :value="largePastes"
            aria-label="Large pastes"
            data-testid="large-pastes-select"
            class="rounded-md border border-border bg-transparent px-2 py-1 text-sm text-text outline-none"
            @change="setLargePastes"
          >
            <option value="ask">
              Ask
            </option>
            <option value="attach">
              Always attach
            </option>
            <option value="text">
              Paste as text
            </option>
          </select>
        </template>
      </SettingRow>
    </SettingGroup>

    <SettingGroup
      id="away"
      title="While you're away"
    >
      <SettingRow
        id="notifications"
        title="Desktop notifications"
        summary="When a session needs you or finishes, unless it's the one in front of you."
      >
        <template #more>
          Tell me when a session needs an answer, or finishes, while I'm looking at something else. Nothing
          is sent about the session in front of you.
        </template>
        <template #below>
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
        </template>
        <template #control>
          <SettingSwitch
            :checked="isNotificationsEnabled"
            :saving="isSavingNotifications"
            :disabled="preferencesStore.isLoading || isSavingNotifications"
            aria-label="Enable desktop notifications"
            @click="toggleNotifications"
          />
        </template>
      </SettingRow>
      <SettingRow
        id="recap"
        title="Session recap"
        summary="A one-line recap when a turn finishes out of view. One extra request each."
      >
        <template #more>
          When a turn finishes while you're looking at something else, write a one-line recap above the
          composer for when you come back. Each recap is one extra request to the session's model.
        </template>
        <template #control>
          <SettingSwitch
            :checked="recap.enabled.value"
            :saving="recap.saving.value"
            :disabled="preferencesStore.isLoading || recap.saving.value"
            aria-label="Enable session recap"
            @click="recap.toggle"
          />
        </template>
      </SettingRow>
      <SettingRow
        id="retry-after-limits"
        title="Try again when a limit resets"
        summary="Sends &quot;Continue where you left off.&quot; once a rate limit resets."
      >
        <template #more>
          When a model provider's rate limit or usage limit stops a turn, send "Continue where you left off."
          when the limit resets, or after a wait when the provider doesn't say. What you queued waits for it.
        </template>
        <template #control>
          <SettingSwitch
            :checked="retryAfterLimits.enabled.value"
            :saving="retryAfterLimits.saving.value"
            :disabled="preferencesStore.isLoading || retryAfterLimits.saving.value"
            aria-label="Try again when a limit resets"
            data-testid="retry-after-limits-switch"
            @click="retryAfterLimits.toggle"
          />
        </template>
      </SettingRow>
    </SettingGroup>

    <SettingGroup
      id="rail"
      title="Rail and status bar"
    >
      <SettingRow
        id="usage-chip"
        title="Show usage in the status bar"
        summary="The tightest provider limit, grey until 80%."
      >
        <template #more>
          Fleet asks the providers you've signed in to how much you've used, read-only: Claude automatically, the others after you press Allow. The chip shows the tightest limit, grey until 80%. Turn it off to hide it; nothing else changes.
        </template>
        <template #control>
          <SettingSwitch
            :checked="usageChip.enabled.value"
            :disabled="preferencesStore.isLoading || usageChip.saving.value"
            aria-label="Show usage in the status bar"
            data-testid="usage-limits-chip-switch"
            @click="usageChip.toggle"
          />
        </template>
      </SettingRow>
      <SettingRow
        id="board"
        title="Board"
        summary="The Board entry in the rail, with its panels."
      >
        <template #more>
          Show the Board entry in the left rail and enable the board workspace panels.
        </template>
        <template #below>
          <p
            v-if="boardFeatureError"
            class="mt-2 text-xs text-red-300"
            role="alert"
          >
            {{ boardFeatureError }}
          </p>
        </template>
        <template #control>
          <SettingSwitch
            :checked="isBoardFeatureEnabled"
            :saving="isSavingBoardFeature"
            :disabled="preferencesStore.isLoading || isSavingBoardFeature"
            aria-label="Enable Board feature"
            @click="toggleBoardFeature"
          />
        </template>
      </SettingRow>
    </SettingGroup>
  </div>
</template>
