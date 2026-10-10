<script setup lang="ts">
import { computed, ref } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { ChevronRight, LoaderCircle, RefreshCw, Undo2 } from "lucide-vue-next";
import ModCodeDialog from "@/components/mods/review/ModCodeDialog.vue";
import ModLogLines from "@/components/settings/mods/ModLogLines.vue";
import { formatAbsoluteTimestamp, formatRelativeTime } from "@/lib/format-utils";
import type { KeptMod, ModLogLine } from "@/lib/mods/kept";
import { fetchModVersionFiles } from "@/lib/mods/kept-api";
import { fetchModLog } from "@/lib/mods/mod-log";
import { useModsStore } from "@/stores/mods";

/** One kept mod: its switch, Undo, why it is off, and its versions (newest first) with Use and Show code. */
const props = defineProps<{ mod: KeptMod }>();

const store = useModsStore();
const router = useRouter();
const busy = ref<string | null>(null);
const error = ref<string | null>(null);
const historyOpen = ref(false);
const codeVersion = ref<number | null>(null);
const codeOpen = computed({
  get: () => codeVersion.value !== null,
  set: (open) => {
    if (!open) codeVersion.value = null;
  },
});

const activeVersion = computed(() => props.mod.versions.find((v) => v.number === props.mod.active) ?? null);
const versionsNewestFirst = computed(() => [...props.mod.versions].sort((a, b) => b.number - a.number));
const isOn = computed(() => props.mod.off === null);
const offByStrikes = computed(() => props.mod.off?.by === "strikes");

/** The server's rule: on vN (N>1) the previous version returns and the mod turns on; on v1 the mod turns off. */
const undoLabel = computed(() => ((props.mod.active ?? 1) > 1 ? `Undo (back to v${(props.mod.active ?? 1) - 1})` : "Undo (turn off)"));
const undoDisabledReason = computed(() => {
  if (props.mod.active === null) return "Nothing to undo: no version is kept yet.";
  if (props.mod.active === 1 && !isOn.value) return "Nothing to undo: v1 is the first version and the mod is already off.";
  return null;
});

function when(createdAt: string): string {
  return formatRelativeTime(createdAt);
}

function exactly(createdAt: string): string {
  return formatAbsoluteTimestamp(Date.parse(createdAt));
}

async function run(key: string, action: () => Promise<unknown>): Promise<void> {
  if (busy.value) return;
  busy.value = key;
  error.value = null;
  try {
    await action();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Something went wrong.";
  } finally {
    busy.value = null;
  }
}

const logLines = ref<ModLogLine[] | null>(null);
const logLoading = ref(false);
const logError = ref<string | null>(null);
let logRequested = false;

async function loadLog(): Promise<void> {
  logLoading.value = true;
  logError.value = null;
  try {
    logLines.value = await fetchModLog(props.mod.name);
  } catch (caught) {
    logError.value = caught instanceof Error ? caught.message : "Couldn't read the log.";
  } finally {
    logLoading.value = false;
  }
}

/** The log is fetched the first time it is opened, then again on Refresh. */
function onLogToggle(event: Event): void {
  if (!(event.target as HTMLDetailsElement).open || logRequested) return;
  logRequested = true;
  void loadLog();
}

function openSession(sessionId: string): void {
  void router.navigate({ to: "/sessions/$id", params: { id: sessionId }, search: { instanceId: undefined, parentSessionId: undefined } });
}

const setOn = (on: boolean) => run("on", () => store.setOn(props.mod.name, on));
const undo = () => run("undo", () => store.undo(props.mod.name));
const useVersion = (number: number) => run(`use-${number}`, () => store.activateVersion(props.mod.name, number));
</script>

<template>
  <li
    class="mod-row"
    :data-testid="`mod-row-${mod.name}`"
  >
    <div class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <p class="m-0 flex flex-wrap items-baseline gap-x-2 text-sm">
          <span class="break-all font-mono font-semibold text-text">{{ mod.name }}</span>
          <span class="font-mono text-xs text-text">v{{ mod.active }}</span>
          <span
            v-if="mod.activeVersion"
            class="font-mono text-xs text-muted"
          >{{ mod.activeVersion }}</span>
        </p>
        <p
          v-if="mod.description"
          class="mt-1 text-sm text-muted"
        >
          {{ mod.description }}
        </p>
        <p
          v-if="activeVersion"
          class="mt-1 text-xs text-muted"
        >
          kept <span :title="exactly(activeVersion.createdAt)">{{ when(activeVersion.createdAt) }}</span>
          <template v-if="activeVersion.sessionTitle">
            · from “<a
              v-if="activeVersion.sessionId"
              :href="`/sessions/${encodeURIComponent(activeVersion.sessionId)}`"
              class="text-accent hover:underline"
              @click.prevent="openSession(activeVersion.sessionId)"
            >{{ activeVersion.sessionTitle }}</a><template v-else>{{ activeVersion.sessionTitle }}</template>”
          </template>
        </p>
      </div>

      <div class="flex shrink-0 items-center gap-2">
        <span
          v-if="!isOn && !offByStrikes"
          class="text-xs text-muted"
        >Off</span>
        <button
          type="button"
          role="switch"
          :aria-checked="isOn"
          :disabled="busy !== null"
          :aria-label="`Run ${mod.name}`"
          :data-testid="`mod-switch-${mod.name}`"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isOn ? 'bg-accent' : 'bg-border'"
          @click="setOn(!isOn)"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isOn ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>

    <p
      v-if="offByStrikes"
      class="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-error"
      :data-testid="`mod-strikes-${mod.name}`"
    >
      <span>Turned off after 3 failures: {{ mod.off?.error }}</span>
      <button
        type="button"
        class="mod-link"
        :disabled="busy !== null"
        :data-testid="`mod-turn-on-${mod.name}`"
        @click="setOn(true)"
      >
        Turn on again
      </button>
    </p>

    <p
      v-if="error"
      class="mt-2 text-xs text-error"
      role="alert"
      :data-testid="`mod-error-${mod.name}`"
    >
      {{ error }}
    </p>

    <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2 text-xs">
      <button
        type="button"
        class="mod-link"
        :disabled="busy !== null || undoDisabledReason !== null"
        :title="undoDisabledReason ?? undefined"
        :data-testid="`mod-undo-${mod.name}`"
        @click="undo"
      >
        <LoaderCircle
          v-if="busy === 'undo'"
          class="h-3 w-3 animate-spin"
          aria-hidden="true"
        />
        <Undo2
          v-else
          class="h-3 w-3"
          aria-hidden="true"
        />{{ undoLabel }}
      </button>
      <button
        type="button"
        class="mod-link"
        :aria-expanded="historyOpen"
        :data-testid="`mod-history-toggle-${mod.name}`"
        @click="historyOpen = !historyOpen"
      >
        <ChevronRight
          class="h-3 w-3 transition-transform"
          :class="{ 'rotate-90': historyOpen }"
          aria-hidden="true"
        />History ({{ mod.versions.length }})
      </button>
    </div>

    <ol
      v-if="historyOpen"
      class="mod-history"
    >
      <li
        v-for="version in versionsNewestFirst"
        :key="version.number"
        class="mod-history__row"
        :data-testid="`mod-version-${mod.name}-${version.number}`"
      >
        <span class="font-mono text-xs font-semibold">v{{ version.number }}</span>
        <div class="min-w-0 space-y-1">
          <p class="m-0 text-xs text-muted">
            <span class="font-mono">{{ version.version }}</span>
            · <span :title="exactly(version.createdAt)">{{ when(version.createdAt) }}</span>
            <template v-if="version.sessionTitle">
              · from “{{ version.sessionTitle }}”
            </template>
          </p>
          <p
            v-if="version.note"
            class="m-0 text-sm text-text"
          >
            “{{ version.note }}”
          </p>
          <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
            <span
              v-if="version.number === mod.active"
              class="mod-badge"
            >Active</span>
            <button
              v-else
              type="button"
              class="mod-link"
              :disabled="busy !== null"
              :data-testid="`mod-use-${mod.name}-${version.number}`"
              @click="useVersion(version.number)"
            >
              <LoaderCircle
                v-if="busy === `use-${version.number}`"
                class="h-3 w-3 animate-spin"
                aria-hidden="true"
              />Use this version
            </button>
            <button
              type="button"
              class="mod-link"
              :data-testid="`mod-code-${mod.name}-${version.number}`"
              @click="codeVersion = version.number"
            >
              Show code
            </button>
          </div>
        </div>
      </li>
    </ol>

    <details
      class="mt-3 text-xs"
      :data-testid="`mod-log-${mod.name}`"
      @toggle="onLogToggle"
    >
      <summary class="cursor-pointer text-muted">
        Log
      </summary>
      <div class="mt-2 space-y-2">
        <p
          v-if="logError"
          class="m-0 text-error"
          role="alert"
        >
          {{ logError }}
        </p>
        <ModLogLines
          v-else-if="!logLoading || logLines"
          :lines="logLines"
        />
        <button
          type="button"
          class="mod-link"
          :disabled="logLoading"
          :data-testid="`mod-log-refresh-${mod.name}`"
          @click="loadLog"
        >
          <RefreshCw
            class="h-3 w-3"
            :class="{ 'animate-spin': logLoading }"
            aria-hidden="true"
          />Refresh
        </button>
      </div>
    </details>

    <ModCodeDialog
      v-model:open="codeOpen"
      :title="`${mod.name} · v${codeVersion ?? mod.active}`"
      :load="() => fetchModVersionFiles(mod.name, codeVersion ?? mod.active ?? 1)"
    />
  </li>
</template>

<style scoped>
.mod-row {
  padding: 14px 16px;
}

.mod-row + .mod-row {
  border-top: 1px solid var(--border);
}

.mod-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  cursor: pointer;
}

.mod-link:hover:not(:disabled) {
  text-decoration: underline;
}

.mod-link:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.mod-link:disabled {
  cursor: default;
  opacity: 0.5;
}

.mod-history {
  display: grid;
  margin: 12px 0 0;
  padding: 0;
  list-style: none;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
}

.mod-history__row {
  display: grid;
  grid-template-columns: 36px minmax(0, 1fr);
  gap: 10px;
  padding: 10px 12px;
}

.mod-history__row + .mod-history__row {
  border-top: 1px solid var(--border);
}

.mod-badge {
  padding: 0 7px;
  border-radius: 999px;
  background: var(--accent-dim);
  color: var(--accent);
  font-size: 11.5px;
  font-weight: 600;
}
</style>
