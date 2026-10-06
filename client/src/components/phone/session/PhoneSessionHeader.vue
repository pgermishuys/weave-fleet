<script setup lang="ts">
import { computed } from "vue";
import { ChevronLeft, Ellipsis } from "lucide-vue-next";
import PhoneGlyph from "@/components/phone/PhoneGlyph.vue";
import type { HeaderTone } from "@/lib/phone/session-status";

/**
 * The session's head inside its panel, as the desktop's SessionDetailHeader: back, the title, and under it the status
 * glyph and what it's doing — "Needs you · hangar · weave-fleet" (amber), "Working · 1m 12s · hangar", "Finished
 * 22 min ago · hangar" — then ⋯ for the menu. A hairline under it.
 */
const props = defineProps<{
  title: string;
  machineName: string;
  folder?: string | null;
  tone: HeaderTone;
  status: string;
}>();
const emit = defineEmits<{ (event: "back"): void; (event: "menu"): void }>();

const glyph = computed(() => {
  switch (props.tone) {
    case "working": return "working" as const;
    case "needs-you": return "waiting" as const;
    case "error":
    case "unreachable": return "error" as const;
    case "finished": return "quiet" as const;
    default: return "idle" as const;
  }
});
const where = computed(() => (props.tone === "needs-you" ? [props.machineName, props.folder].filter(Boolean).join(" · ") : props.tone === "unreachable" ? "" : props.machineName));
</script>

<template>
  <header
    class="ph-shead"
    data-testid="phone-session-header"
  >
    <button
      type="button"
      class="ph-icon-btn ph-icon-btn--text"
      aria-label="Back"
      data-testid="phone-session-back"
      @click="emit('back')"
    >
      <ChevronLeft aria-hidden="true" />
    </button>
    <div class="ph-shead__main">
      <h1 class="ph-shead__t">
        {{ title }}
      </h1>
      <p class="ph-shead__s">
        <PhoneGlyph :kind="glyph" />
        <span
          data-testid="phone-session-state"
          :class="{ 'ph-shead__waiting': tone === 'needs-you', 'ph-shead__bad': tone === 'error' }"
        >{{ status }}</span>
        <span v-if="where">· {{ where }}</span>
      </p>
    </div>
    <button
      type="button"
      class="ph-icon-btn"
      aria-label="More"
      data-testid="phone-session-menu"
      @click="emit('menu')"
    >
      <Ellipsis aria-hidden="true" />
    </button>
  </header>
</template>
