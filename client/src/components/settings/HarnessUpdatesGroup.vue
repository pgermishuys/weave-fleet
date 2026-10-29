<script setup lang="ts">
import { computed } from "vue";
import { RefreshCw } from "lucide-vue-next";
import HarnessUpdateStrip from "@/components/settings/HarnessUpdateStrip.vue";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import SettingSwitch from "@/components/settings/SettingSwitch.vue";
import { Button } from "@/components/ui/button";
import { useHarnessUpdatePoll } from "@/composables/use-harness-update-poll";
import { refreshAllHarnesses, useHarnesses } from "@/composables/use-harnesses";
import { usePreferencesStore } from "@/stores/preferences";
import type { HarnessInfo } from "@/api/client";

/**
 * Updates & about › Harnesses: the Check for updates switch and, for each harness Fleet found, its version with the
 * update it can run. Each harness's tab in Settings → Harnesses still shows its version in its header.
 */
const prefsStore = usePreferencesStore();
const { harnesses, isLoading, refresh } = useHarnesses();

/** Off stops Fleet looking up harnesses' latest versions on npm (`HarnessEndpoints.UpdateChecksPreference`). */
const UPDATE_CHECKS_PREFERENCE_KEY = "harnessUpdates.check";
const areUpdateChecksOn = computed(() => prefsStore.get(UPDATE_CHECKS_PREFERENCE_KEY, "true") !== "false");

async function toggleUpdateChecks(): Promise<void> {
  await prefsStore.set(UPDATE_CHECKS_PREFERENCE_KEY, areUpdateChecksOn.value ? "false" : "true");
  refreshAllHarnesses();
}

function isEnabled(harness: HarnessInfo): boolean {
  return prefsStore.get(`${harness.type}.enabled`, harness.userEnabled ? "true" : "false") === "true";
}

function versionLine(harness: HarnessInfo): string {
  if (!harness.version) return harness.available ? "Installed" : "Not installed";
  // The version is weave-copilot's, the program Fleet runs for GitHub Copilot, and the CLI it drives has its own.
  const cli = harness.type === "copilot" && harness.cliVersion ? ` · Copilot CLI ${harness.cliVersion}` : "";
  return `${harness.version}${cli}`;
}

useHarnessUpdatePoll(harnesses);
</script>

<template>
  <SettingGroup
    id="harnesses"
    title="Harnesses"
    hint="Each harness's version and its update."
  >
    <SettingRow
      id="harness-update-checks"
      title="Check for updates"
      summary="Fleet looks up each harness's latest version on npm."
    >
      <template #control>
        <div class="flex items-center gap-3">
          <Button
            variant="outline"
            size="sm"
            :disabled="isLoading"
            data-testid="harnesses-check-again"
            @click="void refresh()"
          >
            <RefreshCw
              :class="isLoading ? 'animate-spin' : ''"
              aria-hidden="true"
            />
            Check again
          </Button>
          <SettingSwitch
            :checked="areUpdateChecksOn"
            aria-label="Check for updates"
            data-testid="harnesses-update-checks"
            @click="void toggleUpdateChecks()"
          />
        </div>
      </template>
    </SettingRow>
    <SettingRow
      v-for="harness in harnesses"
      :id="`harness-version-${harness.type}`"
      :key="harness.type"
      :title="harness.displayName"
      :summary="versionLine(harness)"
    >
      <template #below>
        <div class="mt-2 empty:hidden">
          <HarnessUpdateStrip
            v-if="isEnabled(harness)"
            :harness="harness"
          />
        </div>
      </template>
    </SettingRow>
  </SettingGroup>
</template>
