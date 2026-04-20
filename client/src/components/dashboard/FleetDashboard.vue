<script setup lang="ts">
import { computed, onUnmounted } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "@tanstack/vue-router";
import { LoaderCircle, Plus } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { useActivityStream } from "@/composables/use-activity-stream";
import { useSessions } from "@/composables/use-sessions";
import type { SessionListItem } from "@/lib/api-types";
import { useSessionsStore } from "@/stores/sessions";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import RetentionFilter from "./RetentionFilter.vue";
import SessionCard from "./SessionCard.vue";
import SummaryBar from "./SummaryBar.vue";

const router = useRouter();
const sessionsStore = useSessionsStore();
const workspaceUiStore = useWorkspaceUiStore();
const activityStream = useActivityStream();
const { retentionStatus, sessions: storeSessions } = storeToRefs(sessionsStore);

const {
  isLoading,
  error,
  refetch,
} = useSessions({ retentionStatus });

const sessions = computed(() => {
  return storeSessions.value.filter((session) => {
    if (retentionStatus.value === "all") {
      return true;
    }

    return session.retentionStatus === retentionStatus.value;
  });
});

const isEmpty = computed(() => !isLoading.value && sessions.value.length === 0);

const sessionActivityEvents = [
  "session.created",
  "session.updated",
  "session.deleted",
  "session_archived",
  "session_unarchived",
  "session_deleted",
] as const;

function handleSessionActivity(): void {
  void refetch();
}

for (const eventType of sessionActivityEvents) {
  activityStream.on(eventType, handleSessionActivity);
}

onUnmounted(() => {
  for (const eventType of sessionActivityEvents) {
    activityStream.off(eventType, handleSessionActivity);
  }
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

</script>

<template>
  <section class="grid gap-6">
    <SummaryBar />

    <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight text-foreground">
          Fleet Dashboard
        </h1>
        <p class="text-sm text-muted-foreground">
          Monitor sessions, review retention, and jump into active work.
        </p>
      </div>

      <div class="flex flex-wrap items-center gap-3">
        <RetentionFilter v-model="retentionStatus" />

        <Button
          data-testid="new-session-button"
          @click="workspaceUiStore.openNewSessionDialog(null)"
        >
          <Plus class="h-4 w-4" />
          New Session
        </Button>
      </div>
    </div>

    <div
      v-if="error"
      class="rounded-lg border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
      role="alert"
    >
      {{ error }}
    </div>

    <div
      v-if="isLoading && sessions.length === 0"
      class="flex items-center justify-center rounded-xl border border-dashed border-border px-6 py-16"
    >
      <LoaderCircle class="h-5 w-5 animate-spin text-muted-foreground" />
      <span class="ml-2 text-sm text-muted-foreground">Loading sessions…</span>
    </div>

    <div
      v-else-if="isEmpty"
      data-testid="empty-state"
      class="rounded-xl border border-dashed border-border px-6 py-16 text-center"
    >
      <h2 class="text-lg font-semibold text-foreground">
        No sessions yet
      </h2>
      <p class="mt-2 text-sm text-muted-foreground">
        Create a session to start tracking fleet activity from the dashboard.
      </p>
    </div>

    <div
      v-else
      class="grid gap-4 md:grid-cols-2 xl:grid-cols-3"
    >
      <SessionCard
        v-for="session in sessions"
        :key="session.session.id"
        :session="session"
        @changed="handleSessionChanged"
        @select="handleSessionSelect"
      />
    </div>
  </section>
</template>
