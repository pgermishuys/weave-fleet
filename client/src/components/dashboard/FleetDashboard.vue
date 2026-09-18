<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "@tanstack/vue-router";
import { Download, LoaderCircle, Plus } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useRelativeTime } from "@/composables/use-relative-time";
import { useSessions } from "@/composables/use-sessions";
import type { SessionListItem } from "@/api/client";
import { useAppShellStore } from "@/stores/app-shell";
import { useHarnessSetupStore } from "@/stores/harness-setup";
import { useSessionsStore } from "@/stores/sessions";
import DashboardSessionRow from "./DashboardSessionRow.vue";
import RetentionFilter from "./RetentionFilter.vue";
import SummaryBar from "./SummaryBar.vue";

/** How many quiet sessions show before "Show more". */
const RECENT_LIMIT = 8;

const router = useRouter();
const sessionsStore = useSessionsStore();
const { retentionStatus, sessions: storeSessions } = storeToRefs(sessionsStore);
const now = useRelativeTime();
const { config } = storeToRefs(useAppShellStore());
const harnessSetup = useHarnessSetupStore();
const { noHarnessReason } = useEnabledHarnesses();
/** Until a harness is ready, say so here and offer setup (not in cloud mode, where it can't be set up from Fleet). */
const showHarnessBanner = computed(() => noHarnessReason.value !== null && !config.value.cloudMode);

const {
  isLoading,
  error,
  refetch,
} = useSessions({ retentionStatus });

// Top-level sessions only; subagents show under their parent in the session itself.
const sessions = computed(() =>
  storeSessions.value.filter((session) => {
    if (session.parentSessionId || session.isHidden) return false;
    return retentionStatus.value === "all" || session.retentionStatus === retentionStatus.value;
  }));

function updatedAt(session: SessionListItem): number {
  const time = session.session.time;
  const value = time?.updated ?? time?.created ?? 0;
  return typeof value === "number" ? value : Date.parse(value) || 0;
}

const byRecent = computed(() => [...sessions.value].sort((a, b) => updatedAt(b) - updatedAt(a)));

/** Waiting on an answer, or stopped with an error: the user has to act. */
const needsYou = computed(() =>
  byRecent.value.filter((session) => session.sessionStatus === "waiting_input" || session.sessionStatus === "error"));

const working = computed(() => byRecent.value.filter((session) => session.sessionStatus === "active"));

const recent = computed(() =>
  byRecent.value.filter((session) => !["waiting_input", "error", "active"].includes(session.sessionStatus)));

const showAllRecent = shallowRef(false);
const visibleRecent = computed(() => (showAllRecent.value ? recent.value : recent.value.slice(0, RECENT_LIMIT)));

const isEmpty = computed(() => !isLoading.value && sessions.value.length === 0);

const greeting = computed(() => {
  const hour = new Date(now.value).getHours();
  if (hour < 5) return "Working late";
  if (hour < 12) return "Good morning";
  if (hour < 18) return "Good afternoon";
  return "Good evening";
});

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

const statusLine = computed(() => {
  const parts: string[] = [];
  if (needsYou.value.length > 0) parts.push(`${plural(needsYou.value.length, "session needs", "sessions need")} you`);
  if (working.value.length > 0) {
    parts.push(parts.length > 0
      ? `${working.value.length} working`
      : `${plural(working.value.length, "session", "sessions")} working`);
  }
  if (parts.length === 0) return "All quiet. Nothing needs you right now.";
  return `${parts.join(", ")}.`;
});

function handleSessionSelect(session: SessionListItem): void {
  void router.navigate({
    to: "/sessions/$id",
    params: { id: session.session.id },
    search: {
      instanceId: session.instanceId,
      parentSessionId: undefined,
    },
  });
}

function handleSessionChanged(): void {
  void refetch();
}

function handleNewSession(): void {
  void router.navigate({
    to: "/sessions/new",
    search: {
      projectId: undefined,
      source: undefined,
    },
  });
}
</script>

<template>
  <section
    class="dashboard"
    aria-label="Fleet dashboard"
  >
    <header class="dashboard__head">
      <div class="dashboard__heading">
        <h1 class="dashboard__title">
          {{ greeting }}
        </h1>
        <p class="dashboard__status">
          {{ statusLine }}
        </p>
      </div>

      <Button
        data-testid="new-session-button"
        size="sm"
        @click="handleNewSession"
      >
        <Plus class="h-4 w-4" />
        New session
      </Button>
    </header>

    <div
      v-if="showHarnessBanner"
      class="dashboard__harness-banner"
      data-testid="harness-setup-banner"
    >
      <div class="dashboard__harness-banner-icon">
        <Download
          :size="17"
          aria-hidden="true"
        />
      </div>
      <p class="dashboard__harness-banner-text">
        <strong>No harness is ready yet</strong>
        Fleet runs sessions through a harness such as OpenCode or Claude Code. Install one to start.
      </p>
      <Button
        size="sm"
        data-testid="harness-setup-banner-open"
        @click="harnessSetup.open('harnesses')"
      >
        Set up a harness
      </Button>
    </div>

    <SummaryBar />

    <p
      v-if="error"
      class="dashboard__error"
      role="alert"
    >
      {{ error }}
    </p>

    <div
      v-if="isLoading && sessions.length === 0"
      class="dashboard__placeholder"
    >
      <LoaderCircle
        class="h-4 w-4 animate-spin"
        aria-hidden="true"
      />
      Loading sessions…
    </div>

    <div
      v-else-if="isEmpty"
      data-testid="empty-state"
      class="dashboard__empty"
    >
      <h2 class="dashboard__empty-title">
        {{ retentionStatus === "archived" ? "No archived sessions" : "No sessions yet" }}
      </h2>
      <p class="dashboard__empty-copy">
        Start a session to put an agent to work. It shows up here while it runs, and when it needs you.
      </p>
      <div class="dashboard__empty-actions">
        <Button
          size="sm"
          @click="handleNewSession"
        >
          <Plus class="h-4 w-4" />
          New session
        </Button>
        <RetentionFilter v-model="retentionStatus" />
      </div>
    </div>

    <template v-else>
      <section
        v-if="needsYou.length > 0"
        class="dashboard__section dashboard__section--attention"
        aria-labelledby="dashboard-needs-you"
      >
        <h2
          id="dashboard-needs-you"
          class="dashboard__section-title"
        >
          Needs you <span class="dashboard__section-count">{{ needsYou.length }}</span>
        </h2>
        <div class="dashboard__rows">
          <DashboardSessionRow
            v-for="session in needsYou"
            :key="session.session.id"
            :session="session"
            :now="now"
            @select="handleSessionSelect"
            @changed="handleSessionChanged"
          />
        </div>
      </section>

      <section
        v-if="working.length > 0"
        class="dashboard__section"
        aria-labelledby="dashboard-working"
      >
        <h2
          id="dashboard-working"
          class="dashboard__section-title"
        >
          Working <span class="dashboard__section-count">{{ working.length }}</span>
        </h2>
        <div class="dashboard__rows">
          <DashboardSessionRow
            v-for="session in working"
            :key="session.session.id"
            :session="session"
            :now="now"
            @select="handleSessionSelect"
            @changed="handleSessionChanged"
          />
        </div>
      </section>

      <section
        class="dashboard__section"
        aria-labelledby="dashboard-recent"
      >
        <div class="dashboard__section-head">
          <h2
            id="dashboard-recent"
            class="dashboard__section-title"
          >
            Recent <span class="dashboard__section-count">{{ recent.length }}</span>
          </h2>
          <RetentionFilter v-model="retentionStatus" />
        </div>
        <div
          v-if="recent.length > 0"
          class="dashboard__rows"
        >
          <DashboardSessionRow
            v-for="session in visibleRecent"
            :key="session.session.id"
            :session="session"
            :now="now"
            @select="handleSessionSelect"
            @changed="handleSessionChanged"
          />
        </div>
        <p
          v-else
          class="dashboard__none"
        >
          Nothing else here.
        </p>
        <button
          v-if="recent.length > RECENT_LIMIT"
          type="button"
          class="dashboard__more"
          @click="showAllRecent = !showAllRecent"
        >
          {{ showAllRecent ? "Show fewer" : `Show ${recent.length - RECENT_LIMIT} more` }}
        </button>
      </section>
    </template>
  </section>
</template>

<style scoped>
.dashboard {
  display: flex;
  flex-direction: column;
  gap: 24px;
  width: 100%;
  max-width: 1040px;
  margin: 0 auto;
}

.dashboard__head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: 12px;
}

.dashboard__heading {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.dashboard__title {
  margin: 0;
  color: var(--text);
  font-size: 22px;
  font-weight: 600;
  letter-spacing: -0.01em;
  line-height: 1.2;
}

.dashboard__status {
  margin: 0;
  color: var(--muted);
  font-size: 13px;
}

.dashboard__harness-banner {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 14px;
  padding: 14px 16px;
  border: 1px solid color-mix(in srgb, var(--accent) 35%, transparent);
  border-radius: var(--radius-card);
  background: var(--accent-dim);
}

.dashboard__harness-banner-icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 34px;
  height: 34px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
}

.dashboard__harness-banner-text {
  flex: 1 1 280px;
  margin: 0;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.5;
}

.dashboard__harness-banner-text strong {
  display: block;
  color: var(--text);
  font-size: 14px;
  font-weight: 600;
}

.dashboard__error {
  margin: 0;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--error) 8%, transparent);
  color: var(--error);
  font-size: 13px;
}

.dashboard__placeholder {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 48px 16px;
  color: var(--muted);
  font-size: 13px;
}

.dashboard__empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 56px 24px;
  border: 1px dashed var(--border);
  border-radius: var(--radius-card);
  text-align: center;
}

.dashboard__empty-title {
  margin: 0;
  color: var(--text);
  font-size: 15px;
  font-weight: 600;
}

.dashboard__empty-copy {
  max-width: 420px;
  margin: 0;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.5;
}

.dashboard__empty-actions {
  display: flex;
  gap: 8px;
  margin-top: 8px;
}

.dashboard__section {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.dashboard__section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.dashboard__section-title {
  display: flex;
  align-items: baseline;
  gap: 6px;
  margin: 0;
  padding: 0 10px;
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}

/* What needs the user sits on a faint wash of the waiting colour. */
.dashboard__section--attention {
  padding: 10px 0 4px;
  border: 1px solid color-mix(in srgb, var(--status-waiting) 22%, transparent);
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--status-waiting) 5%, transparent);
}

.dashboard__section--attention .dashboard__rows {
  padding: 0 4px;
}

.dashboard__section--attention .dashboard__section-title {
  padding: 0 14px;
  color: var(--status-waiting);
}

.dashboard__section-count {
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  opacity: 0.8;
}

.dashboard__rows {
  display: flex;
  flex-direction: column;
}

.dashboard__none {
  margin: 0;
  padding: 8px 10px;
  color: var(--muted);
  font-size: 13px;
}

.dashboard__more {
  align-self: flex-start;
  margin-left: 4px;
  padding: 4px 6px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  cursor: pointer;
}

.dashboard__more:hover {
  color: var(--text);
}
</style>
