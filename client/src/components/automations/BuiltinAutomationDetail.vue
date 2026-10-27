<script setup lang="ts">
import { computed, onUnmounted, shallowRef, watch } from "vue";
import { useNavigate } from "@tanstack/vue-router";
import { AlertCircle, Play } from "lucide-vue-next";
import AutomationRunsList from "@/components/automations/AutomationRunsList.vue";
import { Button } from "@/components/ui/button";
import { useAutomations } from "@/composables/use-automations";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { apiFetch } from "@/lib/api-client";
import { describeDate } from "@/lib/automation-schedule";
import { browserTimeZone } from "@/lib/automations";
import type { Automation, AutomationRun } from "@/stores/automations";

/** A built-in automation Fleet keeps (Dreaming): its time, Run now and Pause are yours; deleting and editing aren't. */
const props = defineProps<{ automation: Automation }>();

const navigate = useNavigate();
const { setActiveSection } = useSettingsNav();
const { enableAutomation, disableAutomation, runAutomation, fetchRuns, refresh } = useAutomations();

const runs = shallowRef<AutomationRun[]>([]);
const hasLoadedRuns = shallowRef(false);
const notice = shallowRef<{ kind: "error" | "info"; text: string } | null>(null);
const isBusy = shallowRef(false);
const editingTime = shallowRef(false);
const time = shallowRef("03:00");
let pollTimer: ReturnType<typeof setTimeout> | undefined;

/** The clock time its cron "m h * * *" runs at, as HH:mm; null for a cron of any other shape. */
const clock = computed(() => {
  const match = /^(\d{1,2}) (\d{1,2}) \* \* \*$/.exec(props.automation.triggerConfig.trim());
  return match ? `${match[2].padStart(2, "0")}:${match[1].padStart(2, "0")}` : null;
});

const description = computed(() => {
  const when = clock.value ? `Every day at ${clock.value}.` : `On the schedule ${props.automation.triggerConfig}.`;
  return `${when} Starts one dreaming session for each repository with sessions since the last run. Skips the rest at no cost.`;
});

const meta = computed(() => {
  const { isEnabled, nextRunAt } = props.automation;
  if (!isEnabled) return "Paused. It won't run until you resume it.";
  return nextRunAt ? `Next run ${describeDate(new Date(nextRunAt))}` : "";
});

function messageOf(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

async function loadRuns(): Promise<void> {
  clearTimeout(pollTimer);
  const id = props.automation.id;
  try {
    const next = await fetchRuns(id);
    if (props.automation.id === id) {
      runs.value = next;
      hasLoadedRuns.value = true;
    }
  } catch {
    // The list keeps what it had; the next poll tries again.
  }
  const busy = runs.value.some((run) => run.state === "starting" || run.state === "running");
  pollTimer = setTimeout(() => {
    void refresh();
    void loadRuns();
  }, busy ? 3000 : 20000);
}

watch(() => props.automation.id, () => {
  runs.value = [];
  hasLoadedRuns.value = false;
  notice.value = null;
  editingTime.value = false;
  void loadRuns();
}, { immediate: true });

onUnmounted(() => clearTimeout(pollTimer));

/** Runs an action, saying why in a note when the server refuses it. */
async function act(action: () => Promise<void>, failure: string): Promise<void> {
  if (isBusy.value) return;
  isBusy.value = true;
  notice.value = null;
  try {
    await action();
  } catch (error) {
    notice.value = { kind: "error", text: messageOf(error, failure) };
  } finally {
    isBusy.value = false;
  }
}

const runNow = () => act(async () => {
  const run = await runAutomation(props.automation.id);
  runs.value = [run, ...runs.value.filter((existing) => existing.id !== run.id)];
  void loadRuns();
}, "Couldn't start a run.");

const togglePaused = () => act(
  () => (props.automation.isEnabled ? disableAutomation(props.automation.id) : enableAutomation(props.automation.id)),
  "Couldn't change it.",
);

function startEditingTime(): void {
  time.value = clock.value ?? "03:00";
  editingTime.value = true;
}

const saveTime = () => act(async () => {
  const response = await apiFetch("/api/memory/dreaming/nightly", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ time: time.value, timeZone: browserTimeZone() ?? "UTC" }),
  });
  if (!response.ok) {
    const data = (await response.json().catch(() => ({}))) as { error?: string };
    throw new Error(data.error ?? `HTTP ${response.status}`);
  }
  editingTime.value = false;
  await refresh();
}, "Couldn't change the time.");

function openMemorySettings(): void {
  setActiveSection("memory");
  void navigate({ to: "/settings" });
}
</script>

<template>
  <div class="builtin-page">
    <header class="builtin-page__header">
      <h2
        class="builtin-page__title"
        data-testid="automation-title"
      >
        {{ automation.name }}
      </h2>
      <span
        class="builtin-page__pill"
        data-testid="automation-builtin-pill"
      >Built in</span>
      <span class="builtin-page__pill">Experimental</span>
      <span
        class="builtin-page__meta"
        data-testid="automation-header-meta"
      >{{ meta }}</span>
    </header>

    <div class="builtin-page__body">
      <p
        class="builtin-page__text"
        data-testid="builtin-description"
      >
        {{ description }}
      </p>
      <div class="builtin-page__actions">
        <Button
          variant="outline"
          size="sm"
          :disabled="isBusy"
          data-testid="automation-run-now"
          @click="runNow"
        >
          <Play
            class="size-3.5"
            aria-hidden="true"
          />
          Run now
        </Button>
        <template v-if="editingTime">
          <input
            v-model="time"
            type="time"
            required
            class="builtin-page__time"
            aria-label="Time each night"
            data-testid="builtin-time"
          >
          <Button
            size="sm"
            :disabled="isBusy || !time"
            data-testid="builtin-time-save"
            @click="saveTime"
          >
            Save
          </Button>
          <Button
            variant="ghost"
            size="sm"
            @click="editingTime = false"
          >
            Cancel
          </Button>
        </template>
        <Button
          v-else
          variant="outline"
          size="sm"
          data-testid="builtin-change-time"
          @click="startEditingTime"
        >
          Change time
        </Button>
        <Button
          variant="outline"
          size="sm"
          :disabled="isBusy"
          data-testid="builtin-pause"
          @click="togglePaused"
        >
          {{ automation.isEnabled ? "Pause" : "Resume" }}
        </Button>
        <Button
          variant="ghost"
          size="sm"
          data-testid="builtin-settings"
          @click="openMemorySettings"
        >
          Settings → Memory
        </Button>
      </div>

      <div
        v-if="notice"
        class="builtin-page__error"
        role="alert"
        data-testid="automation-action-message"
      >
        <AlertCircle
          class="size-4 shrink-0"
          aria-hidden="true"
        />
        {{ notice.text }}
      </div>

      <div
        v-if="hasLoadedRuns && runs.length === 0"
        class="builtin-page__text"
        data-testid="automation-no-runs"
      >
        No runs yet. The first is {{ automation.nextRunAt ? describeDate(new Date(automation.nextRunAt)) : "tonight" }}, or press Run now.
      </div>
      <AutomationRunsList
        v-else-if="runs.length > 0"
        :runs="runs"
      />
    </div>
  </div>
</template>

<style scoped>
.builtin-page {
  display: flex;
  height: 100%;
  min-height: 0;
  flex-direction: column;
}

.builtin-page__header {
  display: flex;
  min-height: 56px;
  flex-shrink: 0;
  align-items: center;
  gap: 10px;
  border-bottom: 1px solid var(--border);
  padding: 8px max(1rem, env(safe-area-inset-right)) 8px max(1rem, env(safe-area-inset-left));
}

.builtin-page__title {
  color: var(--text);
  font-size: 14px;
  font-weight: 600;
  letter-spacing: -0.005em;
}

.builtin-page__pill {
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 1px 8px;
  color: var(--muted);
  font-size: 11.5px;
  font-weight: 500;
}

.builtin-page__meta {
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.builtin-page__body {
  display: flex;
  min-height: 0;
  flex: 1;
  flex-direction: column;
  gap: 14px;
  overflow-y: auto;
  padding: 18px 24px 8px;
}

.builtin-page__body > * {
  width: 100%;
  max-width: 760px;
  margin-inline: auto;
}

.builtin-page__text {
  color: var(--muted);
  font-size: 13.5px;
  line-height: 1.5;
}

.builtin-page__actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.builtin-page__time {
  height: 28px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 0 8px;
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 13px;
}

.builtin-page__error {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--error) 10%, transparent);
  padding: 9px 12px;
  color: var(--error);
  font-size: 12.5px;
  line-height: 1.5;
}

@media (max-width: 716px) {
  .builtin-page__header {
    padding-left: 44px;
  }

  .builtin-page__meta {
    display: none;
  }
}
</style>
