<script setup lang="ts">
import { computed, watch } from "vue";
import {
  CircleCheckBig,
  CircleDot,
  CircleX,
  Clock,
  GitPullRequest,
  MessageSquare,
  TriangleAlert,
  Zap,
} from "lucide-vue-next";
import type { SessionOrigin } from "@/api/client";
import SmartLinkIcon from "@/components/session-context/SmartLinkIcon.vue";
import {
  hasMergeConflict,
  isPullRequest,
  linkNumber,
  linkTitle,
  needsAttention,
  reviewThreads,
  summarizeChecks,
  type SmartLink,
} from "@/lib/smart-links";
import { useCanvasesStore } from "@/stores/canvases";
import { useSidebarStore } from "@/stores/sidebar";
import { useSmartLinksStore } from "@/stores/smart-links";

const MAX_CHIPS = 3;

const props = defineProps<{
  sessionId: string;
  origin?: SessionOrigin | null;
}>();

const smartLinks = useSmartLinksStore();
const canvases = useCanvasesStore();
const sidebar = useSidebarStore();

watch(
  () => props.sessionId,
  (sessionId) => {
    void smartLinks.ensureLoaded(sessionId);
  },
  { immediate: true },
);

const headerLinks = computed(() => smartLinks.headerLinks(props.sessionId));
const visibleLinks = computed(() => headerLinks.value.slice(0, MAX_CHIPS));
const overflow = computed(() => headerLinks.value.length - visibleLinks.value.length);

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
  if (isPullRequest(link) && !link.isTerminal) {
    const checks = summarizeChecks(link);
    if (checks.state !== "none") parts.push(`Checks: ${checks.text}`);
    const threads = reviewThreads(link).length;
    if (threads) parts.push(`${threads} unresolved review thread${threads === 1 ? "" : "s"}`);
    if (hasMergeConflict(link)) parts.push("Merge conflicts");
  }
  return parts.join(". ");
}

function checkIcon(link: SmartLink) {
  const state = summarizeChecks(link).state;
  if (state === "failing") return { icon: CircleX, tone: "bad" };
  if (state === "running") return { icon: Clock, tone: "warn" };
  if (state === "passed") return { icon: CircleCheckBig, tone: "good" };
  return null;
}

function show(target: string): void {
  sidebar.setRightPanelCollapsed(false);
  canvases.introduce(props.sessionId, "context");
  canvases.open(props.sessionId, "context");
  smartLinks.requestFocus(props.sessionId, target);
}
</script>

<template>
  <div
    v-if="automationOrigin || pendingOrigin || visibleLinks.length > 0"
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
      class="context-chip"
      :title="`Started from ${pendingOrigin.isPullRequest ? 'pull request' : 'issue'} #${pendingOrigin.number}: ${pendingOrigin.title}`"
      @click="show('origin')"
    >
      <component
        :is="pendingOrigin.isPullRequest ? GitPullRequest : CircleDot"
        :size="13"
        class="context-chip__pending"
        aria-hidden="true"
      />
      <span class="context-chip__number">#{{ pendingOrigin.number }}</span>
    </button>

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
      <template v-if="isPullRequest(link) && !link.isTerminal && link.enrichmentStatus === 'resolved'">
        <span
          v-if="checkIcon(link) || reviewThreads(link).length || hasMergeConflict(link)"
          class="context-chip__divider"
          aria-hidden="true"
        />
        <component
          :is="checkIcon(link)!.icon"
          v-if="checkIcon(link)"
          :size="12"
          class="context-chip__signal"
          :data-tone="checkIcon(link)!.tone"
          aria-hidden="true"
        />
        <span
          v-if="reviewThreads(link).length"
          class="context-chip__signal"
          data-tone="warn"
          aria-hidden="true"
        >
          <MessageSquare :size="12" />{{ reviewThreads(link).length }}
        </span>
        <TriangleAlert
          v-if="hasMergeConflict(link)"
          :size="12"
          class="context-chip__signal"
          data-tone="warn"
          aria-hidden="true"
        />
      </template>
    </button>

    <button
      v-if="overflow > 0"
      type="button"
      role="listitem"
      class="context-chip context-chip--more"
      :aria-label="`${overflow} more linked item${overflow === 1 ? '' : 's'}. Show in Context`"
      @click="show(headerLinks[MAX_CHIPS]!.id)"
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
  border-color: color-mix(in srgb, var(--error) 40%, transparent);
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

.context-chip__pending {
  flex-shrink: 0;
  color: var(--muted);
}

.context-chip__automation {
  flex-shrink: 0;
  color: var(--status-waiting);
}

.context-chip__divider {
  width: 1px;
  height: 12px;
  background: var(--border);
}

.context-chip__signal {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}

.context-chip__signal[data-tone="bad"] {
  color: var(--error);
}

.context-chip__signal[data-tone="warn"] {
  color: var(--status-waiting);
}

.context-chip__signal[data-tone="good"] {
  color: var(--running);
}

@media (prefers-reduced-motion: reduce) {
  .context-chip {
    transition: none;
  }
}
</style>
