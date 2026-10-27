<script setup lang="ts">
import { shallowRef } from "vue";
import { Button } from "@/components/ui/button";
import type { Tile } from "@/lib/mission-control";
import type { AnswerOutcome, PermissionReply } from "@/lib/push/answer";

defineProps<{ items: readonly Tile[] }>();
const emit = defineEmits<{
  answerQuestion: [tile: Tile, requestId: string, answers: string[][], done: (outcome: AnswerOutcome) => void];
  answerPermission: [tile: Tile, reply: PermissionReply, done: (outcome: AnswerOutcome) => void];
  open: [tile: Tile];
}>();

const busy = shallowRef<ReadonlySet<string>>(new Set());
const errors = shallowRef<Readonly<Record<string, string>>>({});

function finish(key: string, outcome: AnswerOutcome): void {
  busy.value = new Set([...busy.value].filter((candidate) => candidate !== key));
  errors.value = { ...errors.value, [key]: outcome.ok || outcome.gone ? "" : outcome.error ?? "It didn't go through." };
}

function start(key: string): void {
  busy.value = new Set([...busy.value, key]);
}

function answerQuestion(tile: Tile, requestId: string, label: string): void {
  start(tile.key);
  emit("answerQuestion", tile, requestId, [[label]], (outcome) => finish(tile.key, outcome));
}

function answerPermission(tile: Tile, reply: PermissionReply): void {
  start(tile.key);
  emit("answerPermission", tile, reply, (outcome) => finish(tile.key, outcome));
}
</script>

<template>
  <aside
    class="flex w-80 shrink-0 flex-col gap-3 overflow-y-auto border-r border-border p-4"
    aria-label="Needs you"
    data-testid="mission-needs-you"
  >
    <div class="flex items-baseline gap-2">
      <h3 class="text-sm font-semibold text-text">
        Needs you
      </h3>
      <span class="text-sm font-semibold text-accent">{{ items.length }}</span>
    </div>
    <p
      v-if="!items.length"
      class="text-sm text-muted"
    >
      Nothing is waiting on you.
    </p>
    <article
      v-for="tile in items"
      :key="tile.key"
      class="rounded-card border border-border border-l-4 border-l-accent bg-main-bg p-3"
      data-testid="mission-ask"
    >
      <p class="text-sm font-medium text-text">
        {{ tile.title }}
      </p>
      <p class="mt-1 flex flex-wrap items-center gap-1.5 text-xs text-muted">
        <span class="rounded border border-border px-1.5 py-px font-mono text-text">{{ tile.machineName }}</span>
        <span v-if="tile.ask?.kind === 'permission'">wants to run</span>
      </p>

      <template v-if="tile.ask?.kind === 'question'">
        <p class="mt-2 text-sm text-text">
          {{ tile.ask.question.question }}
        </p>
        <div
          v-if="!tile.ask.question.multiple && !tile.ask.more"
          class="mt-2 flex flex-wrap gap-1.5"
        >
          <Button
            v-for="option in tile.ask.question.options"
            :key="option.label"
            size="sm"
            variant="outline"
            :disabled="busy.has(tile.key)"
            :title="option.description"
            @click="answerQuestion(tile, (tile.ask as { requestId: string }).requestId, option.label)"
          >
            {{ option.label }}
          </Button>
        </div>
        <Button
          v-else
          class="mt-2"
          size="sm"
          variant="outline"
          @click="emit('open', tile)"
        >
          Open to answer
        </Button>
      </template>

      <template v-else-if="tile.ask?.kind === 'permission'">
        <code class="mt-2 block break-all rounded bg-panel-bg px-2 py-1 font-mono text-xs text-text">{{ tile.ask.ask.title || tile.ask.ask.tool }}</code>
        <div class="mt-2 flex flex-wrap gap-1.5">
          <Button
            size="sm"
            :disabled="busy.has(tile.key)"
            @click="answerPermission(tile, 'once')"
          >
            Allow once
          </Button>
          <Button
            size="sm"
            variant="outline"
            :disabled="busy.has(tile.key)"
            @click="answerPermission(tile, 'always')"
          >
            Always
          </Button>
          <Button
            size="sm"
            variant="outline"
            :disabled="busy.has(tile.key)"
            @click="answerPermission(tile, 'reject')"
          >
            Deny
          </Button>
        </div>
      </template>

      <template v-else>
        <p class="mt-2 text-sm text-muted">
          {{ tile.now }}
        </p>
        <Button
          class="mt-2"
          size="sm"
          variant="outline"
          @click="emit('open', tile)"
        >
          Open
        </Button>
      </template>

      <p
        v-if="errors[tile.key]"
        class="mt-2 text-xs text-destructive"
        role="alert"
      >
        {{ errors[tile.key] }}
      </p>
    </article>
  </aside>
</template>
