<script setup lang="ts">
import { ArrowLeft, ChevronLeft, Ellipsis } from "lucide-vue-next";
import { phoneLook } from "@/composables/phone/use-phone-env";

/**
 * The session's bar, and its status: back, the title, and under it a dot, the machine and what it's doing —
 * "hangar · Working · 1m 12s", "hangar · Needs you" (amber), "hangar · Finished 22 min ago". Glass buttons on
 * iPhone; a back arrow and plain icons on Android. Content blurs under it once scrolled.
 */
defineProps<{
  title: string;
  machineName: string;
  tone: "working" | "needs-you" | "finished" | "error" | "unreachable" | "idle";
  status: string;
  scrolled: boolean;
}>();
const emit = defineEmits<{ (event: "back"): void; (event: "menu"): void }>();
</script>

<template>
  <header
    class="ph-navbar"
    :class="{ 'ph-navbar--scrolled': scrolled }"
    data-testid="phone-session-header"
  >
    <button
      type="button"
      class="ph-navbtn ph-glass"
      aria-label="Back"
      data-testid="phone-session-back"
      @click="emit('back')"
    >
      <ArrowLeft
        v-if="phoneLook === 'android'"
        :size="24"
        aria-hidden="true"
      />
      <ChevronLeft
        v-else
        :size="26"
        :stroke-width="2.4"
        aria-hidden="true"
      />
    </button>
    <div class="psh">
      <h1 class="psh__title">
        {{ title }}
      </h1>
      <p class="psh__line">
        <span
          class="ph-dot psh__dot"
          :class="{
            'ph-dot--running': tone === 'working',
            'ph-dot--waiting': tone === 'needs-you',
            'ph-dot--done': tone === 'finished',
            'ph-dot--error': tone === 'error' || tone === 'unreachable',
          }"
          aria-hidden="true"
        />
        <span class="psh__machine">{{ machineName }}</span>
        <span
          class="psh__state"
          data-testid="phone-session-state"
        >· {{ status }}</span>
      </p>
    </div>
    <button
      type="button"
      class="ph-navbtn ph-glass"
      aria-label="More"
      data-testid="phone-session-menu"
      @click="emit('menu')"
    >
      <Ellipsis
        :size="24"
        :stroke-width="2.6"
        aria-hidden="true"
      />
    </button>
  </header>
</template>

<style scoped>
.psh {
  display: flex;
  flex: 1;
  flex-direction: column;
  justify-content: center;
  min-width: 0;
  padding: 0 6px;
}

.psh__title {
  margin: 0;
  overflow: hidden;
  font-size: var(--ph-t-body);
  font-weight: 600;
  line-height: 1.25;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.psh__line {
  display: flex;
  align-items: center;
  gap: 5px;
  margin: 0;
  overflow: hidden;
  font-size: var(--ph-t-foot);
  color: var(--muted);
  white-space: nowrap;
}

.psh__dot {
  width: 7px;
  height: 7px;
}

.psh__state {
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
