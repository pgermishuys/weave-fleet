<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { useLocation } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import CommandPalette from "@/components/CommandPalette.vue";
import TauriUpdateDialog from "@/components/TauriUpdateDialog.vue";
import CenterContent from "@/components/layout/CenterContent.vue";
import CollapsedRightRail from "@/components/layout/CollapsedRightRail.vue";
import ContextPanel from "@/components/layout/ContextPanel.vue";
import IconRail from "@/components/layout/IconRail.vue";
import RightPanel from "@/components/layout/RightPanel.vue";
import { useCommands } from "@/composables/use-commands";
import { useSessionTodos } from "@/composables/use-session-todos";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";

useCommands();
useWeaveSocket();

const pathname = useLocation({
  select: (location) => location.pathname,
});
const sidebarStore = useSidebarStore();
const sessionsStore = useSessionsStore();

const { panelCollapsed, rightPanelCollapsed } = storeToRefs(sidebarStore);
const { activeSessionId, sessions } = storeToRefs(sessionsStore);

const activeSession = computed(() => {
  return sessions.value.find((session) => session.session.id === activeSessionId.value) ?? null;
});
const isSettingsRoute = computed(() => pathname.value.startsWith("/settings"));

const activeInstanceId = computed(() => activeSession.value?.instanceId ?? "");
const { todos } = useSessionTodos(
  computed(() => activeSessionId.value ?? ""),
  activeInstanceId,
);

const previousTodoCount = shallowRef(0);

watch(
  activeSessionId,
  (nextSessionId, previousSessionId) => {
    if (nextSessionId && !previousSessionId) {
      sidebarStore.setRightPanelCollapsed(false);
    }
  },
  { flush: "post" },
);

watch(
  [activeSessionId, () => todos.value.length] as const,
  ([nextSessionId, nextTodoCount], [previousSessionId, previousTodoCountValue]) => {
    if (!nextSessionId) {
      previousTodoCount.value = 0;
      return;
    }

    if (nextSessionId !== previousSessionId) {
      previousTodoCount.value = nextTodoCount;
      return;
    }

    const hasNewTodo = nextTodoCount > (previousTodoCountValue ?? 0);

    if (hasNewTodo) {
      sidebarStore.setRightPanelCollapsed(false);
    }

    previousTodoCount.value = nextTodoCount;
  },
  { flush: "post", immediate: true },
);

function handleExpandRightPanel(): void {
  sidebarStore.setRightPanelCollapsed(false);
}
</script>

<template>
  <div class="app">
    <div class="main">
      <IconRail />
      <ContextPanel v-if="!panelCollapsed" />
      <CenterContent>
        <slot />
      </CenterContent>

      <Transition v-if="!isSettingsRoute" name="right-panel-swap" mode="out-in">
        <CollapsedRightRail
          v-if="rightPanelCollapsed"
          key="collapsed"
          :todos="todos"
          @expand="handleExpandRightPanel"
        />

        <RightPanel v-else key="expanded" />
      </Transition>
    </div>

    <CommandPalette />
    <TauriUpdateDialog />
  </div>
</template>

<style scoped>
.app {
  display: flex;
  flex-direction: column;
  height: 100vh;
}

.main {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.right-panel-swap-enter-active,
.right-panel-swap-leave-active {
  transition: opacity 0.16s ease, transform 0.16s ease;
}

.right-panel-swap-enter-from,
.right-panel-swap-leave-to {
  opacity: 0;
  transform: translateX(10px);
}
</style>
