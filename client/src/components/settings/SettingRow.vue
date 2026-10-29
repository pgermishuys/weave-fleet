<script setup lang="ts">
import { shallowRef } from "vue";

/**
 * One setting: its title, a one-line summary, and the control on the right. When the `more` slot is filled, a
 * More link opens it below the summary; that is where a setting's full description lives. `adds` is what a Labs
 * switch gives you while it is on, with an Open link that goes there.
 */
const props = defineProps<{
  /** Stable id, so a link or settings search can reach the row: `settings-row-<id>`. */
  id: string;
  title: string;
  summary?: string;
  adds?: string | null;
  /** The experiment adds something, but there is no place to go to: hides Open. */
  noOpen?: boolean;
}>();

const emit = defineEmits<{ open: [] }>();

const moreOpen = shallowRef(false);
</script>

<template>
  <div
    :id="`settings-row-${id}`"
    class="flex items-start justify-between gap-4 px-4 py-3"
    :data-setting-row="id"
  >
    <div class="min-w-0 flex-1">
      <p class="flex items-center gap-2 text-sm font-medium text-text">
        {{ props.title }}
        <slot name="badge" />
      </p>
      <p
        v-if="summary || $slots.more"
        class="mt-0.5 text-xs text-muted"
      >
        <template v-if="summary">
          {{ summary }}
        </template>
        <button
          v-if="$slots.more"
          type="button"
          class="ml-1 text-accent hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-accent"
          :aria-expanded="moreOpen"
          :aria-controls="`settings-row-${id}-more`"
          :data-testid="`setting-more-${id}`"
          @click="moreOpen = !moreOpen"
        >{{ moreOpen ? "Less" : "More" }}</button>
      </p>
      <div
        v-if="$slots.more && moreOpen"
        :id="`settings-row-${id}-more`"
        class="mt-1 text-xs text-muted"
        :data-testid="`setting-more-text-${id}`"
      >
        <slot name="more" />
      </div>
      <p
        v-if="adds"
        class="mt-1 text-xs text-accent"
        :data-testid="`setting-adds-${id}`"
      >
        &#8627; Adds: {{ adds }}
        <template v-if="!noOpen">
          &middot;
          <button
            type="button"
            class="underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-accent"
            :data-testid="`setting-open-${id}`"
            @click="emit('open')"
          >Open</button>
        </template>
      </p>
      <slot name="below" />
    </div>
    <div class="shrink-0">
      <slot name="control" />
    </div>
  </div>
</template>
