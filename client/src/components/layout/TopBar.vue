<script setup lang="ts">
import { computed } from "vue";
import { Bell } from "lucide-vue-next";
import { useLocation } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { useAppShellStore } from "@/stores/app-shell";
import { useSessionsStore } from "@/stores/sessions";

interface BreadcrumbItem {
  id: string;
  label: string;
  current?: boolean;
}

const sessionsStore = useSessionsStore();
const appShellStore = useAppShellStore();

const { sessions, activeSessionId } = storeToRefs(sessionsStore);
const { config, user } = storeToRefs(appShellStore);

const pathname = useLocation({
  select: (location) => location.pathname,
});

const activeSession = computed(() =>
  sessions.value.find((session) => session.session.id === activeSessionId.value) ?? null,
);

const routeLabel = computed(() => {
  if (pathname.value === "/") {
    return "Sessions";
  }

  const segments = pathname.value
    .split("/")
    .filter(Boolean)
    .map((segment) =>
      segment
        .split(/[-_]/)
        .filter(Boolean)
        .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
        .join(" "),
    );

  return segments.at(-1) ?? "Workspace";
});

const breadcrumbItems = computed<BreadcrumbItem[]>(() => {
  const items: BreadcrumbItem[] = [{ id: "route", label: routeLabel.value }];

  if (activeSession.value?.projectName) {
    items.push({
      id: "project",
      label: activeSession.value.projectName,
    });
  }

  if (activeSession.value?.session.title) {
    items.push({
      id: "session",
      label: activeSession.value.session.title,
    });
  }

  return items.map((item, index) => ({
    ...item,
    current: index === items.length - 1,
  }));
});

const statusLabel = computed(() => {
  switch (activeSession.value?.sessionStatus) {
    case "idle":
      return "Idle";
    case "completed":
      return "Complete";
    case "stopped":
    case "disconnected":
      return "Stopped";
    case "error":
      return "Error";
    case "waiting_input":
      return "Waiting for input";
    case "active":
    default:
      return "Running";
  }
});

const statusClassName = computed(() => {
  switch (activeSession.value?.sessionStatus) {
    case "idle":
      return "status-pill--idle";
    case "completed":
      return "status-pill--complete";
    case "error":
      return "status-pill--error";
    case "waiting_input":
      return "status-pill--waiting";
    case "stopped":
    case "disconnected":
      return "status-pill--stopped";
    case "active":
    default:
      return "status-pill--running";
  }
});

const avatarInitials = computed(() => {
  const seed = user.value?.name?.trim() || user.value?.email?.trim() || config.value.appName;

  return seed
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part.charAt(0).toUpperCase())
    .join("") || "WF";
});

const showNotifications = computed(() => config.value.cloudMode);
const showUserAvatar = computed(() => config.value.authEnabled);
</script>

<template>
  <header class="topbar flex items-center justify-between pr-4">
    <nav
      class="breadcrumb flex min-w-0 items-center"
      aria-label="Breadcrumb"
    >
      <span class="shrink-0 text-[13px] font-medium text-[var(--text)]">Weave Fleet</span>

      <template
        v-for="item in breadcrumbItems"
        :key="item.id"
      >
        <span
          class="breadcrumb__separator"
          aria-hidden="true"
        >/</span>
        <span
          class="truncate transition-colors"
          :class="item.current ? 'text-[var(--text)]' : 'text-[var(--muted)]'"
          :aria-current="item.current ? 'page' : undefined"
        >
          {{ item.label }}
        </span>
      </template>
    </nav>

    <div class="flex items-center gap-3">
      <div
        class="status-pill"
        :class="statusClassName"
      >
        <span
          class="dot"
          :class="{ 'dot--pulse': statusLabel === 'Running' }"
        />
        <span>{{ statusLabel }}</span>
      </div>

      <button
        v-if="showNotifications"
        type="button"
        class="topbar__icon-button inline-flex h-8 w-8 items-center justify-center rounded-full border border-transparent text-[var(--muted)] transition-colors hover:border-[var(--border)] hover:bg-white/5 hover:text-[var(--text)]"
        aria-label="Notifications"
      >
        <Bell class="h-4 w-4" />
      </button>

      <Avatar
        v-if="showUserAvatar"
        class="h-8 w-8 border border-[var(--border)] bg-[var(--panel-bg)]"
      >
        <AvatarFallback class="bg-[rgba(124,58,237,0.15)] text-[11px] font-semibold tracking-[0.08em] text-[var(--text)]">
          {{ avatarInitials }}
        </AvatarFallback>
      </Avatar>
    </div>
  </header>
</template>

<style scoped>
.topbar {
  height: 48px;
  min-height: 48px;
  background: var(--panel-bg);
  border-bottom: 1px solid var(--border);
  z-index: 10;
}

.breadcrumb {
  gap: 6px;
  padding-left: 16px;
  font-size: 13px;
  color: var(--muted);
}

.breadcrumb__separator {
  color: var(--muted);
}

.status-pill {
  display: flex;
  align-items: center;
  gap: 6px;
  border-radius: 20px;
  padding: 4px 12px 4px 8px;
  font-size: 12px;
  line-height: 1;
}

.status-pill--running {
  background: rgba(34, 197, 94, 0.1);
  border: 1px solid rgba(34, 197, 94, 0.2);
  color: var(--running);
}

.status-pill--idle {
  background: rgba(245, 158, 11, 0.1);
  border: 1px solid rgba(245, 158, 11, 0.2);
  color: #f59e0b;
}

.status-pill--complete {
  background: rgba(56, 189, 248, 0.1);
  border: 1px solid rgba(56, 189, 248, 0.2);
  color: #38bdf8;
}

.dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: currentColor;
}

.dot--pulse {
  animation: pulse-dot 2s ease-in-out infinite;
}

.topbar__icon-button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

@keyframes pulse-dot {
  0%,
  100% {
    opacity: 1;
  }

  50% {
    opacity: 0.4;
  }
}
</style>
