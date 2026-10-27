<script setup lang="ts">
import type { PluginConnectionStatus, FleetPluginStatus } from "@/plugins/types";
import { computed, onMounted, onUnmounted, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import { Bug, CircleHelp, Smartphone, Sparkles } from "lucide-vue-next";
import { useIsMobileNav } from "@/composables/use-media-query";
import { storeToRefs } from "pinia";
import weaveLogo from "@/assets/weave_logo.png";
import { api } from "@/api/client";
import type { PluginCatalogResponse } from "@/api/client";
import { usePluginRuntime } from "@/plugins/composable";
import "@/components/layout/core-rails";
import RailButton from "@/components/layout/RailButton.vue";
import { isSidebarRail, rails, type RailDefinition } from "@/lib/rails";
import { useBoardFeature } from "@/composables/use-board-feature";
import { useJobsFeature } from "@/composables/use-jobs-feature";
import { useMissionControlFeature } from "@/composables/use-mission-control-feature";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { useWorkflowsStore } from "@/stores/workflows";
import { useSidebarStore } from "@/stores/sidebar";
import { useProblemReportStore } from "@/stores/problem-report";
import { useWhatsNew } from "@/composables/use-whats-new";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

interface RailItem extends RailDefinition {
  /** The feature behind it is switched off: drawn dimmed, with a marker. */
  isOff?: boolean;
  status?: PluginConnectionStatus;
  badge?: number;
}

const sidebarStore = useSidebarStore();
const { activeRail } = storeToRefs(sidebarStore);
const router = useRouter();
const pluginRuntime = usePluginRuntime();
useBoardFeature(); // starts loading the preferences the Board rail's switch reads
useJobsFeature(); // and the Jobs rail's
useMissionControlFeature(); // and the Fleet rail's
const { isWorkflowsEnabled } = useWorkflowsFeature();
const workflowsStore = useWorkflowsStore();
const problemReport = useProblemReportStore();
const { openWhatsNew } = useWhatsNew();
// On a phone, the phone view: what needs you on every machine.
const isMobileNav = useIsMobileNav();
const pathname = useLocation({
  select: (location) => location.pathname,
});

// Preserve plugin rail badge wiring for future use, but keep it hidden for now.
const showPluginRailBadges = false;

const visibleRails = computed<readonly RailItem[]>(() =>
  rails.value
    .filter((rail) => rail.icon && !rail.hidden?.())
    .map((rail) => {
      const isOff = rail.off?.() ?? false;
      if (!rail.pluginId) return { ...rail, isOff };

      const pluginStatus = pluginRuntime.getStatus(rail.pluginId);
      const badge = showPluginRailBadges
        ? getStatusBadgeCount(pluginStatus?.actions?.length ?? 0)
        : undefined;

      return { ...rail, isOff, status: pluginStatus?.status ?? "disconnected", badge };
    }));

const topItems = computed(() => visibleRails.value.filter((rail) => rail.placement === "top"));

const secondaryItems = computed(() => visibleRails.value.filter((rail) => rail.placement === "secondary"));

// Plugin rails sit above Fleet's own bottom rails (Plugins, Settings).
const bottomItems = computed(() => [
  ...visibleRails.value.filter((rail) => rail.placement === "plugin"),
  ...visibleRails.value.filter((rail) => rail.placement === "bottom"),
]);

// Workflows exist only while they're switched on in Settings.
watch(isWorkflowsEnabled, (enabled) => {
  if (enabled) void workflowsStore.ensureLoaded();
}, { immediate: true });

const currentRouteRail = computed<string | null>(() => {
  if (pathname.value === "/board") {
    return "board";
  }

  if (pathname.value === "/analytics" || pathname.value.startsWith("/analytics/")) {
    return "analytics";
  }

  if (pathname.value === "/automations" || pathname.value.startsWith("/automations/")) {
    return "automations";
  }

  if (pathname.value === "/workflows") {
    return "workflows";
  }

  if (pathname.value === "/jobs") {
    return "jobs";
  }

  if (pathname.value === "/fleet") {
    return "fleet";
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

  const matchingPluginRail = rails.value.find((rail) => {
    return rail.pluginId && rail.to
      && (pathname.value === rail.to || pathname.value.startsWith(`${rail.to}/`));
  });

  if (matchingPluginRail) {
    return matchingPluginRail.id;
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

async function loadPluginStatuses(): Promise<void> {
  pluginRuntime.setLoading(true);

  try {
    const { data, error: apiError, response } = await api.GET("/api/plugins");

    if (apiError || !data) {
      throw new Error(`HTTP ${response.status}`);
    }

    pluginRuntime.setStatuses((data as PluginCatalogResponse).statuses as unknown as FleetPluginStatus[]);
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
    :class="{ 'icon-only': isMobileNav }"
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
      <RailButton
        v-for="item in topItems"
        :key="item.id"
        :item="item"
        :active="activeRail === item.id"
        :icon-only="isMobileNav"
        @select="handleSelect"
      />

      <div class="rail-divider" />

      <RailButton
        v-for="item in secondaryItems"
        :key="item.id"
        :item="item"
        :active="activeRail === item.id"
        :icon-only="isMobileNav"
        @select="handleSelect"
      />

      <div class="rail-divider rail-bottom-divider" />

      <div class="rail-bottom">
        <RailButton
          v-for="item in bottomItems"
          :key="item.id"
          :item="item"
          :active="activeRail === item.id"
          :icon-only="isMobileNav"
          @select="handleSelect"
        />

        <button
          v-if="isMobileNav"
          type="button"
          class="rail-item icon-only"
          data-tooltip="Phone view"
          aria-label="Phone view"
          data-testid="rail-phone-view"
          @click="router.navigate({ to: '/phone' })"
        >
          <Smartphone
            :size="18"
            aria-hidden="true"
          />
        </button>

        <DropdownMenu>
          <DropdownMenuTrigger as-child>
            <button
              type="button"
              class="rail-item"
              :class="{ 'icon-only': isMobileNav }"
              :data-tooltip="isMobileNav ? 'Help' : undefined"
              aria-label="Help"
              data-testid="rail-help"
            >
              <CircleHelp
                :size="18"
                aria-hidden="true"
              />
              <span
                v-if="!isMobileNav"
                class="rail-label"
              >Help</span>
            </button>
          </DropdownMenuTrigger>
          <DropdownMenuContent
            side="right"
            align="end"
            class="w-56"
          >
            <DropdownMenuItem
              data-testid="rail-report-problem"
              @select="problemReport.show({ from: 'help' })"
            >
              <Bug class="size-3.5" />
              Report a problem…
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem
              data-testid="rail-whats-new"
              @select="openWhatsNew()"
            >
              <Sparkles class="size-3.5" />
              What's new
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </nav>
  </aside>
</template>

<style scoped>
.rail {
  width: 74px;
  min-width: 74px;
  background: transparent;
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 8px 0;
}

/* The phone drawer keeps the narrow icon-only rail so the sessions column keeps its room. */
.rail.icon-only {
  width: 48px;
  min-width: 48px;
}

.rail-nav {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  width: 100%;
}

.rail-bottom {
  display: flex;
  flex-direction: column;
  align-items: center;
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
</style>

<style>
/* Unscoped: RailButton renders the buttons, and Help below renders its own with the same classes. */
.rail-item {
  width: 68px;
  height: 50px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 3px;
  border-radius: var(--radius-btn);
  color: var(--muted);
  cursor: pointer;
  position: relative;
  transition: background var(--transition), color var(--transition), border-color var(--transition);
  margin-bottom: 2px;
  border: 1px solid transparent;
  background: transparent;
  padding: 0;
}

.rail-item.icon-only {
  width: 40px;
  height: 40px;
}

.rail-item:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.rail-item.active {
  color: var(--text);
  background: color-mix(in srgb, var(--text) 9%, transparent);
}

.rail-glyph {
  position: relative;
  display: flex;
}

.rail-label {
  font-size: 10px;
  font-weight: 500;
  line-height: 1;
  white-space: nowrap;
}

/* Switched off: icon and label dim, not the button, so the hover and active backgrounds stay as they are. */
.rail-item.off .rail-glyph,
.rail-item.off .rail-label {
  opacity: 0.55;
}

.rail-off-dot {
  position: absolute;
  top: -3px;
  right: -5px;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--idle);
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
  border-radius: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0 3px;
}

/* A tooltip only where there's no label under the icon. */
.rail-item[data-tooltip]::after {
  content: attr(data-tooltip);
  position: absolute;
  left: 52px;
  top: 50%;
  transform: translate(0, -50%);
  background: var(--color-popover);
  color: var(--color-popover-foreground);
  font-size: 11px;
  padding: 4px 10px;
  border-radius: 0;
  white-space: nowrap;
  pointer-events: none;
  opacity: 0;
  transition: opacity var(--transition), transform var(--transition);
  z-index: 100;
}

.rail-item[data-tooltip]:hover::after {
  opacity: 1;
  transform: translate(4px, -50%);
}
</style>
