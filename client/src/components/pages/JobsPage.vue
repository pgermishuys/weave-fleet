<script setup lang="ts">
import FeatureOffNotice from "@/components/pages/FeatureOffNotice.vue";
import JobsScreen from "@/components/jobs/JobsScreen.vue";
import { useJobsFeature } from "@/composables/use-jobs-feature";

const { isJobsEnabled, setJobsEnabled } = useJobsFeature();
</script>

<template>
  <section class="flex h-full flex-col overflow-hidden">
    <JobsScreen v-if="isJobsEnabled" />
    <FeatureOffNotice
      v-else
      title="Jobs"
      description="Post jobs to one queue. Fleet starts each on whichever of your machines has a free seat."
      :turn-on="() => setJobsEnabled(true)"
    />
  </section>
</template>
