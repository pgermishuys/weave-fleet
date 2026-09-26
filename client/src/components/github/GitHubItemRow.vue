<script setup lang="ts">
import { computed } from "vue";
import { ExternalLink, MessageSquare, Plus } from "lucide-vue-next";
import type { SessionListItem } from "@/api/client";
import CheckIcon from "@/components/github/CheckIcon.vue";
import DiffStat from "@/components/github/DiffStat.vue";
import GitHubItemIcon from "@/components/github/GitHubItemIcon.vue";
import GitHubLabel from "@/components/github/GitHubLabel.vue";
import GitHubSessionChip from "@/components/github/GitHubSessionChip.vue";
import ReviewerAvatar from "@/components/github/ReviewerAvatar.vue";
import { formatRelativeTime } from "@/lib/format-utils";
import { itemPrFacts, type GitHubItemSummary } from "@/lib/github-items";
import { prState, prWords } from "@/lib/pr-state";

/**
 * A pull request or issue in a list: what it is on the left; whether it can merge and whether Fleet is on it on
 * the right. Hovering swaps the right side for "Start session" (or "Open session") and a GitHub link.
 */
const props = withDefaults(defineProps<{
  item: GitHubItemSummary;
  sessions?: SessionListItem[];
  /** Show `owner/repo` before the number, for lists across repositories. */
  showRepo?: boolean;
  /** Why the item is in a "Needs you" list: "Review requested", "client-tests failing". */
  reason?: string | null;
}>(), {
  sessions: () => [],
  showRepo: false,
  reason: null,
});

const emit = defineEmits<{
  open: [item: GitHubItemSummary];
  openSession: [session: SessionListItem];
  start: [item: GitHubItemSummary];
  label: [name: string];
}>();

const isPull = computed(() => props.item.kind === "pull");
const facts = computed(() => itemPrFacts(props.item));
const state = computed(() => {
  if (!isPull.value) return props.item.state === "closed" ? "closed" : "open";
  return prState(facts.value);
});
const checkState = computed(() => {
  switch (facts.value.checks) {
    case "failing": return "failure";
    case "pending": return "pending";
    case "passing": return "success";
    default: return null;
  }
});
// Reviewers who've said something; waiting ones show on the pull request's page.
const verdicts = computed(() => props.item.reviewers.filter((r) => r.state === "APPROVED" || r.state === "CHANGES_REQUESTED").slice(0, 3));
const description = computed(() => {
  const kind = isPull.value ? "Pull request" : "Issue";
  const parts = [`${kind} #${props.item.number}: ${props.item.title}`];
  if (isPull.value && props.item.state === "open") parts.push(prWords(facts.value));
  if (props.item.comments) parts.push(`${props.item.comments} comment${props.item.comments === 1 ? "" : "s"}`);
  return parts.join(" · ");
});
const updated = computed(() => formatRelativeTime(props.item.updatedAt));

function handleKeydown(event: KeyboardEvent): void {
  if (event.target !== event.currentTarget) return;
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    emit("open", props.item);
  }
}
</script>

<template>
  <div
    class="gh-row"
    role="button"
    tabindex="0"
    :title="description"
    :aria-label="description"
    data-testid="github-row"
    @click="emit('open', item)"
    @keydown="handleKeydown"
  >
    <GitHubItemIcon
      class="gh-row__icon"
      :kind="item.kind"
      :state="state"
      :size="15"
    />

    <div class="gh-row__line">
      <span class="gh-row__title">{{ item.title }}</span>
      <GitHubLabel
        v-for="label in item.labels.slice(0, 3)"
        :key="label.name"
        :name="label.name"
        :color="label.color"
        @click.stop="emit('label', label.name)"
      />
    </div>

    <div class="gh-row__meta">
      <span v-if="showRepo">{{ item.owner }}/{{ item.repo }}</span>
      <span>#{{ item.number }}</span>
      <template v-if="item.author">
        <span aria-hidden="true">·</span>
        <ReviewerAvatar
          :login="item.author"
          :avatar-url="item.authorAvatarUrl"
          :size="14"
        />
        <span>{{ item.author }}</span>
      </template>
      <span aria-hidden="true">·</span>
      <span>{{ updated }}</span>
      <template v-if="item.headRef">
        <span aria-hidden="true">·</span>
        <span class="gh-row__branch">{{ item.headRef }}</span>
      </template>
    </div>

    <div class="gh-row__trail">
      <span class="gh-row__signals">
        <span
          v-if="reason"
          class="gh-row__reason"
        >{{ reason }}</span>
        <GitHubSessionChip
          v-if="sessions.length"
          :sessions="sessions"
          @open="emit('openSession', $event)"
        />
        <span
          v-if="verdicts.length"
          class="gh-row__reviewers"
        >
          <ReviewerAvatar
            v-for="reviewer in verdicts"
            :key="reviewer.login"
            :login="reviewer.login"
            :avatar-url="reviewer.avatarUrl"
            :state="reviewer.state"
            :size="16"
          />
        </span>
        <CheckIcon
          v-if="checkState"
          :state="checkState"
          :size="14"
        />
        <DiffStat
          v-if="item.additions !== null && item.deletions !== null"
          class="gh-row__diff"
          :additions="item.additions"
          :deletions="item.deletions"
        />
      </span>
      <span class="gh-row__actions">
        <button
          v-if="sessions.length"
          type="button"
          class="gh-row__btn"
          @click.stop="emit('openSession', sessions[0])"
        >
          <MessageSquare
            :size="12"
            aria-hidden="true"
          />
          Open session
        </button>
        <button
          v-else
          type="button"
          class="gh-row__btn gh-row__btn--primary"
          data-testid="github-row-start"
          @click.stop="emit('start', item)"
        >
          <Plus
            :size="12"
            aria-hidden="true"
          />
          Start session
        </button>
        <a
          class="gh-row__btn gh-row__btn--ghost"
          :href="item.url"
          target="_blank"
          rel="noreferrer noopener"
          aria-label="Open on GitHub"
          title="Open on GitHub"
          @click.stop
        >
          <ExternalLink
            :size="12"
            aria-hidden="true"
          />
        </a>
      </span>
    </div>
  </div>
</template>

<style scoped>
.gh-row {
  display: grid;
  grid-template-columns: 18px minmax(0, 1fr) auto;
  gap: 2px 10px;
  align-items: start;
  padding: 8px 10px;
  border-radius: var(--radius-btn);
  color: var(--text);
  cursor: pointer;
  transition: background-color var(--transition);
}

.gh-row:hover,
.gh-row:focus-within {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.gh-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.gh-row__icon {
  grid-row: span 2;
  margin-top: 2px;
}

.gh-row__line {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.gh-row__title {
  min-width: 0;
  overflow: hidden;
  font-size: 13px;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.gh-row__meta {
  grid-column: 2;
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
}

.gh-row__branch {
  overflow: hidden;
  color: color-mix(in srgb, var(--muted) 85%, transparent);
  font-family: var(--font-mono-stack);
  font-size: 11px;
  text-overflow: ellipsis;
}

.gh-row__trail {
  grid-row: 1 / span 2;
  grid-column: 3;
  align-self: center;
  display: grid;
  justify-items: end;
}

.gh-row__signals,
.gh-row__actions {
  grid-area: 1 / 1;
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--muted);
}

.gh-row__actions {
  gap: 4px;
  visibility: hidden;
}

.gh-row:hover .gh-row__actions,
.gh-row:focus-within .gh-row__actions {
  visibility: visible;
}

.gh-row:hover .gh-row__signals,
.gh-row:focus-within .gh-row__signals {
  visibility: hidden;
}

.gh-row__reason {
  color: var(--pr-blocked);
  font-size: 11.5px;
  white-space: nowrap;
}

.gh-row__reviewers {
  display: inline-flex;
  gap: 5px;
  padding-right: 2px;
}

.gh-row__diff {
  min-width: 72px;
  justify-content: flex-end;
}

.gh-row__btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 24px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  text-decoration: none;
  cursor: pointer;
}

.gh-row__btn--primary {
  border-color: transparent;
  background: var(--accent);
  color: var(--primary-foreground);
}

.gh-row__btn--ghost {
  border-color: transparent;
  background: transparent;
  color: var(--muted);
}

.gh-row__btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

@media (hover: none) {
  .gh-row__actions { display: none; }
  .gh-row:hover .gh-row__signals { visibility: visible; }
}
</style>
