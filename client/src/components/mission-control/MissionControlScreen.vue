<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import NeedsYouColumn from "@/components/mission-control/NeedsYouColumn.vue";
import PeekPanel from "@/components/mission-control/PeekPanel.vue";
import SessionTile from "@/components/mission-control/SessionTile.vue";
import { useFleetView } from "@/composables/use-fleet-view";
import { useSharedMissionControl } from "@/composables/use-shared-mission-control";
import FleetViewStrip from "@/components/mission-control/FleetViewStrip.vue";
import { groupTiles, type Grouping, type Tile } from "@/lib/mission-control";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";
import { useMachinesStore } from "@/stores/machines";

const router = useRouter();
const machines = useMachinesStore();
const mission = useSharedMissionControl();
const view = useFleetView();
const selectedKey = shallowRef<string | null>(null);
const machine = computed(() => {
  const current = view.value;
  return current.kind === "machine" ? machines.entries.find((entry) => entry.key === current.machineId) ?? null : null;
});
// A machine's view lists only that machine's sessions; Needs you lists the asks from all of them.
const machineTiles = computed(() => mission.tiles.value.filter((tile) => tile.machineId === machine.value?.key));
const machineGroups = computed(() => groupTiles(machineTiles.value, mission.grouping.value));
const selected = computed(() => machineTiles.value.find((tile) => tile.key === selectedKey.value) ?? null);

const GROUPINGS: { value: Grouping; label: string }[] = [
  { value: "folder", label: "Folder" },
  { value: "state", label: "State" },
];

function open(tile: Tile): void {
  const path = `/sessions/${encodeURIComponent(tile.sessionId)}`;
  if (tile.machineId === machines.liveKey) void router.navigate({ to: "/sessions/$id", params: { id: tile.sessionId }, search: { instanceId: undefined, parentSessionId: undefined } });
  else machines.openOn(tile.machineId, path);
}

function answerQuestion(tile: Tile, requestId: string, answers: string[][], done: (outcome: AnswerOutcome) => void): void {
  void mission.answerQuestion(tile, requestId, answers).then(done);
}

function answerPermission(tile: Tile, reply: PermissionReply, done: (outcome: AnswerOutcome) => void): void {
  if (tile.ask?.kind !== "permission") return;
  void mission.answerPermission(tile, tile.ask.ask.sessionId, tile.ask.ask.id, reply).then(done);
}

function nudge(tile: Tile, text: string, done: (outcome: AnswerOutcome & { queued: boolean }) => void): void {
  void mission.nudge(tile, text).then(done);
}
</script>

<template>
  <div
    class="flex h-full min-h-0 flex-col"
    data-testid="mission-control"
  >
    <header class="flex items-center gap-3 border-b border-border px-4 py-3">
      <h2 class="text-base font-semibold text-text">
        Fleet
      </h2>
      <p class="text-xs text-muted">
        {{ mission.summary.value.running }} running · {{ mission.summary.value.helpers }} helpers · {{ mission.summary.value.needYou }} need you
      </p>
    </header>
    <!-- On a phone the menu is a strip of chips here; beside the page it is the context panel. -->
    <FleetViewStrip class="fleet-view-strip" />
    <div
      v-if="view.kind === 'needs-you'"
      class="flex min-h-0 flex-1"
      data-testid="fleet-needs-you-view"
    >
      <NeedsYouColumn
        wide
        :items="mission.waiting.value"
        @answer-question="answerQuestion"
        @answer-permission="answerPermission"
        @open="open"
      />
    </div>
    <p
      v-else-if="!machine"
      class="p-4 text-sm text-muted"
      data-testid="fleet-machine-gone"
    >
      That machine isn't in the list any more.
    </p>
    <div
      v-else
      class="flex min-h-0 flex-1"
      data-testid="fleet-machine-view"
    >
      <section class="min-w-0 flex-1 overflow-y-auto p-4">
        <div class="mb-3 flex items-center gap-2 text-xs text-muted">
          <h3 class="mr-2 text-sm font-semibold text-text">
            {{ machine.name }}
          </h3>
          <span>Group by</span>
          <div
            class="inline-flex rounded-card border border-border"
            role="group"
            aria-label="Group by"
          >
            <button
              v-for="option in GROUPINGS"
              :key="option.value"
              type="button"
              class="px-2.5 py-1 text-xs"
              :class="mission.grouping.value === option.value ? 'bg-accent-dim font-medium text-accent' : 'text-muted hover:text-text'"
              :aria-pressed="mission.grouping.value === option.value"
              :data-testid="`group-${option.value}`"
              @click="mission.grouping.value = option.value"
            >
              {{ option.label }}
            </button>
          </div>
        </div>
        <p
          v-if="!machineTiles.length"
          class="text-sm text-muted"
        >
          No sessions are working or waiting on this machine. Start one from Sessions.
        </p>
        <div
          v-for="group in machineGroups"
          :key="group.key"
          class="mb-5"
          data-testid="mission-group"
        >
          <h3 class="mb-2 text-sm font-semibold text-text">
            {{ group.label }}
            <span class="ml-1 text-xs font-normal text-muted">{{ group.tiles.length }}</span>
          </h3>
          <div class="grid grid-cols-1 gap-3 xl:grid-cols-2">
            <SessionTile
              v-for="tile in group.tiles"
              :key="tile.key"
              :tile="tile"
              :selected="tile.key === selectedKey"
              @click="selectedKey = tile.key"
            />
          </div>
        </div>
      </section>
      <PeekPanel
        v-if="selected"
        :tile="selected"
        :target="mission.targetOf(selected.machineId)"
        @nudge="nudge"
        @open="open"
      />
    </div>
  </div>
</template>

<style scoped>
.fleet-view-strip {
  display: none;
  padding: 10px 16px 0;
}

@media (max-width: 767px) {
  .fleet-view-strip {
    display: flex;
  }
}
</style>
