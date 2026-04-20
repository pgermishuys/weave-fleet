<script setup lang="ts">
import type { ClientConfigResponse, UserMeResponse } from "@/lib/api-types";
import { computed, onMounted, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import OnboardingGate from "@/components/auth/OnboardingGate.vue";
import { apiFetch } from "@/lib/api-client";
import { useAppShellStore } from "@/stores/app-shell";

const appShellStore = useAppShellStore();
const { isLoading, user } = storeToRefs(appShellStore);
const errorMessage = shallowRef<string | null>(null);

const isReady = computed(() => !isLoading.value && user.value !== null);

onMounted(() => {
  void hydrateShell();
});

async function hydrateShell(): Promise<void> {
  appShellStore.setLoading(true);
  errorMessage.value = null;

  try {
    const clientConfigResponse = await apiFetch("/api/config/client");

    if (clientConfigResponse.status === 401) {
      appShellStore.clear();
      redirectToLogin();
      return;
    }

    if (!clientConfigResponse.ok) {
      throw new Error(`Client config request failed with status ${clientConfigResponse.status}.`);
    }

    const clientConfig = await clientConfigResponse.json() as ClientConfigResponse;
    appShellStore.setConfig(clientConfig);

    const userResponse = await apiFetch("/api/user/me");

    if (userResponse.status === 401) {
      appShellStore.clear();
      redirectToLogin();
      return;
    }

    if (!userResponse.ok) {
      throw new Error(`Current user request failed with status ${userResponse.status}.`);
    }

    const currentUser = await userResponse.json() as UserMeResponse;
    appShellStore.setUser(currentUser);
  } catch (error) {
    appShellStore.clear();
    errorMessage.value = error instanceof Error ? error.message : "Unable to verify your session.";
  } finally {
    if (errorMessage.value === null && user.value !== null) {
      appShellStore.setLoading(false);
      return;
    }

    if (window.location.pathname !== "/login") {
      appShellStore.setLoading(false);
    }
  }
}

function redirectToLogin(): void {
  if (typeof window === "undefined") {
    return;
  }

  if (window.location.pathname !== "/login") {
    const returnUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;
    window.location.replace(`/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  }
}
</script>

<template>
  <div
    v-if="isLoading"
    class="auth-gate auth-gate--status"
    role="status"
    aria-live="polite"
  >
    Checking your session…
  </div>

  <div
    v-else-if="errorMessage"
    class="auth-gate auth-gate--status"
    role="alert"
  >
    {{ errorMessage }}
  </div>

  <div
    v-else-if="!user"
    class="auth-gate auth-gate--status"
    role="status"
    aria-live="polite"
  >
    Redirecting to login…
  </div>

  <OnboardingGate v-else-if="isReady">
    <slot />
  </OnboardingGate>
</template>

<style scoped>
.auth-gate {
  min-height: 100%;
}

.auth-gate--status {
  display: grid;
  place-items: center;
  color: var(--muted);
  font-size: 0.95rem;
  padding: 24px;
}
</style>
