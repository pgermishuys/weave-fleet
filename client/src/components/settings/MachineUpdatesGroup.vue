<script setup lang="ts">
import { computed, onMounted, onUnmounted } from "vue";
import MachineUpdatesStrip from "@/components/settings/MachineUpdatesStrip.vue";
import MachineVersionLine from "@/components/settings/MachineVersionLine.vue";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import { useFleetUpdates } from "@/composables/use-fleet-updates";
import { useWhatsNew } from "@/composables/use-whats-new";
import type { FleetUpdateRow } from "@/lib/fleet-updates";
import { useMachinesStore } from "@/stores/machines";

/**
 * Updates & about › Machines (with Updates in Machines on in Labs): the release line and the rule every machine
 * follows, then each machine's version, where it is with updating, its own rule and Restart.
 */
const machines = useMachinesStore();
const fleetUpdates = useFleetUpdates();
const { openWhatsNew } = useWhatsNew();

const rows = computed(() => fleetUpdates.machineRows.value);
const unreachableKeys = computed(() => new Set(rows.value.filter((row) => row.state.kind === "unreachable").map((row) => row.key)));

const buttonClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";

/** "Restart into X" on a machine whose update is downloaded; the reason it can't is in its version line. */
function restartButton(row: FleetUpdateRow): { label: string; disabled: boolean } | null {
  const offer = row.restart;
  if (offer?.kind !== "button") return null;
  return { label: offer.label, disabled: offer.disabled || fleetUpdates.rolloutRunning.value || !!fleetUpdates.restartingSelf.value };
}

let stopPolling: (() => void) | null = null;
onMounted(() => {
  void machines.loadHome();
  stopPolling = machines.startPolling();
});
onUnmounted(() => stopPolling?.());
</script>

<template>
  <SettingGroup
    id="machines"
    title="Machines"
    hint="Each machine's version and when it updates."
  >
    <div class="px-4 pb-3 [&>div]:mt-3">
      <MachineUpdatesStrip
        :summary="fleetUpdates.summary.value"
        :checked-at="fleetUpdates.checkedAt.value"
        :rollout="fleetUpdates.rollout.value"
        :rollout-running="fleetUpdates.rolloutRunning.value"
        :is-checking="fleetUpdates.isChecking.value"
        :ready-machines="fleetUpdates.readyMachines.value"
        :unreachable="unreachableKeys"
        :chosen-rule="fleetUpdates.chosenRule.value"
        :outcome="fleetUpdates.outcome.value"
        :action-error="fleetUpdates.actionError.value"
        @check="fleetUpdates.checkNow()"
        @update="fleetUpdates.startRollout()"
        @stop="fleetUpdates.stopRollout()"
        @choose="fleetUpdates.chooseRule($event)"
        @whats-new="openWhatsNew"
      />
    </div>
    <SettingRow
      v-for="row in rows"
      :id="`machine-update-${row.key}`"
      :key="row.key"
      :title="row.name"
      :summary="row.isLive ? 'The machine you are working in' : row.where"
    >
      <template #below>
        <MachineVersionLine
          :row="row"
          :rollout="fleetUpdates.rollout.value"
          :chosen-rule="fleetUpdates.chosenRule.value"
          @override="fleetUpdates.overrideRule(row.key, $event)"
        />
      </template>
      <template #control>
        <button
          v-if="restartButton(row)"
          type="button"
          :class="buttonClass"
          :disabled="restartButton(row)!.disabled"
          data-testid="update-restart"
          @click="fleetUpdates.restart(row)"
        >
          {{ restartButton(row)!.label }}
        </button>
      </template>
    </SettingRow>
  </SettingGroup>
</template>
