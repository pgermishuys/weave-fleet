<script setup lang="ts">
import { shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

/** Adds Fleet's test-chips sample mod (pass/fail chips on test runs) so there is something to try. */
const isAdding = shallowRef(false);
const message = shallowRef<{ text: string; failed: boolean } | null>(null);

async function addSample(): Promise<void> {
  isAdding.value = true;
  message.value = null;
  try {
    const response = await apiFetch("/api/mods/samples/test-chips", { method: "POST" });
    if (response.ok) {
      message.value = { text: "Added. Run tests in any session to see it.", failed: false };
      return;
    }
    const body = (await response.json().catch(() => ({}))) as { error?: string; message?: string };
    message.value = { text: body.error ?? body.message ?? `The sample couldn't be added (HTTP ${response.status}).`, failed: true };
  } catch (error) {
    message.value = { text: error instanceof Error ? error.message : "The sample couldn't be added.", failed: true };
  } finally {
    isAdding.value = false;
  }
}
</script>

<template>
  <div class="mt-2 flex items-center gap-3 text-xs">
    <button
      type="button"
      class="text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-60"
      :disabled="isAdding"
      data-testid="mods-sample-button"
      @click="addSample"
    >
      Try the test-chips sample
    </button>
    <span
      v-if="message"
      :class="message.failed ? 'text-red-400' : 'text-muted'"
      role="status"
      data-testid="mods-sample-message"
    >
      {{ message.text }}
    </span>
  </div>
</template>
