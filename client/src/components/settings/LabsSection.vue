<script setup lang="ts">
import { useRouter } from "@tanstack/vue-router";
import { shallowRef } from "vue";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import SettingSwitch from "@/components/settings/SettingSwitch.vue";
import { usePreferenceSwitch } from "@/composables/use-preference-switch";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { DREAMING_PREFERENCE_KEY } from "@/lib/dreaming";
import { FLEET_UPDATES_PREFERENCE_KEY } from "@/lib/fleet-updates";
import { JOBS_PREFERENCE_KEY } from "@/lib/jobs";
import { LIVE_MACHINES_PREFERENCE_KEY } from "@/lib/machines";
import { MISSION_CONTROL_PREFERENCE_KEY } from "@/lib/mission-control";
import { PAGE_CHOICE_PREFERENCE_KEY } from "@/lib/page-choice";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

const preferencesStore = usePreferencesStore();
const machines = useMachinesStore();
const router = useRouter();
const { setActiveSection } = useSettingsNav();
preferencesStore.ensureLoaded();

const liveMachines = usePreferenceSwitch(LIVE_MACHINES_PREFERENCE_KEY);
const fleetUpdates = usePreferenceSwitch(FLEET_UPDATES_PREFERENCE_KEY);
const jobs = usePreferenceSwitch(JOBS_PREFERENCE_KEY);
const missionControl = usePreferenceSwitch(MISSION_CONTROL_PREFERENCE_KEY);
const dreaming = usePreferenceSwitch(DREAMING_PREFERENCE_KEY);
const pageChoice = usePreferenceSwitch(PAGE_CHOICE_PREFERENCE_KEY);

const { isWorkflowsEnabled, setWorkflowsEnabled } = useWorkflowsFeature();
const isSavingWorkflows = shallowRef(false);

async function toggleWorkflows(): Promise<void> {
  isSavingWorkflows.value = true;
  try {
    await setWorkflowsEnabled(!isWorkflowsEnabled.value);
  } finally {
    isSavingWorkflows.value = false;
  }
}

/** What a switch adds while it is on, or nothing while it is off. */
function addsWhenOn(on: boolean, text: string): string | null {
  return on ? text : null;
}

function openRoute(path: string): void {
  void router.navigate({ to: path });
}
</script>

<template>
  <div
    class="flex flex-col gap-6"
    data-testid="labs-section"
  >
    <SettingGroup
      id="experiments"
      title="Experiments"
      hint="Each is off until you turn it on"
    >
      <SettingRow
        v-if="machines.hasMachines"
        id="live-machines"
        title="Keep every machine live"
        summary="Other machines update as they change, not every 15 seconds."
      >
        <template #more>
          Other machines' sessions update as they change, instead of every 15 seconds, and open here without a reload.
        </template>
        <template #control>
          <SettingSwitch
            :checked="liveMachines.enabled.value"
            :saving="liveMachines.saving.value"
            :disabled="preferencesStore.isLoading || liveMachines.saving.value"
            aria-label="Keep every machine live"
            data-testid="live-machines-switch"
            @click="liveMachines.toggle"
          />
        </template>
      </SettingRow>

      <SettingRow
        id="fleet-updates"
        title="Updates in Machines"
        summary="Each machine's version, update rule and Restart."
        :adds="addsWhenOn(fleetUpdates.enabled.value, 'Machine versions and update rules in Updates & about')"
        @open="setActiveSection('updates', 'machines')"
      >
        <template #more>
          Shows each machine's version, update rule and Restart in Settings → Updates & about, plus Update machines now,
          which restarts every machine that has an update downloaded, one at a time.
        </template>
        <template #control>
          <SettingSwitch
            :checked="fleetUpdates.enabled.value"
            :saving="fleetUpdates.saving.value"
            :disabled="preferencesStore.isLoading || fleetUpdates.saving.value"
            aria-label="Show updates in Machines"
            data-testid="fleet-updates-switch"
            @click="fleetUpdates.toggle"
          />
        </template>
      </SettingRow>

      <SettingRow
        id="jobs"
        title="Jobs"
        summary="One queue; Fleet starts each job wherever a seat is free."
        :adds="addsWhenOn(jobs.enabled.value, 'Jobs in the rail, seats on each machine')"
        @open="openRoute('/jobs')"
      >
        <template #more>
          One queue of jobs. Fleet starts each on whichever of your machines has a free seat. Give machines seats in Settings → Machines.
        </template>
        <template #control>
          <SettingSwitch
            :checked="jobs.enabled.value"
            :saving="jobs.saving.value"
            :disabled="preferencesStore.isLoading || jobs.saving.value"
            aria-label="Enable Jobs"
            data-testid="jobs-switch"
            @click="jobs.toggle"
          />
        </template>
      </SettingRow>

      <SettingRow
        id="mission-control"
        title="Mission control"
        summary="One screen for every running agent. It becomes the first screen."
        :adds="addsWhenOn(missionControl.enabled.value, 'Fleet in the rail, the first screen')"
        @open="openRoute('/fleet')"
      >
        <template #more>
          One screen for every running agent on every machine: what needs you, what each is doing right now, and a nudge box. It becomes the first screen when Fleet opens.
        </template>
        <template #control>
          <SettingSwitch
            :checked="missionControl.enabled.value"
            :saving="missionControl.saving.value"
            :disabled="preferencesStore.isLoading || missionControl.saving.value"
            aria-label="Enable Mission control"
            data-testid="mission-control-switch"
            @click="missionControl.toggle"
          />
        </template>
      </SettingRow>

      <SettingRow
        id="dreaming"
        title="Dreaming"
        summary="Fleet reads recent sessions and updates memory notes overnight."
        :adds="addsWhenOn(dreaming.enabled.value, 'Nightly run in Memory')"
        @open="setActiveSection('memory')"
      >
        <template #more>
          Fleet reads a repository's recent sessions and updates its memory notes. Every change says why, and you can undo it. Notes you wrote are never changed.
        </template>
        <template #control>
          <SettingSwitch
            :checked="dreaming.enabled.value"
            :saving="dreaming.saving.value"
            :disabled="preferencesStore.isLoading || dreaming.saving.value"
            aria-label="Enable Dreaming"
            data-testid="dreaming-switch"
            @click="dreaming.toggle"
          />
        </template>
      </SettingRow>

      <SettingRow
        id="page-choice"
        title="Choose on a page"
        summary="The agent shows you a page and waits while you pick."
      >
        <template #more>
          The agent can show you a page and wait while you pick on it. Works with OpenCode and Claude Code; GitHub Copilot needs a weave-copilot update first.
        </template>
        <template #control>
          <SettingSwitch
            :checked="pageChoice.enabled.value"
            :saving="pageChoice.saving.value"
            :disabled="preferencesStore.isLoading || pageChoice.saving.value"
            aria-label="Enable choosing on a page"
            data-testid="page-choice-switch"
            @click="pageChoice.toggle"
          />
        </template>
      </SettingRow>

      <SettingRow
        id="workflows"
        title="Workflows"
        summary="Workflows in the rail; Fleet runs their steps and stops where one asks you."
        :adds="addsWhenOn(isWorkflowsEnabled, 'Workflows in the rail')"
        @open="openRoute('/workflows')"
      >
        <template #more>
          Show Workflows in the rail, and let Fleet run workflows: it moves between steps and stops where a step
          asks you. Off: no runner, no step tool, nothing changes.
        </template>
        <template #control>
          <SettingSwitch
            :checked="isWorkflowsEnabled"
            :saving="isSavingWorkflows"
            :disabled="preferencesStore.isLoading || isSavingWorkflows"
            aria-label="Enable workflows"
            data-testid="workflows-switch"
            @click="toggleWorkflows"
          />
        </template>
      </SettingRow>
    </SettingGroup>
  </div>
</template>
