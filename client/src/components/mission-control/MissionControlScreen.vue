<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import NeedsYouColumn from "@/components/mission-control/NeedsYouColumn.vue";
import PeekPanel from "@/components/mission-control/PeekPanel.vue";
import SessionTile from "@/components/mission-control/SessionTile.vue";
import { useMissionControl } from "@/composables/use-mission-control";
import type { Grouping, Tile } from "@/lib/mission-control";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";
import { useMachinesStore } from "@/stores/machines";

const router = useRouter();
const machines = useMachinesStore();
const mission = useMissionControl();
const selectedKey = shallowRef<string | null>(null);
const selected = computed(() => mission.tiles.value.find((tile) => tile.key === selectedKey.value) ?? null);

const GROUPINGS: { value: Grouping; label: string }[] = [
  { value: "folder", label: "Folder" },
  { value: "machine", label: "Machine" },
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
    <div class="flex min-h-0 flex-1">
      <NeedsYouColumn
        :items="mission.waiting.value"
        @answer-question="answerQuestion"
        @answer-permission="answerPermission"
        @open="open"
      />
      <section class="min-w-0 flex-1 overflow-y-auto p-4">
        <div class="mb-3 flex items-center gap-2 text-xs text-muted">
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
          v-if="!mission.tiles.value.length"
          class="text-sm text-muted"
        >
          No sessions are working or waiting. Start one from Sessions.
        </p>
        <div
          v-for="group in mission.groups.value"
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
