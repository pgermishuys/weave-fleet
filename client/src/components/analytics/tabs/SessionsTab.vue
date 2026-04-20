<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { ArrowDown, ArrowUp, ArrowUpDown } from "lucide-vue-next";
import type { SessionAnalytics } from "@/lib/api-types";
import { formatAnalyticsCost } from "@/lib/format-utils";

export type SessionsTabSortBy =
  | "title"
  | "project"
  | "tokens"
  | "cost"
  | "estimatedCost"
  | "durationSeconds"
  | "models"
  | "createdAt";

export type SessionsTabSortDir = "asc" | "desc";

interface SessionsTabColumn {
  key: SessionsTabSortBy;
  label: string;
  defaultDirection: SessionsTabSortDir;
  align?: "left" | "right";
}

interface Props {
  sessions: readonly SessionAnalytics[];
  isLoading: boolean;
  error?: string;
  sortBy?: SessionsTabSortBy;
  sortDir?: SessionsTabSortDir;
}

interface Emits {
  "update:sortBy": [value: SessionsTabSortBy];
  "update:sortDir": [value: SessionsTabSortDir];
  "sort-change": [payload: { sortBy: SessionsTabSortBy; sortDir: SessionsTabSortDir }];
}

const SESSIONS_TAB_COLUMNS: readonly SessionsTabColumn[] = [
  { key: "title", label: "Title", defaultDirection: "asc" },
  { key: "project", label: "Project", defaultDirection: "asc" },
  { key: "tokens", label: "Tokens", defaultDirection: "desc", align: "right" },
  { key: "cost", label: "Cost", defaultDirection: "desc", align: "right" },
  { key: "estimatedCost", label: "Estimated cost", defaultDirection: "desc", align: "right" },
  { key: "durationSeconds", label: "Duration", defaultDirection: "desc", align: "right" },
  { key: "models", label: "Models", defaultDirection: "asc" },
  { key: "createdAt", label: "Created", defaultDirection: "desc" },
] as const;

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const integerFormatter = new Intl.NumberFormat("en-US");
const createdAtFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

const activeSortBy = shallowRef<SessionsTabSortBy>(props.sortBy ?? "createdAt");
const activeSortDir = shallowRef<SessionsTabSortDir>(props.sortDir ?? "desc");

watch(
  () => props.sortBy,
  (nextSortBy) => {
    if (nextSortBy) {
      activeSortBy.value = nextSortBy;
    }
  },
  { immediate: true },
);

watch(
  () => props.sortDir,
  (nextSortDir) => {
    if (nextSortDir) {
      activeSortDir.value = nextSortDir;
    }
  },
  { immediate: true },
);

const sortedSessions = computed<SessionAnalytics[]>(() => {
  return [...props.sessions].sort((left, right) => compareSessions(left, right, activeSortBy.value, activeSortDir.value));
});

const emptyStateMessage = computed(() => {
  return props.error
    ? `Unable to load session analytics (${props.error}).`
    : "No sessions matched the current analytics filters.";
});

function handleSort(column: SessionsTabColumn): void {
  const nextSortDir = activeSortBy.value === column.key
    ? toggleSortDirection(activeSortDir.value)
    : column.defaultDirection;

  activeSortBy.value = column.key;
  activeSortDir.value = nextSortDir;
  emit("update:sortBy", column.key);
  emit("update:sortDir", nextSortDir);
  emit("sort-change", { sortBy: column.key, sortDir: nextSortDir });
}

function toggleSortDirection(direction: SessionsTabSortDir): SessionsTabSortDir {
  return direction === "asc" ? "desc" : "asc";
}

function compareSessions(
  left: SessionAnalytics,
  right: SessionAnalytics,
  sortBy: SessionsTabSortBy,
  sortDir: SessionsTabSortDir,
): number {
  switch (sortBy) {
    case "title":
      return compareText(formatSessionTitle(left.title), formatSessionTitle(right.title), sortDir);
    case "project":
      return compareText(resolveProjectLabel(left), resolveProjectLabel(right), sortDir);
    case "tokens":
      return compareNumber(left.tokens, right.tokens, sortDir);
    case "cost":
      return compareNumber(left.cost, right.cost, sortDir);
    case "estimatedCost":
      return compareNumber(left.estimatedCost, right.estimatedCost, sortDir);
    case "durationSeconds":
      return compareNullableNumber(left.durationSeconds, right.durationSeconds, sortDir);
    case "models":
      return compareText(resolveModelsLabel(left.models), resolveModelsLabel(right.models), sortDir);
    case "createdAt":
      return compareDate(left.createdAt, right.createdAt, sortDir);
  }
}

function compareText(left: string, right: string, direction: SessionsTabSortDir): number {
  const comparison = left.localeCompare(right, undefined, { sensitivity: "base" });
  return direction === "asc" ? comparison : comparison * -1;
}

function compareNumber(left: number, right: number, direction: SessionsTabSortDir): number {
  return direction === "asc" ? left - right : right - left;
}

function compareNullableNumber(
  left: number | null,
  right: number | null,
  direction: SessionsTabSortDir,
): number {
  if (left == null && right == null) {
    return 0;
  }

  if (left == null) {
    return 1;
  }

  if (right == null) {
    return -1;
  }

  return compareNumber(left, right, direction);
}

function compareDate(left: string, right: string, direction: SessionsTabSortDir): number {
  const leftTime = Number(new Date(left));
  const rightTime = Number(new Date(right));
  return compareNumber(leftTime, rightTime, direction);
}

function formatSessionTitle(title: string | null): string {
  return title?.trim() || "Untitled session";
}

function resolveProjectLabel(session: SessionAnalytics): string {
  return session.projectName?.trim() || session.projectId?.trim() || "Unassigned";
}

function resolveModelsLabel(models: readonly string[]): string {
  return models.length > 0 ? models.join(", ") : "Unknown model";
}

function formatTokenCount(tokens: number): string {
  return integerFormatter.format(tokens);
}

function formatDuration(durationSeconds: number | null): string {
  if (durationSeconds == null) {
    return "—";
  }

  if (durationSeconds < 60) {
    return `${Math.max(1, Math.round(durationSeconds))}s`;
  }

  const hours = Math.floor(durationSeconds / 3600);
  const minutes = Math.round((durationSeconds % 3600) / 60);

  if (hours === 0) {
    return `${Math.max(1, minutes)}m`;
  }

  return `${hours}h ${String(Math.max(0, minutes)).padStart(2, "0")}m`;
}

function formatCreatedAt(createdAt: string): string {
  return createdAtFormatter.format(new Date(createdAt));
}

function isActiveSort(sortBy: SessionsTabSortBy): boolean {
  return activeSortBy.value === sortBy;
}

function getAriaSort(sortBy: SessionsTabSortBy): "ascending" | "descending" | "none" {
  if (!isActiveSort(sortBy)) {
    return "none";
  }

  return activeSortDir.value === "asc" ? "ascending" : "descending";
}

function getSortIcon(sortBy: SessionsTabSortBy) {
  if (!isActiveSort(sortBy)) {
    return ArrowUpDown;
  }

  return activeSortDir.value === "asc" ? ArrowUp : ArrowDown;
}
</script>

<template>
  <section
    class="sessions-tab"
    aria-label="Sessions analytics table"
  >
    <div
      v-if="isLoading"
      class="sessions-tab__state sessions-tab__state--loading"
    >
      Loading session analytics…
    </div>

    <div
      v-else-if="sortedSessions.length === 0"
      class="sessions-tab__state"
    >
      {{ emptyStateMessage }}
    </div>

    <div
      v-else
      class="sessions-tab__table-shell"
    >
      <table class="sessions-tab__table">
        <caption class="sessions-tab__sr-only">
          Session analytics including title, project, token usage, cost, estimated cost, duration, models, and created time.
        </caption>
        <thead>
          <tr>
            <th
              v-for="column in SESSIONS_TAB_COLUMNS"
              :key="column.key"
              scope="col"
              class="sessions-tab__head"
              :class="{
                'sessions-tab__head--right': column.align === 'right',
              }"
              :aria-sort="getAriaSort(column.key)"
            >
              <button
                type="button"
                class="sessions-tab__sort-button"
                :class="{
                  'sessions-tab__sort-button--active': isActiveSort(column.key),
                  'sessions-tab__sort-button--right': column.align === 'right',
                }"
                @click="handleSort(column)"
              >
                <span>{{ column.label }}</span>
                <component
                  :is="getSortIcon(column.key)"
                  class="sessions-tab__sort-icon"
                  aria-hidden="true"
                />
              </button>
            </th>
          </tr>
        </thead>

        <tbody>
          <tr
            v-for="session in sortedSessions"
            :key="session.sessionId"
            class="sessions-tab__row"
          >
            <td class="sessions-tab__cell">
              <div class="sessions-tab__primary">
                {{ formatSessionTitle(session.title) }}
              </div>
              <div class="sessions-tab__secondary">
                {{ session.sessionId }}
              </div>
            </td>
            <td class="sessions-tab__cell">
              <div class="sessions-tab__primary">
                {{ resolveProjectLabel(session) }}
              </div>
            </td>
            <td class="sessions-tab__cell sessions-tab__cell--right">
              {{ formatTokenCount(session.tokens) }}
            </td>
            <td class="sessions-tab__cell sessions-tab__cell--right">
              {{ formatAnalyticsCost(session.cost) }}
            </td>
            <td class="sessions-tab__cell sessions-tab__cell--right">
              {{ formatAnalyticsCost(session.estimatedCost) }}
            </td>
            <td class="sessions-tab__cell sessions-tab__cell--right">
              {{ formatDuration(session.durationSeconds) }}
            </td>
            <td class="sessions-tab__cell">
              <div class="sessions-tab__models">
                <span
                  v-for="model in session.models"
                  :key="`${session.sessionId}-${model}`"
                  class="sessions-tab__model"
                >
                  {{ model }}
                </span>
                <span
                  v-if="session.models.length === 0"
                  class="sessions-tab__model sessions-tab__model--muted"
                >
                  Unknown model
                </span>
              </div>
            </td>
            <td class="sessions-tab__cell">
              <div class="sessions-tab__primary">
                {{ formatCreatedAt(session.createdAt) }}
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

<style scoped>
.sessions-tab {
  min-width: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  overflow: hidden;
}

.sessions-tab__table-shell {
  overflow-x: auto;
}

.sessions-tab__table {
  width: 100%;
  min-width: 980px;
  border-collapse: collapse;
}

.sessions-tab__head {
  padding: 0;
  border-bottom: 1px solid var(--border);
  background: rgba(255, 255, 255, 0.02);
  text-align: left;
  vertical-align: middle;
}

.sessions-tab__head--right {
  text-align: right;
}

.sessions-tab__sort-button {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 14px 16px;
  border: 0;
  background: transparent;
  color: var(--muted);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  cursor: pointer;
  transition: color 0.18s ease, background-color 0.18s ease;
}

.sessions-tab__sort-button:hover,
.sessions-tab__sort-button--active {
  color: var(--text);
}

.sessions-tab__sort-button:focus-visible {
  outline: 2px solid rgba(99, 102, 241, 0.6);
  outline-offset: -2px;
}

.sessions-tab__sort-button--right {
  justify-content: flex-end;
}

.sessions-tab__sort-icon {
  width: 14px;
  height: 14px;
  flex: 0 0 auto;
}

.sessions-tab__row {
  transition: background-color 0.18s ease;
}

.sessions-tab__row:hover {
  background: rgba(255, 255, 255, 0.02);
}

.sessions-tab__row + .sessions-tab__row {
  border-top: 1px solid rgba(255, 255, 255, 0.06);
}

.sessions-tab__cell {
  padding: 16px;
  color: var(--text);
  font-size: 14px;
  line-height: 1.5;
  vertical-align: top;
}

.sessions-tab__cell--right {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.sessions-tab__primary {
  color: var(--text);
  font-weight: 600;
}

.sessions-tab__secondary {
  margin-top: 4px;
  color: var(--muted);
  font-size: 12px;
  word-break: break-all;
}

.sessions-tab__models {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.sessions-tab__model {
  display: inline-flex;
  align-items: center;
  min-height: 24px;
  padding: 2px 8px;
  border: 1px solid rgba(99, 102, 241, 0.22);
  border-radius: 999px;
  background: rgba(99, 102, 241, 0.12);
  color: var(--text);
  font-size: 12px;
  line-height: 1.2;
}

.sessions-tab__model--muted {
  border-color: rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.04);
  color: var(--muted);
}

.sessions-tab__state {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 280px;
  padding: 24px;
  color: var(--muted);
  font-size: 14px;
  text-align: center;
}

.sessions-tab__state--loading {
  color: var(--text);
}

.sessions-tab__sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

@media (max-width: 720px) {
  .sessions-tab {
    border-radius: 20px;
  }

  .sessions-tab__sort-button,
  .sessions-tab__cell {
    padding: 14px;
  }
}
</style>
