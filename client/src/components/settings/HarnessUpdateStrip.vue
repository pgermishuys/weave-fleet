<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ArrowUpCircle, Check, Copy, LoaderCircle } from "lucide-vue-next";
import type { HarnessInfo } from "@/api/client";
import { refreshAllHarnesses } from "@/composables/use-harnesses";
import { apiFetch } from "@/lib/api-client";
import { harnessState } from "@/lib/harness-display";

/**
 * A harness's update, under its card in Settings → Harnesses: that a newer version is out (or that Fleet needs
 * one), the Update button, and the update Fleet is running. The update waits for working sessions, runs the
 * harness's own updater, then Fleet checks the version; a failed one shows the command to run by hand.
 */

const props = defineProps<{
  harness: HarnessInfo;
}>();

const update = computed(() => props.harness.update ?? null);
const job = computed(() => update.value?.job ?? null);
const name = computed(() => props.harness.displayName);
const tooOld = computed(() => harnessState(props.harness) === "update-needed");
const offer = computed(() => !job.value && (tooOld.value || update.value?.updateAvailable === true));

const busy = shallowRef(false);
const error = shallowRef<string | null>(null);
const copied = shallowRef(false);

function updatePath(): string {
  return `/api/harnesses/${encodeURIComponent(props.harness.type)}/update`;
}

async function send(method: "POST" | "DELETE"): Promise<void> {
  busy.value = true;
  error.value = null;
  try {
    const response = await apiFetch(updatePath(), { method });
    if (!response.ok) {
      const body = (await response.json().catch(() => ({}))) as { error?: string };
      error.value = body.error ?? `The request failed (HTTP ${response.status}).`;
    }
  } catch (requestError) {
    error.value = requestError instanceof Error ? requestError.message : "The request failed.";
  } finally {
    busy.value = false;
    refreshAllHarnesses();
  }
}

async function copyCommand(): Promise<void> {
  const command = update.value?.command;
  if (!command) return;
  try {
    await navigator.clipboard?.writeText(command);
    copied.value = true;
    setTimeout(() => (copied.value = false), 1500);
  } catch {
    // The command is on screen to select by hand.
  }
}
</script>

<template>
  <div
    v-if="job || offer || error"
    class="harness-update"
    :class="{
      'harness-update--warn': offer && tooOld,
      'harness-update--ok': job?.phase === 'succeeded',
      'harness-update--error': job?.phase === 'failed',
    }"
    data-testid="harness-update"
  >
    <div class="harness-update__row">
      <p
        v-if="job?.phase === 'waiting'"
        class="harness-update__msg"
      >
        <b>Waiting for {{ job.workingSessions }} working {{ job.workingSessions === 1 ? "session" : "sessions" }} to finish.</b>
        <span>The update starts as soon as they're idle.</span>
      </p>
      <p
        v-else-if="job?.phase === 'running'"
        class="harness-update__msg"
      >
        <b>Updating {{ name }}…</b>
        <span>It stops after 5 minutes if it hasn't finished.</span>
      </p>
      <p
        v-else-if="job"
        class="harness-update__msg"
      >
        <b>{{ job.message }}</b>
        <span v-if="job.phase === 'succeeded'">Idle {{ name }} processes restarted. New sessions use the new version.</span>
      </p>
      <p
        v-else-if="offer && tooOld"
        class="harness-update__msg"
      >
        <b>{{ harness.reason }}</b>
        <span v-if="update?.latestVersion">{{ update.latestVersion }} is the latest. New sessions can't use {{ name }} until you update.</span>
        <span v-else>New sessions can't use {{ name }} until you update.</span>
      </p>
      <p
        v-else-if="offer"
        class="harness-update__msg"
      >
        <b>{{ name }} {{ update?.latestVersion }} is available.</b> You have {{ harness.version }}.
        <span v-if="update?.command">Fleet runs <code>{{ update.command }}</code> once no session is working.</span>
      </p>

      <div class="harness-update__actions">
        <LoaderCircle
          v-if="job?.phase === 'waiting' || job?.phase === 'running'"
          :size="15"
          class="animate-spin text-accent"
          aria-hidden="true"
        />
        <button
          v-if="job?.phase === 'waiting'"
          type="button"
          class="harness-update__btn"
          :disabled="busy"
          data-testid="harness-update-cancel"
          @click="void send('DELETE')"
        >
          Cancel
        </button>
        <button
          v-if="job?.phase === 'succeeded' || job?.phase === 'failed'"
          type="button"
          class="harness-update__btn"
          :disabled="busy"
          data-testid="harness-update-dismiss"
          @click="void send('DELETE')"
        >
          {{ job.phase === "succeeded" ? "Done" : "Dismiss" }}
        </button>
        <button
          v-if="(offer && update?.command) || job?.phase === 'failed'"
          type="button"
          class="harness-update__btn harness-update__btn--primary"
          :disabled="busy"
          data-testid="harness-update-start"
          @click="void send('POST')"
        >
          <ArrowUpCircle
            :size="14"
            aria-hidden="true"
          />
          {{ job?.phase === "failed" ? "Try again" : update?.latestVersion ? `Update to ${update.latestVersion}` : "Update" }}
        </button>
      </div>
    </div>

    <p
      v-if="error"
      class="harness-update__error"
      role="alert"
    >
      {{ error }}
    </p>
    <div
      v-if="job?.phase === 'failed' && update?.command"
      class="harness-update__copy"
    >
      <code>{{ update.command }}</code>
      <button
        type="button"
        class="harness-update__btn"
        @click="void copyCommand()"
      >
        <Check
          v-if="copied"
          :size="14"
          aria-hidden="true"
        />
        <Copy
          v-else
          :size="14"
          aria-hidden="true"
        />
        {{ copied ? "Copied" : "Copy" }}
      </button>
    </div>
    <pre
      v-if="job?.phase === 'failed' && job.output"
      class="harness-update__log"
      data-testid="harness-update-output"
    >{{ job.output }}</pre>
  </div>
</template>

<style scoped>
.harness-update {
  display: grid;
  gap: 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 12px 14px;
  background: var(--main-bg);
  font-size: 13px;
}

.harness-update--warn {
  border-color: color-mix(in srgb, var(--idle) 40%, transparent);
  background: color-mix(in srgb, var(--idle) 10%, transparent);
}

.harness-update--ok {
  border-color: color-mix(in srgb, var(--running) 35%, transparent);
  background: color-mix(in srgb, var(--running) 10%, transparent);
}

.harness-update--error {
  border-color: color-mix(in srgb, var(--error) 35%, transparent);
  background: color-mix(in srgb, var(--error) 8%, transparent);
}

.harness-update__row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px 12px;
}

.harness-update__msg {
  flex: 1 1 260px;
  margin: 0;
  color: var(--text);
  line-height: 1.5;
}

.harness-update__msg b {
  font-weight: 600;
}

.harness-update__msg span {
  display: block;
  color: var(--muted);
  font-size: 12px;
}

.harness-update__msg code,
.harness-update__copy code {
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  overflow-wrap: anywhere;
}

.harness-update__actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.harness-update__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 6px 11px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 12.5px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color var(--transition), opacity var(--transition);
}

.harness-update__btn:hover {
  border-color: color-mix(in srgb, var(--accent) 50%, transparent);
}

.harness-update__btn--primary {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--primary-foreground);
}

.harness-update__btn--primary:hover {
  opacity: 0.9;
}

.harness-update__btn:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}

.harness-update__error {
  margin: 0;
  color: var(--error);
  font-size: 12.5px;
}

.harness-update__copy {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.harness-update__copy code {
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 5px 8px;
  background: var(--card-bg);
  color: var(--text);
}

.harness-update__log {
  max-height: 140px;
  margin: 0;
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 8px 10px;
  background: var(--card-bg);
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
