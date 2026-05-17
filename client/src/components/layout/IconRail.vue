<script setup lang="ts">
import type { Component } from "vue";
import type { SidebarRail } from "@/stores/sidebar";
import type { PluginConnectionStatus } from "@/plugins/types";
import { computed, onMounted, onUnmounted, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import { BarChart3, LayoutGrid, MessageSquare, Puzzle, Settings } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import weaveLogo from "@/assets/weave_logo.png";
import { apiFetch } from "@/lib/api-client";
import type { PluginCatalogResponse } from "@/lib/api-types";
import { usePluginRuntime } from "@/plugins/composable";
import { getSidebarViews } from "@/plugins/slots";
import { useSidebarStore } from "@/stores/sidebar";

type RailItemId = SidebarRail | string;

interface RailItem {
  id: RailItemId;
  label: string;
  icon: Component;
  to?: string;
  status?: PluginConnectionStatus;
  badge?: number;
}

const ALL_TOP_ITEMS: readonly RailItem[] = [
  { id: "board", label: "Board", icon: LayoutGrid, to: "/board" },
  { id: "sessions", label: "Sessions", icon: MessageSquare, to: "/" },
];

const bottomItems: readonly RailItem[] = [
  { id: "marketplace", label: "Plugins", icon: Puzzle },
  { id: "analytics", label: "Analytics", icon: BarChart3, to: "/analytics" },
  { id: "settings", label: "Settings", icon: Settings, to: "/settings" },
];

const sidebarStore = useSidebarStore();
const { activeRail } = storeToRefs(sidebarStore);
const router = useRouter();
const pluginRuntime = usePluginRuntime();
const pathname = useLocation({
  select: (location) => location.pathname,
});

const pluginSidebarViews = computed(() => getSidebarViews(pluginRuntime.manifests.value));
// Preserve plugin rail badge wiring for future use, but keep it hidden for now.
const showPluginRailBadges = false;

const pluginItems = computed<readonly RailItem[]>(() => {
  return pluginSidebarViews.value.map((item) => {
    const pluginStatus = pluginRuntime.getStatus(item.pluginId);
    const badge = showPluginRailBadges
      ? getStatusBadgeCount(pluginStatus?.actions?.length ?? 0)
      : undefined;

    return {
      id: item.viewId,
      label: item.label,
      icon: item.icon,
      to: item.defaultPath,
      status: pluginStatus?.status ?? "disconnected",
      badge,
    };
  });
});

const topItems = computed<readonly RailItem[]>(() => ALL_TOP_ITEMS);

const currentRouteRail = computed<RailItemId | null>(() => {
  if (pathname.value === "/board") {
    return "board";
  }

  if (pathname.value === "/analytics" || pathname.value.startsWith("/analytics/")) {
    return "analytics";
  }

  if (pathname.value === "/settings") {
    return "settings";
  }

  if (pathname.value === "/") {
    return "sessions";
  }

  // Session detail pages (/sessions/:id) — preserve current sessions rail
  if (pathname.value.startsWith("/sessions/")) {
    const current = activeRail.value;

    if (current === "sessions") {
      return current;
    }

    return "sessions";
  }

  const matchingPluginView = pluginSidebarViews.value.find((item) => {
    return pathname.value === item.defaultPath
      || pathname.value.startsWith(`${item.defaultPath}/`);
  });

  if (matchingPluginView) {
    return matchingPluginView.viewId;
  }

  return null;
});

watch(
  currentRouteRail,
  (rail) => {
    if (rail && isSidebarRail(rail)) {
      sidebarStore.setActiveRail(rail);
    }
  },
  { immediate: true },
);

let statusPollInterval: number | undefined;

onMounted(() => {
  void loadPluginStatuses();
  statusPollInterval = window.setInterval(() => {
    void loadPluginStatuses();
  }, 30_000);
});

onUnmounted(() => {
  if (statusPollInterval !== undefined) {
    window.clearInterval(statusPollInterval);
  }
});

function getStatusBadgeCount(count: number): number | undefined {
  return count > 0 ? count : undefined;
}

function isSidebarRail(value: RailItemId): value is SidebarRail {
  return ["board", "sessions", "analytics", "github", "marketplace", "settings"].includes(value);
}

async function loadPluginStatuses(): Promise<void> {
  pluginRuntime.setLoading(true);

  try {
    const response = await apiFetch("/api/plugins");

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const data = (await response.json()) as PluginCatalogResponse;
    pluginRuntime.setStatuses(data.statuses);
    pluginRuntime.setError(undefined);
  } catch (error) {
    pluginRuntime.setError(error instanceof Error ? error.message : String(error));
  } finally {
    pluginRuntime.setLoading(false);
  }
}

function handleSelect(item: RailItem): void {
  if (isSidebarRail(item.id)) {
    sidebarStore.setActiveRail(item.id);
  }

  if (item.to) {
    void router.navigate({ to: item.to });
  }
}
</script>

<template>
  <aside
    class="rail"
    aria-label="Primary navigation"
  >
    <div
      class="rail-logo"
      aria-hidden="true"
    >
      <img
        :src="weaveLogo"
        alt=""
        class="rail-logo-image"
      >
    </div>

    <nav
      class="rail-nav"
      aria-label="App sections"
    >
      <button
        v-for="item in topItems"
        :key="item.id"
        type="button"
        class="rail-item"
        :class="{ active: activeRail === item.id }"
        :data-tooltip="item.label"
        :aria-label="item.label"
        @click="handleSelect(item)"
      >
        <component
          :is="item.icon"
          :size="18"
          aria-hidden="true"
        />
      </button>

      <div class="rail-divider" />

      <button
        v-for="item in pluginItems"
        :key="item.id"
        type="button"
        class="rail-item"
        :class="{ active: activeRail === item.id }"
        :data-tooltip="item.label"
        :aria-label="item.label"
        @click="handleSelect(item)"
      >
        <component
          :is="item.icon"
          :size="18"
          aria-hidden="true"
        />
        <span
          v-if="item.badge"
          class="rail-badge"
          aria-hidden="true"
        >
          {{ item.badge }}
        </span>
      </button>

      <div class="rail-divider rail-bottom-divider" />

      <div class="rail-bottom">
        <button
          v-for="item in bottomItems"
          :key="item.id"
          type="button"
          class="rail-item"
          :class="{ active: activeRail === item.id }"
          :data-tooltip="item.label"
          :aria-label="item.label"
          @click="handleSelect(item)"
        >
          <component
            :is="item.icon"
            :size="18"
            aria-hidden="true"
          />
        </button>
      </div>
    </nav>
  </aside>
</template>

<style scoped>
.rail {
  width: 48px;
  min-width: 48px;
  background: var(--rail-bg);
  border-right: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 8px 0;
}

.rail-nav {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  width: 100%;
}

.rail-item {
  width: 40px;
  height: 40px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: var(--radius-btn);
  color: var(--muted);
  cursor: pointer;
  position: relative;
  font-size: 15px;
  transition: color 0.25s ease, background-color 0.25s ease;
  margin-bottom: 2px;
  border: 0;
  border-left: 3px solid transparent;
  background: transparent;
  padding: 0;
}

.rail-item:hover {
  color: #a1a1aa;
}

.rail-item.active {
  color: #fff;
  border-left-color: var(--accent);
  background: rgba(255, 255, 255, 0.04);
}

.rail-logo {
  width: 32px;
  height: 32px;
  display: flex;
  align-items: center;
  justify-content: center;
  margin-bottom: 4px;
}

.rail-logo-image {
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.rail-divider {
  width: 24px;
  height: 1px;
  background: var(--border);
  margin: 4px 0;
}

.rail-bottom-divider {
  margin-top: auto;
}

.rail-bottom {
  display: flex;
  flex-direction: column;
  align-items: center;
}

.rail-badge {
  position: absolute;
  top: 4px;
  right: 2px;
  background: var(--error);
  color: #fff;
  font-size: 9px;
  font-weight: 700;
  min-width: 14px;
  height: 14px;
  border-radius: 7px;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0 3px;
}

.rail-item::after {
  content: attr(data-tooltip);
  position: absolute;
  left: 52px;
  top: 50%;
  transform: translate(0, -50%);
  background: #27272a;
  color: var(--text);
  font-size: 11px;
  padding: 4px 10px;
  border-radius: 4px;
  white-space: nowrap;
  pointer-events: none;
  opacity: 0;
  transition: opacity 0.25s ease, transform 0.25s ease;
  z-index: 100;
}

.rail-item:hover::after {
  opacity: 1;
  transform: translate(4px, -50%);
}

</style>
