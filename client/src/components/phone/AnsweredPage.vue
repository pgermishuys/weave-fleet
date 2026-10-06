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
    class="answered"
    data-testid="answered-page"
  >
    <div class="ph-panel ph-panel--full">
      <div class="ph-center-page answered__page">
        <div class="answered__body">
          <span
            class="ph-done-mark"
            :class="{ 'ph-done-mark--bad': !allowed }"
            aria-hidden="true"
          >
            <Check v-if="allowed" />
            <X v-else />
          </span>
          <h1 class="answered__title">
            {{ allowed ? `Allowed. ${machineName} carries on.` : `Denied. ${machineName} was told.` }}
          </h1>
          <p class="ph-center-page__lede">
            {{ allowed ? "It ran once; it'll ask again next time." : "The agent will try something else, or ask you." }}
          </p>
          <div class="ph-actions">
            <!-- A session on another machine opens with a page load there. -->
            <a
              v-if="sessionPath"
              :href="sessionPath"
              class="ph-btn ph-btn--primary ph-btn--block"
            ><span>Open the session</span></a>
            <a
              href="/phone"
              class="ph-btn ph-btn--outline ph-btn--block"
            ><span>Back to Needs you</span></a>
          </div>
        </div>
      </div>
    </div>
  </main>
</template>

<style scoped>
.answered {
  position: absolute;
  inset: 0;
}

.answered__body {
  width: 100%;
  max-width: 480px;
  margin: 0 auto;
  text-align: center;
}

.answered__title {
  margin-top: 18px;
  margin-bottom: 6px;
}

.answered .ph-actions a {
  text-decoration: none;
}
</style>
