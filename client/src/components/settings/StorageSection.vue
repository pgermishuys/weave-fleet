<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { AlertCircle, LoaderCircle } from "lucide-vue-next";
import type { components } from "@/api/generated/schema";
import { extractApiError } from "@/lib/api-error";
import { formatBytes } from "@/lib/format-utils";
import { liveTarget } from "@/lib/machine-target";

type Checkout = components["schemas"]["WorktreeCheckout"];
type Report = components["schemas"]["WorktreeCleanupReport"];

/** How many folder sizes Fleet asks for at once; each is a walk of the folder. */
const SIZE_CONCURRENCY = 3;

const checkouts = shallowRef<Checkout[]>([]);
const archivedDays = shallowRef(7);
const isLoading = shallowRef(true);
const error = shallowRef<string | null>(null);
/** Bytes per workspace once measured; `complete` false means Fleet stopped counting at its cap. */
const sizes = shallowRef<Record<string, { bytes: number; complete: boolean }>>({});
const previewing = shallowRef(false);
const isCleaning = shallowRef(false);
const report = shallowRef<Report | null>(null);
/** The sizes at the time of the run, so the report still has them after the reload. */
const reportSizes = shallowRef<Record<string, { bytes: number; complete: boolean }>>({});
let generation = 0;

const byVerdict = (verdicts: string[]) => checkouts.value.filter((c) => verdicts.includes(c.verdict));
const removable = computed(() => byVerdict(["remove"]));
const kept = computed(() => byVerdict(["keep"]));
const recent = computed(() => byVerdict(["recent"]));

function sumOf(list: Checkout[], from = sizes.value): { bytes: number; label: string } {
  let bytes = 0;
  let known = 0;
  let complete = true;
  for (const c of list) {
    const size = from[c.workspaceId];
    if (!size) continue;
    known++;
    bytes += size.bytes;
    complete = complete && size.complete;
  }
  const label = list.length > 0 && known === 0
    ? "—"
    : `${complete && known === list.length ? "" : "≥ "}${formatBytes(bytes)}`;
  return { bytes, label };
}

function sizeLabel(c: Checkout, from = sizes.value): string {
  const size = from[c.workspaceId];
  if (!size) return "—";
  return `${size.complete ? "" : "≥ "}${formatBytes(size.bytes)}`;
}

const total = computed(() => sumOf(checkouts.value));
const inUse = computed(() => [...byVerdict(["in-use"]), ...recent.value]);
const splits = computed(() => [
  { key: "in-use", label: "in use", list: inUse.value },
  { key: "keep", label: "need a look", list: kept.value },
  { key: "remove", label: "could be freed", list: removable.value },
]);

async function loadSizes(list: Checkout[], run: number): Promise<void> {
  const queue = [...list];
  const worker = async (): Promise<void> => {
    for (let next = queue.shift(); next && run === generation; next = queue.shift()) {
      try {
        const { data } = await liveTarget().api.GET("/api/storage/worktrees/{workspaceId}/size", {
          params: { path: { workspaceId: next.workspaceId } },
        });
        if (data && run === generation) sizes.value = { ...sizes.value, [next.workspaceId]: data };
      } catch {
        // A folder that can't be measured stays "—".
      }
    }
  };
  await Promise.all(Array.from({ length: SIZE_CONCURRENCY }, worker));
}

async function load(): Promise<void> {
  const run = ++generation;
  isLoading.value = checkouts.value.length === 0;
  error.value = null;
  try {
    const { data, error: apiError } = await liveTarget().api.GET("/api/storage/worktrees");
    if (run !== generation) return;
    if (apiError || !data) {
      error.value = extractApiError(apiError, "Couldn't load the worktree checkouts.");
      return;
    }
    checkouts.value = data.checkouts;
    archivedDays.value = data.archivedDays;
    isLoading.value = false;
    // Counts first; sizes fill in as they arrive, removable folders first since they matter most.
    const order = [...data.checkouts].sort((a, b) => Number(b.verdict === "remove") - Number(a.verdict === "remove"));
    void loadSizes(order, run);
  } catch (cause) {
    if (run === generation) error.value = extractApiError(cause, "Couldn't load the worktree checkouts.");
  } finally {
    if (run === generation) isLoading.value = false;
  }
}

async function clean(): Promise<void> {
  const ids = removable.value.map((c) => c.workspaceId);
  isCleaning.value = true;
  error.value = null;
  try {
    const { data, error: apiError } = await liveTarget().api.POST("/api/storage/worktrees/clean", {
      body: { workspaceIds: ids },
    });
    if (apiError || !data) {
      error.value = extractApiError(apiError, "Couldn't clean the checkouts.");
      return;
    }
    reportSizes.value = sizes.value;
    // What the preview already kept belongs in the report too, after what the run itself kept.
    const keptAgain = new Set(data.kept.map((c) => c.workspaceId));
    report.value = { removed: data.removed, kept: [...data.kept, ...kept.value.filter((c) => !keptAgain.has(c.workspaceId))] };
    previewing.value = false;
    await load();
  } catch (cause) {
    error.value = extractApiError(cause, "Couldn't clean the checkouts.");
  } finally {
    isCleaning.value = false;
  }
}

const noun = (n: number) => `${n} ${n === 1 ? "checkout" : "checkouts"}`;
const freed = computed(() => report.value ? sumOf(report.value.removed, reportSizes.value) : null);

onMounted(load);
</script>

<template>
  <section
    class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
    data-testid="storage-section"
  >
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Storage
      </h2>
      <p class="text-sm text-muted">
        What Fleet's worktree checkouts take on this machine.
      </p>
    </div>

    <p
      v-if="error"
      class="mt-4 flex items-center gap-2 text-sm text-[var(--error)]"
      role="alert"
      data-testid="storage-error"
    >
      <AlertCircle
        :size="14"
        aria-hidden="true"
      />
      {{ error }}
    </p>

    <p
      v-if="isLoading"
      class="mt-5 flex items-center gap-2 text-sm text-muted"
      data-testid="storage-loading"
    >
      <LoaderCircle
        :size="14"
        class="animate-spin"
        aria-hidden="true"
      />
      Looking at your checkouts…
    </p>

    <template v-else-if="!error || checkouts.length > 0">
      <div class="mt-5">
        <p
          class="text-2xl font-semibold text-text"
          data-testid="storage-total"
        >
          <span class="font-mono">{{ total.label }}</span> in {{ noun(checkouts.length) }}
        </p>
        <div class="mt-3 grid grid-cols-3 gap-3">
          <div
            v-for="split in splits"
            :key="split.key"
            class="rounded-btn border border-border bg-main-bg px-3 py-2"
            :data-testid="`storage-split-${split.key}`"
          >
            <p class="font-mono text-sm text-text">
              {{ sumOf(split.list).label }}
            </p>
            <p class="text-xs text-muted">
              {{ split.list.length }} {{ split.label }}
            </p>
          </div>
        </div>
        <p class="mt-2 text-xs text-muted">
          Sizes fill in as Fleet measures each folder. Only checkouts Fleet made are counted.
        </p>
      </div>

      <p class="mt-4 text-xs text-muted">
        <span class="font-medium text-text">Always kept:</span>
        Uncommitted changes · a session that is working · a folder another session uses · an open terminal · anything Fleet didn't make.
        The branch and the conversation always stay; the next message to the session puts the checkout back.
      </p>

      <div class="mt-5 rounded-btn border border-border bg-main-bg p-4">
        <div class="flex flex-wrap items-center gap-3">
          <h3 class="text-sm font-semibold text-text">
            Clean now
          </h3>
          <button
            v-if="!previewing"
            type="button"
            class="rounded-btn border border-border px-3 py-1.5 text-sm text-text transition-colors hover:bg-card-bg disabled:cursor-not-allowed disabled:opacity-50"
            :disabled="removable.length === 0"
            data-testid="storage-preview"
            @click="previewing = true; report = null"
          >
            Preview…
          </button>
          <span
            v-if="removable.length === 0"
            class="text-xs text-muted"
          >
            Nothing to remove right now. Only checkouts of sessions archived for {{ archivedDays }} days or more can go.
          </span>
          <span
            v-else-if="!previewing"
            class="text-xs text-muted"
          >
            Clean now removes the checkouts of sessions archived for {{ archivedDays }} days or more.
          </span>
        </div>

        <div
          v-if="previewing"
          class="mt-3"
          data-testid="storage-preview-panel"
        >
          <p class="text-sm text-text">
            Would remove <strong>{{ noun(removable.length) }}</strong> and free <strong class="font-mono">{{ sumOf(removable).label }}</strong>.
            {{ kept.length }} would be kept<template v-if="recent.length > 0">, and {{ recent.length }} more {{ recent.length === 1 ? "is" : "are" }} too recent</template>.
          </p>
          <p class="text-xs text-muted">
            Nothing has been removed yet. Branches, commits and conversations stay.
          </p>

          <template
            v-for="group in [{ title: 'Would remove', tag: 'will remove', list: removable }, { title: 'Would keep', tag: 'will keep', list: kept }]"
            :key="group.title"
          >
            <h4
              v-if="group.list.length > 0"
              class="mt-4 text-[10px] font-semibold uppercase tracking-wider text-muted"
            >
              {{ group.title }}
            </h4>
            <ul class="divide-y divide-border">
              <li
                v-for="c in group.list"
                :key="c.workspaceId"
                class="grid grid-cols-[auto_1fr_auto] items-baseline gap-x-3 py-2"
                data-testid="storage-row"
              >
                <span class="w-[84px] rounded-full border border-border px-2 text-center font-mono text-[10px] text-muted">{{ group.tag }}</span>
                <span class="min-w-0 truncate text-sm text-text">{{ c.repository }} · {{ c.title }}</span>
                <span class="font-mono text-xs text-text">{{ sizeLabel(c) }}</span>
                <span class="col-start-2 text-xs text-muted">{{ c.reason }}</span>
              </li>
            </ul>
          </template>

          <div class="mt-4 flex gap-2">
            <button
              type="button"
              class="rounded-btn bg-accent px-3 py-1.5 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60"
              :disabled="isCleaning || removable.length === 0"
              data-testid="storage-confirm"
              @click="clean"
            >
              <template v-if="isCleaning">
                <LoaderCircle
                  :size="13"
                  class="mr-1 inline animate-spin"
                  aria-hidden="true"
                />
                Removing {{ noun(removable.length) }}…
              </template>
              <template v-else>
                Remove {{ noun(removable.length) }}
              </template>
            </button>
            <button
              type="button"
              class="rounded-btn border border-border px-3 py-1.5 text-sm text-text transition-colors hover:bg-card-bg disabled:opacity-50"
              :disabled="isCleaning"
              @click="previewing = false"
            >
              Cancel
            </button>
          </div>
        </div>

        <div
          v-if="report"
          class="mt-3"
          data-testid="storage-report"
        >
          <p class="text-sm font-semibold text-text">
            Freed <span class="font-mono">{{ freed?.label }}</span>
          </p>
          <p class="text-xs text-muted">
            Removed {{ noun(report.removed.length) }}. Kept {{ report.kept.length }}<template v-if="recent.length > 0">, and {{ recent.length }} {{ recent.length === 1 ? "is" : "are" }} too recent</template>.
          </p>
          <template
            v-for="group in [{ title: 'Removed', list: report.removed }, { title: 'Kept', list: report.kept }]"
            :key="group.title"
          >
            <h4
              v-if="group.list.length > 0"
              class="mt-3 text-[10px] font-semibold uppercase tracking-wider text-muted"
            >
              {{ group.title }}
            </h4>
            <ul class="divide-y divide-border">
              <li
                v-for="c in group.list"
                :key="c.workspaceId"
                class="grid grid-cols-[1fr_auto] items-baseline gap-x-3 py-2"
              >
                <span class="min-w-0 truncate text-sm text-text">{{ c.repository }} · {{ c.title }}</span>
                <span class="font-mono text-xs text-text">{{ sizeLabel(c, reportSizes) }}</span>
                <span class="text-xs text-muted">{{ c.reason }}</span>
              </li>
            </ul>
          </template>
          <p class="mt-3 text-xs text-muted">
            Each removed session shows “Checkout removed”. Its checkout comes back on its next message, or with Restore now.
          </p>
        </div>
      </div>
    </template>
  </section>
</template>
