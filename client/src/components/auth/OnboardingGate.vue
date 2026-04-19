<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import OnboardingWizard from "@/components/onboarding/OnboardingWizard.vue";
import { useAppShellStore } from "@/stores/app-shell";

const appShellStore = useAppShellStore();
const { config, user } = storeToRefs(appShellStore);
const dismissed = shallowRef(false);

const showWizard = computed(() => {
  if (!config.value.authEnabled || !config.value.cloudMode || user.value === null || dismissed.value) {
    return false;
  }

  return !user.value.onboardingStatus.completed;
});

const credentialsOptional = true;

function handleComplete(): void {
  dismissed.value = true;

  if (user.value === null) {
    return;
  }

  appShellStore.setUser({
    userId: user.value.userId,
    email: user.value.email,
    displayName: user.value.displayName,
    onboardingCompleted: true,
    onboardingStatus: {
      ...user.value.onboardingStatus,
      completed: true,
    },
    createdAt: user.value.createdAt,
  });
}
</script>

<template>
  <OnboardingWizard
    v-if="showWizard"
    :credentials-optional="credentialsOptional"
    @complete="handleComplete"
  />

  <slot />
</template>
