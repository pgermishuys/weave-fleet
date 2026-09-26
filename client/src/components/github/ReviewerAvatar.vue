<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { reviewerWords, type ReviewerState } from "@/lib/pr-state";

/** A GitHub avatar; with a review state, ringed in its colour (approved, changes requested, still to answer). */
const props = withDefaults(defineProps<{
  login: string;
  avatarUrl?: string | null;
  state?: ReviewerState | null;
  size?: number;
}>(), {
  avatarUrl: null,
  state: null,
  size: 18,
});

const failed = shallowRef(false);
const initial = computed(() => props.login.slice(0, 1).toUpperCase());
const ring = computed(() => {
  switch (props.state) {
    case "APPROVED": return "approved";
    case "CHANGES_REQUESTED": return "changes";
    case null: return null;
    default: return "waiting";
  }
});
const title = computed(() => (props.state ? `${props.login}: ${reviewerWords(props.state)}` : props.login));
</script>

<template>
  <span
    class="gh-avatar"
    :data-ring="ring"
    :title="title"
    :style="{ width: `${size}px`, height: `${size}px`, fontSize: `${Math.round(size * 0.5)}px` }"
  >
    <img
      v-if="avatarUrl && !failed"
      :src="avatarUrl"
      alt=""
      loading="lazy"
      @error="failed = true"
    >
    <span
      v-else
      aria-hidden="true"
    >{{ initial }}</span>
    <span class="sr-only">{{ title }}</span>
  </span>
</template>

<style scoped>
.gh-avatar {
  position: relative;
  display: inline-grid;
  place-items: center;
  flex-shrink: 0;
  border-radius: 50%;
  background: color-mix(in srgb, var(--text) 12%, transparent);
  color: var(--text);
  font-weight: 600;
  line-height: 1;
}

.gh-avatar img {
  width: 100%;
  height: 100%;
  border-radius: 50%;
  object-fit: cover;
}

.gh-avatar[data-ring] {
  box-shadow: 0 0 0 2px var(--panel-bg), 0 0 0 3.5px var(--gh-avatar-ring);
}

.gh-avatar[data-ring="approved"] { --gh-avatar-ring: var(--check-pass); }
.gh-avatar[data-ring="changes"] { --gh-avatar-ring: var(--pr-blocked); }
.gh-avatar[data-ring="waiting"] { --gh-avatar-ring: color-mix(in srgb, var(--text) 22%, transparent); }
</style>
