<script setup lang="ts">
import { computed } from "vue";
import { Pin, PinOff, X } from "lucide-vue-next";
import SmartLinkIcon from "@/components/session-context/SmartLinkIcon.vue";
import {
  hasTitle,
  isPullRequest,
  linkHref,
  linkNumber,
  linkTitle,
  summarizeChecks,
  type SmartLink,
} from "@/lib/smart-links";
import { useSmartLinksStore } from "@/stores/smart-links";

const props = defineProps<{
  link: SmartLink;
  /** Short context shown before the status, e.g. "Started from a board card". */
  note?: string | null;
}>();

const store = useSmartLinksStore();

const number = computed(() => linkNumber(props.link));
const kind = computed(() => (isPullRequest(props.link) ? "Pull request" : "Issue"));

const detail = computed(() => {
  const parts: string[] = [];
  if (props.note) parts.push(props.note);
  // Until GitHub answers, the title already reads "Issue #42".
  if (hasTitle(props.link)) parts.push(`${kind.value} #${number.value}`);
  switch (props.link.enrichmentStatus) {
    case "resolved": {
      if (props.link.statusLabel) parts.push(props.link.statusLabel);
      const checks = summarizeChecks(props.link);
      if (isPullRequest(props.link) && !props.link.isTerminal && checks.failing) parts.push(`${checks.failing} failing`);
      break;
    }
    case "pending": parts.push("Checking GitHub…"); break;
    case "not_connected": parts.push("GitHub not connected"); break;
    case "not_found": parts.push("Not found on GitHub"); break;
    case "error": parts.push("Couldn't check GitHub"); break;
  }
  return parts.join(" · ");
});
</script>

<template>
  <div
    class="link-row"
    :data-link-id="link.id"
  >
    <SmartLinkIcon
      :link="link"
      class="link-row__icon"
    />
    <div class="link-row__main">
      <a
        class="link-row__title"
        :href="linkHref(link)"
        target="_blank"
        rel="noopener noreferrer"
      >{{ linkTitle(link) }}</a>
      <span class="link-row__detail">{{ detail }}</span>
    </div>
    <div class="link-row__actions">
      <button
        v-if="link.relationship === 'mentioned'"
        type="button"
        class="link-row__btn"
        :aria-label="`Pin #${number} to the session header`"
        title="Pin to header"
        @click="store.setPinned(link.sessionId, link.id, true)"
      >
        <Pin
          :size="12"
          aria-hidden="true"
        />
      </button>
      <button
        v-else-if="link.relationship === 'pinned'"
        type="button"
        class="link-row__btn"
        :aria-label="`Unpin #${number}`"
        title="Unpin"
        @click="store.setPinned(link.sessionId, link.id, false)"
      >
        <PinOff
          :size="12"
          aria-hidden="true"
        />
      </button>
      <button
        v-if="link.relationship !== 'origin'"
        type="button"
        class="link-row__btn"
        :aria-label="`Dismiss #${number}`"
        title="Dismiss"
        @click="store.dismiss(link.sessionId, link.id)"
      >
        <X
          :size="12"
          aria-hidden="true"
        />
      </button>
    </div>
  </div>
</template>

<style scoped>
.link-row {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr) auto;
  align-items: start;
  gap: 8px;
  margin-inline: -6px;
  padding: 6px;
  border-radius: var(--radius-btn);
}

.link-row__icon {
  margin-top: 2px;
}

.link-row__main {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}

.link-row__title {
  overflow: hidden;
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  text-decoration: none;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.link-row__title:hover {
  text-decoration: underline;
  text-underline-offset: 2px;
}

.link-row__detail {
  color: var(--muted);
  font-size: 11.5px;
}

.link-row__actions {
  display: flex;
  gap: 2px;
}

.link-row__btn {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.link-row__btn:hover {
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--text);
}

.link-row__btn:focus-visible,
.link-row__title:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}
</style>
