<script setup lang="ts">
import { Inbox, MessageSquare, Monitor } from "lucide-vue-next";

/** The phone home's three tabs: Needs you (with its count), Sessions and Machines. */
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
    class="tabbar"
    aria-label="Phone sections"
  >
    <button
      v-for="item in tabs"
      :key="item.id"
      type="button"
      class="tabbar__tab"
      :class="{ 'tabbar__tab--on': tab === item.id }"
      :aria-current="tab === item.id ? 'page' : undefined"
      :data-testid="`phone-tab-${item.id}`"
      @click="emit('select', item.id)"
    >
      <span class="tabbar__icon">
        <component
          :is="item.icon"
          :size="20"
          aria-hidden="true"
        />
        <span
          v-if="item.id === 'needs-you' && needsYou > 0"
          class="tabbar__badge"
        >{{ needsYou }}</span>
      </span>
      {{ item.label }}
    </button>
  </nav>
</template>

<style scoped>
.tabbar {
  display: flex;
  flex: none;
  padding-bottom: env(safe-area-inset-bottom);
  border-top: 1px solid var(--border);
  background: var(--panel-bg);
}

.tabbar__tab {
  display: grid;
  flex: 1;
  justify-items: center;
  gap: 2px;
  min-height: 52px;
  padding: 6px 0;
  border: 0;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 11px;
  cursor: pointer;
}

.tabbar__tab--on {
  color: var(--accent);
}

.tabbar__icon {
  position: relative;
}

.tabbar__badge {
  position: absolute;
  top: -5px;
  right: -10px;
  min-width: 16px;
  padding: 0 4px;
  border-radius: 999px;
  background: var(--idle);
  color: #fff;
  font-size: 10px;
  font-weight: 600;
  line-height: 16px;
  text-align: center;
}
</style>
