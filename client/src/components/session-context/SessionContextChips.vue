<script setup lang="ts">
import { computed, watch } from "vue";
import { CircleDot, GitPullRequest, Zap } from "lucide-vue-next";
import type { SessionOrigin } from "@/api/client";
import PullRequestPill from "@/components/session-context/PullRequestPill.vue";
import SmartLinkIcon from "@/components/session-context/SmartLinkIcon.vue";
import {
  isPullRequest,
  linkNumber,
  linkTitle,
  needsAttention,
  type SmartLink,
} from "@/lib/smart-links";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { useCanvasesStore } from "@/stores/canvases";
import { useSmartLinksStore } from "@/stores/smart-links";

const MAX_CHIPS = 2;

const props = defineProps<{
  sessionId: string;
  origin?: SessionOrigin | null;
}>();

const smartLinks = useSmartLinksStore();
const canvases = useCanvasesStore();
const { showRightPanel } = useSidebarMobile();

watch(
  () => props.sessionId,
  (sessionId) => {
    void smartLinks.ensureLoaded(sessionId);
  },
  { immediate: true },
);

const headerLinks = computed(() => smartLinks.headerLinks(props.sessionId));
// The pull request the session is about gets the pill; the rest stay small.
const pullRequest = computed(() => smartLinks.sessionPullRequest(props.sessionId));
const originLink = computed(() => headerLinks.value.find((link) => link.relationship === "origin" && link.id !== pullRequest.value?.id) ?? null);
const otherLinks = computed(() => headerLinks.value.filter((link) => link.id !== pullRequest.value?.id && link.id !== originLink.value?.id));
const visibleLinks = computed(() => otherLinks.value.slice(0, MAX_CHIPS));
const overflow = computed(() => otherLinks.value.length - visibleLinks.value.length);

const automationOrigin = computed(() =>
  props.origin?.sourceType === "automation" ? props.origin : null,
);

// A GitHub origin shows from its record until the server has added it as a link.
const pendingOrigin = computed(() => {
  const origin = props.origin;
  if (!origin?.resourceUrl || origin.providerId !== "builtin.github") return null;
  if (headerLinks.value.some((link) => link.relationship === "origin" || link.url === origin.resourceUrl)) return null;
  const number = /\/(?:pull|issues)\/(\d+)/.exec(origin.resourceUrl)?.[1];
  return number ? { number, title: origin.title ?? origin.resourceUrl, isPullRequest: origin.sourceType === "github-pull-request" } : null;
});

function describe(link: SmartLink): string {
  const kind = isPullRequest(link) ? "Pull request" : "Issue";
  const parts = [`${kind} #${linkNumber(link) ?? ""}: ${linkTitle(link)}`];
  if (link.relationship === "origin") parts.push("Session started from this");
  if (link.enrichmentStatus === "resolved" && link.statusLabel) parts.push(link.statusLabel);
  return parts.join(". ");
}

function show(target: string): void {
  showRightPanel();
  canvases.introduce(props.sessionId, "context");
  canvases.open(props.sessionId, "context");
  smartLinks.requestFocus(props.sessionId, target);
}
</script>

<template>
  <div
    v-if="automationOrigin || pendingOrigin || headerLinks.length > 0"
    class="context-chips"
    role="list"
    aria-label="Session context"
  >
    <button
      v-if="automationOrigin"
      type="button"
      role="listitem"
      class="context-chip"
      :title="`Started by automation: ${automationOrigin.title ?? 'automation'}`"
      :aria-label="`Started by automation ${automationOrigin.title ?? ''}. Show in Context`"
      @click="show('origin')"
    >
      <Zap
        :size="13"
        class="context-chip__automation"
        aria-hidden="true"
      />
      <span class="context-chip__name">{{ automationOrigin.title ?? "Automation" }}</span>
    </button>

    <button
      v-if="pendingOrigin"
      type="button"
      role="listitem"
      class="context-origin"
      :title="`Started from ${pendingOrigin.isPullRequest ? 'pull request' : 'issue'} #${pendingOrigin.number}: ${pendingOrigin.title}`"
      @click="show('origin')"
    >
      <component
        :is="pendingOrigin.isPullRequest ? GitPullRequest : CircleDot"
        :size="13"
        aria-hidden="true"
      />
      from <span class="context-chip__number">#{{ pendingOrigin.number }}</span>
    </button>

    <button
      v-if="originLink"
      type="button"
      role="listitem"
      class="context-origin"
      :title="describe(originLink)"
      :aria-label="`${describe(originLink)}. Show in Context`"
      :data-testid="`context-chip-${originLink.id}`"
      @click="show(originLink.id)"
    >
      <SmartLinkIcon
        :link="originLink"
        :size="13"
      />
      from <span class="context-chip__number">#{{ linkNumber(originLink) }}</span>
    </button>

    <span
      v-if="pullRequest"
      role="listitem"
      class="context-chips__pill"
    >
      <PullRequestPill
        :link="pullRequest"
        @show-context="show"
      />
    </span>

    <button
      v-for="link in visibleLinks"
      :key="link.id"
      type="button"
      role="listitem"
      class="context-chip"
      :class="{ 'context-chip--attention': needsAttention(link) }"
      :title="describe(link)"
      :aria-label="`${describe(link)}. Show in Context`"
      :data-testid="`context-chip-${link.id}`"
      @click="show(link.id)"
    >
      <SmartLinkIcon
        :link="link"
        :size="13"
      />
      <span class="context-chip__number">#{{ linkNumber(link) }}</span>
    </button>

    <button
      v-if="overflow > 0"
      type="button"
      role="listitem"
      class="context-chip context-chip--more"
      :aria-label="`${overflow} more linked pull requests and issues. Show in Context`"
      @click="show(otherLinks[MAX_CHIPS]?.id ?? '')"
    >
      +{{ overflow }}
    </button>
  </div>
</template>

<style scoped>
.context-chips {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.context-chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  max-width: 220px;
  padding: 0 9px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  font-size: 12px;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color var(--transition), border-color var(--transition);
}

.context-chip:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.context-chip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.context-chip--attention {
  border-color: color-mix(in srgb, var(--pr-blocked) 45%, transparent);
}

/* Where the session came from: quiet, since it rarely changes. */
.context-origin {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 7px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.context-origin:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.context-origin:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.context-chips__pill {
  display: inline-flex;
  min-width: 0;
}

.context-chip--more {
  color: var(--muted);
}

.context-chip__number {
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.context-chip__name {
  overflow: hidden;
  text-overflow: ellipsis;
}


.context-chip__automation {
  flex-shrink: 0;
  color: var(--status-waiting);
}






@media (prefers-reduced-motion: reduce) {
  .context-chip {
    transition: none;
  }
}
</style>
