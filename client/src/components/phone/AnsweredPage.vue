<script setup lang="ts">
import { computed } from "vue";
import { useSearch } from "@tanstack/vue-router";
import { Check, X } from "lucide-vue-next";
import { readCredentialsSync } from "@/lib/device-credentials";

/**
 * Where tapping "Allowed — falcon carries on" lands (`/phone/answered`): what was answered, then the session or the
 * inbox. The answer itself went from the notification's button.
 */
const search = useSearch({ from: "/phone/answered" });
const allowed = computed(() => search.value.reply !== "reject");
const machineName = computed(() => {
  const credentials = readCredentialsSync();
  if (!credentials || search.value.machine === credentials.homeMachineId) return credentials?.homeMachineName ?? "The machine";
  return search.value.machine ?? "The machine";
});
const sessionPath = computed(() => search.value.machine && search.value.session
  ? `/phone/s/${encodeURIComponent(search.value.machine)}/${encodeURIComponent(search.value.session)}`
  : null);
</script>

<template>
  <main
    class="ph-page answered"
    data-testid="answered-page"
  >
    <div class="answered__body">
      <div class="ph-sheet__pad ph-hero">
        <span
          class="answered__icon"
          :class="{ 'answered__icon--no': !allowed }"
          aria-hidden="true"
        >
          <Check
            v-if="allowed"
            :size="34"
            :stroke-width="2.6"
          />
          <X
            v-else
            :size="34"
            :stroke-width="2.6"
          />
        </span>
        <h3>{{ allowed ? `Allowed. ${machineName} carries on.` : `Denied. ${machineName} was told.` }}</h3>
        <p>{{ allowed ? "It ran once; it'll ask again next time." : "The agent will try something else, or ask you." }}</p>
      </div>
      <div class="ph-sheet__pad answered__actions">
        <!-- A session on another machine opens with a page load there. -->
        <a
          v-if="sessionPath"
          :href="sessionPath"
          class="ph-btn ph-btn--primary ph-btn--big"
        ><span>Open the session</span></a>
        <a
          href="/phone"
          class="ph-btn ph-btn--big"
        ><span>Back to Needs you</span></a>
      </div>
    </div>
  </main>
</template>

<style scoped>
.answered {
  display: flex;
  flex-direction: column;
  justify-content: center;
}

.answered__body {
  width: 100%;
  max-width: 480px;
  margin: 0 auto;
}

.answered__icon {
  display: grid;
  width: 72px;
  height: 72px;
  margin: 0 auto;
  place-items: center;
  border-radius: 50%;
  background: color-mix(in srgb, var(--running) 16%, transparent);
  color: var(--running);
}

.answered__icon--no {
  background: color-mix(in srgb, var(--error) 14%, transparent);
  color: var(--error);
}

.answered__actions {
  display: grid;
  gap: 10px;
  margin-top: 28px;
}

.answered__actions a {
  text-decoration: none;
}
</style>
