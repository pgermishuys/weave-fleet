<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "@tanstack/vue-router";
import { LoaderCircle, Plus } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import type { SessionListItem } from "@/lib/api-types";
import { useSessions } from "@/composables/use-sessions";
import { useWorkspaces } from "@/composables/use-workspaces";
import { useSessionsStore } from "@/stores/sessions";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import SessionCard from "@/components/dashboard/SessionCard.vue";
import SummaryBar from "@/components/dashboard/SummaryBar.vue";
import WorkspaceToolbar from "./WorkspaceToolbar.vue";
import type { GroupBy, SortBy } from "./WorkspaceToolbar.vue";

const PREFS_KEY = "weave:fleet:prefs";

interface FleetPrefs {
  groupBy: GroupBy;
  sortBy: SortBy;
}

function loadPrefs(): FleetPrefs {
  try {
    const raw = localStorage.getItem(PREFS_KEY);

    if (raw) {
      const parsed = JSON.parse(raw) as Partial<FleetPrefs>;
      return {
        groupBy: parsed.groupBy ?? "directory",
        sortBy: parsed.sortBy ?? "recent",
      };
    }
  } catch {
    // ignore
  }

  return { groupBy: "directory", sortBy: "recent" };
}

const router = useRouter();
const sessionsStore = useSessionsStore();
const workspaceUiStore = useWorkspaceUiStore();
const { retentionStatus } = storeToRefs(sessionsStore);

const { sessions, isLoading, error } = useSessions({ retentionStatus });
const allWorkspaces = useWorkspaces(sessions);

const savedPrefs = loadPrefs();
const search = shallowRef("");
const groupBy = shallowRef<GroupBy>(savedPrefs.groupBy);
const sortBy = shallowRef<SortBy>(savedPrefs.sortBy);

const filteredSessions = computed<readonly SessionListItem[]>(() => {
  const q = search.value.toLowerCase().trim();

  if (!q) {
    return sessions.value;
  }

  return sessions.value.filter((s) =>
    (s.session.title ?? "").toLowerCase().includes(q)
    || s.workspaceDirectory.toLowerCase().includes(q)
    || s.session.id.toLowerCase().includes(q),
  );
});

const workspaces = computed(() => {
  let groups = allWorkspaces.value.filter((g) =>
    filteredSessions.value.some((s) => s.workspaceDirectory === g.workspaceDirectory),
  );

  if (sortBy.value === "name") {
    groups = [...groups].sort((a, b) => a.displayName.localeCompare(b.displayName));
  } else if (sortBy.value === "status") {
    groups = [...groups].sort((a, b) => (b.hasRunningSession ? 1 : 0) - (a.hasRunningSession ? 1 : 0));
  }

  return groups;
});

const isEmpty = computed(() => !isLoading.value && workspaces.value.length === 0);

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
</script>

<template>
  <section class="grid gap-6">
    <SummaryBar />

    <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h1 class="text-2xl font-semibold tracking-tight text-foreground">
          Sessions — Workspaces
        </h1>
        <p class="text-sm text-muted-foreground">
          Sessions grouped by workspace directory.
        </p>
      </div>

      <Button @click="workspaceUiStore.openNewSessionDialog(null)">
        <Plus class="h-4 w-4" />
        New Session
      </Button>
    </div>

    <WorkspaceToolbar
      v-model:search="search"
      v-model:group-by="groupBy"
      v-model:sort-by="sortBy"
    />

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
      class="rounded-xl border border-dashed border-border px-6 py-16 text-center"
    >
      <h2 class="text-lg font-semibold text-foreground">
        No sessions yet
      </h2>
      <p class="mt-2 text-sm text-muted-foreground">
        Create a session to start tracking fleet activity.
      </p>
    </div>

    <template v-else>
      <div
        v-for="group in workspaces"
        :key="group.workspaceId"
        class="grid gap-3"
      >
        <div class="flex items-center gap-2">
          <span
            class="h-2 w-2 rounded-full"
            :class="group.hasRunningSession ? 'bg-green-500' : 'bg-zinc-500'"
          />
          <h2 class="text-sm font-medium text-foreground">
            {{ group.displayName }}
          </h2>
          <span class="text-xs text-muted-foreground">{{ group.workspaceDirectory }}</span>
          <span class="ml-auto text-xs text-muted-foreground">{{ group.sessionCount }} session{{ group.sessionCount !== 1 ? 's' : '' }}</span>
        </div>
        <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          <SessionCard
            v-for="session in group.sessions"
            :key="session.session.id"
            :session="session"
            @select="handleSessionSelect"
          />
        </div>
      </div>
    </template>
  </section>
</template>
