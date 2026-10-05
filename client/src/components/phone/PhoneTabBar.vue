<script setup lang="ts">
import { Inbox, MessageSquare, Monitor } from "lucide-vue-next";

/**
 * The phone home's three tabs: Needs you (with its count), Sessions and Machines. A floating glass bar on iPhone, a
 * flat Material bar with a pill behind the current icon on Android. Tapping the current tab again scrolls it to the
 * top, as the phones do.
 */
export type PhoneTab = "needs-you" | "sessions" | "machines";

defineProps<{ tab: PhoneTab; needsYou: number }>();
const emit = defineEmits<{ (event: "select", tab: PhoneTab): void }>();

const tabs = [
  { id: "needs-you" as const, label: "Needs you", icon: Inbox },
  { id: "sessions" as const, label: "Sessions", icon: MessageSquare },
  { id: "machines" as const, label: "Machines", icon: Monitor },
];
</script>

<template>
  <nav
    class="ph-tabbar ph-glass"
    aria-label="Phone sections"
  >
    <button
      v-for="item in tabs"
      :key="item.id"
      type="button"
      class="ph-tab"
      :class="{ 'ph-tab--on': tab === item.id }"
      :aria-current="tab === item.id ? 'page' : undefined"
      :data-testid="`phone-tab-${item.id}`"
      @click="emit('select', item.id)"
    >
      <span class="ph-tab__ic">
        <component
          :is="item.icon"
          :stroke-width="2"
          aria-hidden="true"
        />
        <span
          v-if="item.id === 'needs-you' && needsYou > 0"
          class="ph-badge ph-badge--amber"
        >{{ needsYou }}</span>
      </span>
      <span>{{ item.label }}</span>
    </button>
  </nav>
</template>
