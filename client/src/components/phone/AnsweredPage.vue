<script setup lang="ts">
import { computed } from "vue";
import { useSearch } from "@tanstack/vue-router";
import { Check, X } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
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
    <span
      class="answered__icon"
      :class="{ 'answered__icon--no': !allowed }"
      aria-hidden="true"
    >
      <Check
        v-if="allowed"
        :size="28"
      />
      <X
        v-else
        :size="28"
      />
    </span>
    <h1 class="answered__title">
      {{ allowed ? `Allowed. ${machineName} carries on.` : `Denied. ${machineName} was told.` }}
    </h1>
    <p class="answered__text">
      {{ allowed ? "It ran once; it'll ask again next time." : "The agent will try something else, or ask you." }}
    </p>
    <!-- A session on another machine opens with a page load there. -->
    <Button
      v-if="sessionPath"
      as="a"
      :href="sessionPath"
      class="h-11 w-full"
    >
      Open the session
    </Button>
    <Button
      as="a"
      href="/phone"
      variant="outline"
      class="h-11 w-full"
    >
      Back to Needs you
    </Button>
  </main>
</template>

<style scoped>
.answered {
  display: grid;
  flex: 1;
  align-content: center;
  justify-items: center;
  gap: 12px;
  max-width: 420px;
  margin: 0 auto;
  padding: 24px 20px;
  text-align: center;
}

.answered__icon {
  display: grid;
  width: 56px;
  height: 56px;
  place-items: center;
  border-radius: 50%;
  background: color-mix(in srgb, var(--running) 14%, transparent);
  color: var(--running);
}

.answered__icon--no {
  background: color-mix(in srgb, var(--error) 12%, transparent);
  color: var(--error);
}

.answered__title {
  font-size: 20px;
  font-weight: 600;
}

.answered__text {
  margin-bottom: 8px;
  font-size: 14px;
  color: var(--muted);
}
</style>
