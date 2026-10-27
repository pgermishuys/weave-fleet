<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { Button } from "@/components/ui/button";
import type { Tile } from "@/lib/mission-control";
import { readRequest } from "@/lib/mission-control-requests";
import { peekEvents, type PeekEvent } from "@/lib/mission-peek";
import type { FleetMessage } from "@/lib/pagination-utils";
import type { RunningWorkItem } from "@/lib/running-work";
import type { AnswerOutcome, AnswerTarget } from "@/lib/push/answer";

const props = defineProps<{
  tile: Tile;
  /** Where the tile's machine is; null when it's no longer in the list. */
  target: AnswerTarget | null;
}>();
const emit = defineEmits<{
  nudge: [tile: Tile, text: string, done: (outcome: AnswerOutcome & { queued: boolean }) => void];
  open: [tile: Tile];
}>();

const events = shallowRef<PeekEvent[]>([]);
const helpers = shallowRef<RunningWorkItem[]>([]);
const text = shallowRef("");
const sending = shallowRef(false);
const sent = shallowRef<"sent" | "queued" | null>(null);
const error = shallowRef<string | null>(null);

async function read<T>(path: string): Promise<T | null> {
  if (!props.target) return null;
  const request = readRequest(props.target, path);
  try {
    const response = await fetch(request.url, request.init);
    return response.ok ? await response.json() as T : null;
  } catch {
    return null;
  }
}

async function load(): Promise<void> {
  const id = encodeURIComponent(props.tile.sessionId);
  const key = props.tile.key;
  const [messages, work] = await Promise.all([
    read<{ messages?: FleetMessage[] }>(`/api/sessions/${id}/messages?limit=6`),
    read<RunningWorkItem[]>(`/api/sessions/${id}/work`),
  ]);
  if (key !== props.tile.key) return;
  events.value = peekEvents(messages?.messages ?? []);
  helpers.value = (work ?? []).filter((item) => item.status === "running");
}

// Again whenever the session does something new: its tile's line changes with each step.
watch(() => [props.tile.key, props.tile.now, props.tile.helpers] as const, () => void load(), { immediate: true });
watch(() => props.tile.key, () => {
  text.value = "";
  sent.value = null;
  error.value = null;
});

function nudge(): void {
  const message = text.value.trim();
  if (!message || sending.value) return;
  sending.value = true;
  sent.value = null;
  error.value = null;
  emit("nudge", props.tile, message, (outcome) => {
    sending.value = false;
    if (outcome.ok) {
      text.value = "";
      sent.value = outcome.queued ? "queued" : "sent";
    } else {
      error.value = outcome.error ?? "It didn't go through.";
    }
  });
}
</script>

<template>
  <aside
    class="flex w-80 shrink-0 flex-col gap-4 overflow-y-auto border-l border-border p-4"
    aria-label="Peek"
    aria-live="polite"
    data-testid="mission-peek"
  >
    <div>
      <p class="text-xs text-muted">
        {{ tile.state === "working" ? "Working" : tile.state === "needs-you" ? "Needs you" : tile.state === "error" ? "Stopped with an error" : "Idle" }}
        · {{ tile.machineName }}
      </p>
      <h3 class="mt-1 text-base font-semibold text-text">
        {{ tile.title }}
      </h3>
      <p class="mt-1 text-xs text-muted">
        {{ [tile.harness, tile.model, tile.branch].filter(Boolean).join(" · ") }}
      </p>
    </div>

    <section>
      <h4 class="text-[0.7rem] font-semibold uppercase tracking-wide text-muted">
        Just now
      </h4>
      <ul
        class="mt-2 space-y-1.5 text-sm"
        data-testid="peek-events"
      >
        <li
          v-for="event in events"
          :key="event.key"
          class="truncate font-mono text-xs text-text"
        >
          {{ event.text }}
        </li>
        <li
          v-if="!events.length"
          class="text-xs text-muted"
        >
          Nothing yet.
        </li>
      </ul>
    </section>

    <section v-if="helpers.length">
      <h4 class="text-[0.7rem] font-semibold uppercase tracking-wide text-muted">
        Helpers
      </h4>
      <ul
        class="mt-2 space-y-1.5"
        data-testid="peek-helpers"
      >
        <li
          v-for="helper in helpers"
          :key="helper.id"
          class="rounded-card border border-border px-2 py-1.5 text-xs text-text"
        >
          <span class="font-semibold">{{ helper.title }}</span>
          <span
            v-if="helper.label"
            class="text-muted"
          > {{ helper.label }}</span>
        </li>
      </ul>
    </section>

    <div class="mt-auto space-y-2">
      <form @submit.prevent="nudge">
        <input
          v-model="text"
          type="text"
          class="w-full rounded-card border border-border bg-main-bg px-3 py-2 text-sm text-text placeholder:text-muted"
          placeholder="Nudge it…"
          aria-label="Nudge this session"
          data-testid="peek-nudge"
        >
      </form>
      <p
        v-if="sent"
        class="text-xs text-muted"
        data-testid="peek-sent"
      >
        {{ sent === "queued" ? "Queued: it goes when the turn ends." : "Sent." }}
      </p>
      <p
        v-if="error"
        class="text-xs text-destructive"
        role="alert"
      >
        {{ error }}
      </p>
      <Button
        size="sm"
        @click="emit('open', tile)"
      >
        Open
      </Button>
    </div>
  </aside>
</template>
