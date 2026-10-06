<script setup lang="ts">
import { onMounted, shallowRef } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import { readCredentialsSync } from "@/lib/device-credentials";

/**
 * Sign-in for the phone pages. The phone signs in with its device cookie; if that's gone (cleared, or iOS gave the
 * Home Screen app its own storage) but its device token is still here, the token signs it in again. A phone with
 * neither isn't paired: it goes to /pair. Unlike the desktop gate there's no onboarding: that happens on a computer.
 */
const state = shallowRef<"checking" | "ready" | "error">("checking");
const message = shallowRef("");

async function signedIn(): Promise<boolean> {
  const response = await fetch("/api/user/me", { credentials: "include" });
  return response.ok;
}

onMounted(async () => {
  try {
    if (await signedIn()) {
      state.value = "ready";
      return;
    }

    const credentials = readCredentialsSync();
    if (credentials) {
      const login = await fetch("/auth/token-login", {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token: credentials.token }),
      });
      if (login.ok && await signedIn()) {
        state.value = "ready";
        return;
      }
    }

    window.location.replace("/pair");
  } catch {
    message.value = "Couldn't reach Fleet. Check the connection and try again.";
    state.value = "error";
  }
});
</script>

<template>
  <slot v-if="state === 'ready'" />
  <div
    v-else
    class="phone-gate"
    :role="state === 'error' ? 'alert' : 'status'"
  >
    <LoaderCircle
      v-if="state === 'checking'"
      class="ph-spinner"
      :size="22"
      aria-hidden="true"
    />
    <span v-else>{{ message }}</span>
  </div>
</template>

<style scoped>
.phone-gate {
  position: absolute;
  inset: 0;
  display: grid;
  padding: 24px;
  place-items: center;
  color: var(--muted);
  font-size: var(--ph-t-body);
  text-align: center;
}
</style>
