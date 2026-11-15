<script setup lang="ts">
// A relative time with its absolute one on hover: "3h" in a list, "3 hours ago" elsewhere, and the tooltip and the
// <time datetime> say exactly when. Keeps itself up to date on the shared 30-second tick unless the caller passes `now`.
import { computed } from "vue";
import { useRelativeTime } from "@/composables/use-relative-time";
import { formatDateTime, formatRelative, toTimestamp, type TimeInput } from "@/lib/format";

const props = defineProps<{
  at: TimeInput;
  /** "3h" for dense lists and tables; "3 hours ago" otherwise. */
  compact?: boolean;
  /** The caller's own ticking now, when it keeps one. */
  now?: number;
}>();

const tick = useRelativeTime();
const ts = computed(() => toTimestamp(props.at));
const reference = computed(() => props.now ?? tick.value);
const text = computed(() => formatRelative(ts.value, { now: reference.value, compact: props.compact }));
const exact = computed(() => formatDateTime(ts.value, { now: reference.value }));
const iso = computed(() => (Number.isNaN(ts.value) ? undefined : new Date(ts.value).toISOString()));
</script>

<template>
  <time
    v-if="text"
    :datetime="iso"
    :title="exact"
  >{{ text }}</time>
</template>
