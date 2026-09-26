<script setup lang="ts">
import type { ClientConfigResponse, UserMeResponse } from "@/api/client";
import { computed, onMounted, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import OnboardingGate from "@/components/auth/OnboardingGate.vue";
import { api } from "@/api/client";
import { useAppShellStore } from "@/stores/app-shell";
import { getActiveMachine, switchToMachine } from "@/lib/machines";

const appShellStore = useAppShellStore();
const { isLoading, user } = storeToRefs(appShellStore);
const errorMessage = shallowRef<string | null>(null);

const isReady = computed(() => !isLoading.value && user.value !== null);
// The page works in another machine; this machine's login page can't fix that machine turning it away.
const remoteMachine = getActiveMachine();

onMounted(() => {
  void hydrateShell();
});

async function hydrateShell(): Promise<void> {
  appShellStore.setLoading(true);
  errorMessage.value = null;

  try {
    const { data: clientConfig, error: configError, response: configResponse } = await api.GET("/api/config/client");

    if ((configResponse as Response).status === 401) {
      appShellStore.clear();
      redirectToLogin();
      return;
    }

    if (configError || !clientConfig) {
      throw new Error(`Client config request failed with status ${(configResponse as Response).status}.`);
    }

    appShellStore.setConfig(clientConfig as ClientConfigResponse);

    const { data: currentUser, error: userError, response: userResponse } = await api.GET("/api/user/me");

    if ((userResponse as Response).status === 401) {
      appShellStore.clear();
      redirectToLogin();
      return;
    }

    if (userError || !currentUser) {
      throw new Error(`Current user request failed with status ${(userResponse as Response).status}.`);
    }

    appShellStore.setUser(currentUser as UserMeResponse);
  } catch (error) {
    appShellStore.clear();
    errorMessage.value = remoteMachine
      ? `Couldn't reach ${remoteMachine.name} at ${remoteMachine.baseUrl}.`
      : error instanceof Error ? error.message : "Unable to verify your session.";
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

  if (remoteMachine) {
    errorMessage.value = `${remoteMachine.name} didn't accept this device's token. It may have been replaced; add the machine again with the new one.`;
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
    <div class="auth-gate__message">
      <p>{{ errorMessage }}</p>
      <button
        v-if="remoteMachine"
        type="button"
        class="auth-gate__home"
        data-testid="machine-go-home"
        @click="switchToMachine(null, '/')"
      >
        Back to this machine
      </button>
    </div>
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

.auth-gate__message {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  max-width: 420px;
  text-align: center;
}

.auth-gate__home {
  border: 1px solid var(--border-strong, var(--border));
  border-radius: 8px;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 0.875rem;
  padding: 6px 12px;
  cursor: pointer;
}

.auth-gate__home:hover {
  border-color: var(--accent);
  color: var(--accent);
}
</style>
