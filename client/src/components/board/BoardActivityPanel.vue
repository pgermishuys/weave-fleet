<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { storeToRefs } from "pinia";
import { useActivityStream } from "@/composables/use-activity-stream";
import type { SessionListItem } from "@/lib/api-types";
import { useSessionsStore } from "@/stores/sessions";

type BoardActivityTone = "running" | "complete" | "error" | "queued" | "muted";
type BoardActivityEventType = "session_started" | "session_completed" | "session_failed" | "session_queued" | "session_updated";

interface BoardActivityEntry {
  id: string;
  sessionId: string;
  sessionTitle: string;
  projectName: string | null;
  action: string;
  occurredAt: number;
  timestampLabel: string;
  tone: BoardActivityTone;
}

interface MockActivitySeed {
  id: string;
  sessionId: string;
  action: string;
  tone: BoardActivityTone;
  offsetMinutes: number;
}

const MAX_VISIBLE_EVENTS = 40;
const INITIAL_VISIBLE_COUNT = 4;
const MOCK_EVENT_INTERVAL_MS = 2_500;
const WEBSOCKET_EVENT_TYPES: readonly BoardActivityEventType[] = [
  "session_started",
  "session_completed",
  "session_failed",
  "session_queued",
  "session_updated",
];

const fallbackSessions: readonly SessionListItem[] = [
  createFallbackSession("sess-auth", "JWT Auth Module", "API Platform", "active"),
  createFallbackSession("sess-ui", "Board Activity Feed", "Fleet UI", "active"),
  createFallbackSession("sess-ci", "CI Workflow Cleanup", "DX", "idle"),
  createFallbackSession("sess-billing", "Retry Failed Webhooks", "Billing", "completed"),
];

const mockActivitySeeds: readonly MockActivitySeed[] = [
  { id: "activity-001", sessionId: "sess-auth", action: "session started", tone: "running", offsetMinutes: 14 },
  { id: "activity-002", sessionId: "sess-ui", action: "delegate accepted review task", tone: "muted", offsetMinutes: 12 },
  { id: "activity-003", sessionId: "sess-ci", action: "session queued for follow-up", tone: "queued", offsetMinutes: 10 },
  { id: "activity-004", sessionId: "sess-auth", action: "tests completed successfully", tone: "complete", offsetMinutes: 8 },
  { id: "activity-005", sessionId: "sess-billing", action: "session completed", tone: "complete", offsetMinutes: 6 },
  { id: "activity-006", sessionId: "sess-ui", action: "new board activity event received", tone: "running", offsetMinutes: 4 },
  { id: "activity-007", sessionId: "sess-ci", action: "build failed on lint step", tone: "error", offsetMinutes: 2 },
  { id: "activity-008", sessionId: "sess-ui", action: "session updated with handoff notes", tone: "muted", offsetMinutes: 1 },
];

const sessionsStore = useSessionsStore();
const { sessions } = storeToRefs(sessionsStore);

const activityStream = useActivityStream();
const activityEntries = ref<BoardActivityEntry[]>([]);
const pendingMockEntries = ref<BoardActivityEntry[]>([]);

let mockTimer: ReturnType<typeof setInterval> | null = null;

const sessionIndex = computed(() => {
  const mergedSessions = [...fallbackSessions, ...sessions.value];
  return new Map(mergedSessions.map((session) => [session.session.id, session]));
});

const orderedEntries = computed(() => {
  return [...activityEntries.value].sort((left, right) => right.occurredAt - left.occurredAt);
});

function createFallbackSession(
  id: string,
  title: string,
  projectName: string,
  sessionStatus: SessionListItem["sessionStatus"],
): SessionListItem {
  const now = Date.now();

  return {
    instanceId: `instance-${id}`,
    workspaceId: `workspace-${id}`,
    workspaceDirectory: "/tmp",
    workspaceDisplayName: null,
    isolationStrategy: "existing",
    sessionStatus,
    session: {
      id,
      title,
      time: {
        created: now,
        updated: now,
      },
    },
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: null,
    branch: null,
    activityStatus: sessionStatus === "active" ? "busy" : "idle",
    lifecycleStatus: sessionStatus === "completed" ? "completed" : "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    projectId: null,
    projectName,
  };
}

function formatTimestamp(value: number): string {
  return new Intl.DateTimeFormat("en-US", {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
  }).format(value);
}

function createEntry(seed: MockActivitySeed, occurredAt: number): BoardActivityEntry {
  const session = sessionIndex.value.get(seed.sessionId);

  return {
    id: seed.id,
    sessionId: seed.sessionId,
    sessionTitle: session?.session.title ?? `Session ${seed.sessionId}`,
    projectName: session?.projectName ?? null,
    action: seed.action,
    occurredAt,
    timestampLabel: formatTimestamp(occurredAt),
    tone: seed.tone,
  };
}

function createMockTimeline(): BoardActivityEntry[] {
  const now = Date.now();

  return mockActivitySeeds.map((seed) => {
    const occurredAt = now - seed.offsetMinutes * 60_000;
    return createEntry(seed, occurredAt);
  });
}

function pushEntry(entry: BoardActivityEntry): void {
  activityEntries.value = [entry, ...activityEntries.value.filter((item) => item.id !== entry.id)].slice(
    0,
    MAX_VISIBLE_EVENTS,
  );
}

function flushNextMockEntry(): void {
  const [nextEntry, ...remainingEntries] = pendingMockEntries.value;

  if (!nextEntry) {
    stopMockStream();
    return;
  }

  pendingMockEntries.value = remainingEntries;
  pushEntry(nextEntry);
}

function startMockStream(): void {
  stopMockStream();

  mockTimer = setInterval(() => {
    flushNextMockEntry();
  }, MOCK_EVENT_INTERVAL_MS);
}

function stopMockStream(): void {
  if (mockTimer === null) {
    return;
  }

  clearInterval(mockTimer);
  mockTimer = null;
}

function normalizeTimestamp(value: unknown): number {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === "string") {
    const parsed = Date.parse(value);
    if (!Number.isNaN(parsed)) {
      return parsed;
    }
  }

  return Date.now();
}

function getTone(eventType: string, payload: Record<string, unknown>): BoardActivityTone {
  const status = typeof payload.status === "string" ? payload.status : "";

  if (eventType === "session_failed" || status === "failed") {
    return "error";
  }

  if (eventType === "session_completed" || status === "completed") {
    return "complete";
  }

  if (eventType === "session_queued" || status === "queued") {
    return "queued";
  }

  if (eventType === "session_started") {
    return "running";
  }

  return "muted";
}

function getActionLabel(eventType: string, payload: Record<string, unknown>): string {
  if (typeof payload.action === "string" && payload.action.length > 0) {
    return payload.action;
  }

  if (typeof payload.message === "string" && payload.message.length > 0) {
    return payload.message;
  }

  switch (eventType) {
    case "session_started":
      return "session started";
    case "session_completed":
      return "session completed";
    case "session_failed":
      return "session failed";
    case "session_queued":
      return "session queued";
    case "session_updated":
    default:
      return "session updated";
  }
}

function normalizeActivityEntry(eventType: string, payload: unknown): BoardActivityEntry | null {
  if (!payload || typeof payload !== "object") {
    return null;
  }

  const record = payload as Record<string, unknown>;
  const sessionId = typeof record.sessionId === "string" ? record.sessionId : "unknown";
  const session = sessionIndex.value.get(sessionId);
  const occurredAt = normalizeTimestamp(record.timestamp);

  return {
    id: typeof record.id === "string" ? record.id : `${eventType}-${sessionId}-${occurredAt}`,
    sessionId,
    sessionTitle: typeof record.sessionTitle === "string" ? record.sessionTitle : session?.session.title ?? `Session ${sessionId}`,
    projectName: typeof record.projectName === "string" ? record.projectName : session?.projectName ?? null,
    action: getActionLabel(eventType, record),
    occurredAt,
    timestampLabel: formatTimestamp(occurredAt),
    tone: getTone(eventType, record),
  };
}

function createWebSocketHandler(eventType: BoardActivityEventType): (payload: unknown) => void {
  return (payload: unknown) => {
    const normalizedEntry = normalizeActivityEntry(eventType, payload);
    if (!normalizedEntry) {
      return;
    }

    pushEntry(normalizedEntry);
  };
}

const websocketHandlers = new Map(
  WEBSOCKET_EVENT_TYPES.map((eventType) => [eventType, createWebSocketHandler(eventType)]),
);

onMounted(() => {
  const timeline = createMockTimeline();
  activityEntries.value = timeline.slice(-INITIAL_VISIBLE_COUNT).reverse();
  pendingMockEntries.value = timeline.slice(0, -INITIAL_VISIBLE_COUNT);

  for (const eventType of WEBSOCKET_EVENT_TYPES) {
    const handler = websocketHandlers.get(eventType);
    if (handler) {
      activityStream.on(eventType, handler);
    }
  }

  if (pendingMockEntries.value.length > 0) {
    startMockStream();
  }
});

onUnmounted(() => {
  stopMockStream();

  for (const eventType of WEBSOCKET_EVENT_TYPES) {
    const handler = websocketHandlers.get(eventType);
    if (handler) {
      activityStream.off(eventType, handler);
    }
  }
});
</script>

<template>
  <section class="activity-feed" aria-label="Board activity feed">
    <p v-if="orderedEntries.length === 0" class="activity-feed-empty">
      Waiting for activity events.
    </p>

    <article
      v-for="entry in orderedEntries"
      :key="entry.id"
      class="activity-feed-item"
    >
      <div class="activity-feed-row">
        <span class="af-time">{{ entry.timestampLabel }}</span>
        <span class="af-session">{{ entry.sessionTitle }}</span>
      </div>

      <p class="af-action" :class="`af-action--${entry.tone}`">
        {{ entry.action }}
      </p>

      <p v-if="entry.projectName" class="af-project">
        {{ entry.projectName }}
      </p>
    </article>
  </section>
</template>

<style scoped>
.activity-feed {
  display: flex;
  flex-direction: column;
}

.activity-feed-empty {
  margin: 0;
  padding: 8px 0;
  font-size: 12px;
  color: var(--muted);
}

.activity-feed-item {
  padding: 8px 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  font-size: 11px;
  line-height: 1.5;
}

.activity-feed-row {
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.af-time {
  color: #52525b;
}

.af-session {
  font-weight: 500;
  color: var(--text);
}

.af-action {
  margin: 2px 0 0;
  color: var(--muted);
}

.af-action--running {
  color: var(--accent);
}

.af-action--complete {
  color: var(--complete);
}

.af-action--error {
  color: var(--error);
}

.af-action--queued {
  color: var(--queued);
}

.af-action--muted {
  color: var(--muted);
}

.af-project {
  margin: 2px 0 0;
  color: #52525b;
}
</style>
