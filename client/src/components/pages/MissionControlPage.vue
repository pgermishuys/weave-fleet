<script setup lang="ts">
import FeatureOffNotice from "@/components/pages/FeatureOffNotice.vue";
import MissionControlScreen from "@/components/mission-control/MissionControlScreen.vue";
import { useMissionControlFeature } from "@/composables/use-mission-control-feature";

const { isMissionControlEnabled, setMissionControlEnabled } = useMissionControlFeature();
</script>

<template>
  <section class="flex h-full flex-col overflow-hidden">
    <MissionControlScreen v-if="isMissionControlEnabled" />
    <FeatureOffNotice
      v-else
      title="Mission control"
      description="One screen for every running agent on every machine: what needs you, what each is doing right now, and a nudge box."
      :turn-on="() => setMissionControlEnabled(true)"
    />
  </section>
</template>
