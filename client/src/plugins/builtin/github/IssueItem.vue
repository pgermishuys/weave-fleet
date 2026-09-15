<script setup lang="ts">
import { useRouter } from "@tanstack/vue-router";
import { computed } from "vue";
import { CheckCircle2, CircleDot, ExternalLink, MessageSquare } from "lucide-vue-next";
import { formatRelativeTime } from "@/lib/format-utils";
import CreateSessionFromGitHubDialog from "./components/CreateSessionFromGitHubDialog.vue";

interface GitHubLabel {
  name: string;
  color: string;
}

interface GitHubUser {
  login: string;
  avatarUrl: string;
}

interface GitHubIssueItemData {
  id: number;
  number: number;
  title: string;
  state: "open" | "closed";
  repoFullName: string;
  labels: readonly GitHubLabel[];
  user: GitHubUser;
  comments: number;
  updatedAt: string;
  htmlUrl: string;
}

const props = defineProps<{
  item: GitHubIssueItemData;
}>();

const emit = defineEmits<{
  labelClick: [label: string];
}>();

const router = useRouter();

const statusIcon = computed(() => (props.item.state === "open" ? CircleDot : CheckCircle2));
const statusClassName = computed(() => `gh-row__icon gh-row__icon--${props.item.state === "open" ? "open" : "closed"}`);
const relativeTime = computed(() => formatRelativeTime(props.item.updatedAt));

function getLabelStyle(color: string): { backgroundColor: string; borderColor: string; color: string } {
  return {
    backgroundColor: `#${color}22`,
    borderColor: `#${color}55`,
    // Tinted toward the text colour so pale labels stay readable on light themes.
    color: `color-mix(in srgb, #${color} 70%, var(--text))`,
  };
}

function getRepoRouteParams(): { owner: string; repo: string } | null {
  const [owner, repo] = props.item.repoFullName.split("/");

  if (!owner || !repo) {
    return null;
  }

  return { owner, repo };
}

function openIssue(): void {
  const params = getRepoRouteParams();

  if (!params) {
    window.open(props.item.htmlUrl, "_blank", "noopener,noreferrer");
    return;
  }

  void router.navigate({
    to: "/github/$owner/$repo/issues/$number",
    params: {
      ...params,
      number: String(props.item.number),
    },
  });
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    openIssue();
  }
}
</script>

<template>
  <article
    class="gh-row issue-item"
    role="button"
    tabindex="0"
    @click="openIssue"
    @keydown="handleKeydown"
  >
    <component
      :is="statusIcon"
      :class="statusClassName"
      :size="15"
      aria-hidden="true"
    />

    <div class="gh-row__body">
      <div class="gh-row__line">
        <p class="gh-row__title">
          {{ item.title }}
        </p>
        <span
          v-for="label in item.labels"
          :key="label.name"
          class="gh-row__label"
          :style="getLabelStyle(label.color)"
          @click.stop="emit('labelClick', label.name)"
        >
          {{ label.name }}
        </span>
      </div>

      <div class="gh-row__meta">
        <span>#{{ item.number }}</span>
        <span aria-hidden="true">·</span>
        <img
          v-if="item.user.avatarUrl"
          class="gh-row__avatar"
          :src="item.user.avatarUrl"
          alt=""
        >
        <span>{{ item.user.login }}</span>
        <span aria-hidden="true">·</span>
        <span>{{ relativeTime }}</span>
      </div>
    </div>

    <span
      v-if="item.comments > 0"
      class="gh-row__side"
      :aria-label="`${item.comments} comments`"
    >
      <MessageSquare
        :size="12"
        aria-hidden="true"
      />
      {{ item.comments }}
    </span>

    <div
      class="gh-row__actions"
      @click.stop
    >
      <CreateSessionFromGitHubDialog
        type="github-issue"
        :owner="item.repoFullName.split('/')[0] ?? ''"
        :repo="item.repoFullName.split('/')[1] ?? ''"
        :number="item.number"
        :title="item.title"
        :body="null"
        :html-url="item.htmlUrl"
        :repo-full-name="item.repoFullName"
      />
      <a
        class="gh-row__link"
        :href="item.htmlUrl"
        target="_blank"
        rel="noreferrer noopener"
        title="Open on GitHub"
        aria-label="Open on GitHub"
      >
        <ExternalLink
          :size="13"
          aria-hidden="true"
        />
      </a>
    </div>
  </article>
</template>

<style scoped>
/* A row in the repo's list, like a session row: title and labels, then who and when. */
.gh-row {
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 8px 10px;
  border-radius: var(--radius-btn);
  cursor: pointer;
  outline: none;
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
  flex-shrink: 0;
  margin-top: 2px;
}

.gh-row__icon--open {
  color: var(--running);
}

.gh-row__icon--closed {
  color: var(--muted);
}

.gh-row__icon--merged {
  color: var(--queued);
}

.gh-row__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.gh-row__line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 8px;
  min-width: 0;
}

.gh-row__title {
  margin: 0;
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  line-height: 1.4;
}

.gh-row__label {
  display: inline-flex;
  align-items: center;
  height: 18px;
  padding: 0 7px;
  border: 1px solid transparent;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}

.gh-row__label:hover {
  filter: brightness(1.15);
}

.gh-row__draft {
  height: 18px;
  padding: 0 7px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--muted);
  font-size: 11px;
  line-height: 18px;
}

.gh-row__meta {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  color: var(--muted);
  font-size: 12px;
}

.gh-row__avatar {
  width: 14px;
  height: 14px;
  border-radius: 999px;
  object-fit: cover;
}

.gh-row__branch {
  overflow: hidden;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.gh-row__side {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 4px;
  min-height: 20px;
  color: var(--muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.gh-row:hover .gh-row__side,
.gh-row:focus-within .gh-row__side {
  visibility: hidden;
}

/* Start a session or open on GitHub; they take the comment count's place on hover. */
.gh-row__actions {
  position: absolute;
  top: 6px;
  right: 8px;
  display: flex;
  align-items: center;
  gap: 2px;
  opacity: 0;
  pointer-events: none;
}

.gh-row:hover .gh-row__actions,
.gh-row:focus-within .gh-row__actions {
  opacity: 1;
  pointer-events: auto;
}

.gh-row__actions :deep(.create-session-trigger) {
  opacity: 1;
}

.gh-row__link {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--muted);
}

.gh-row__link:hover {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
}

@media (hover: none) {
  .gh-row__actions {
    display: none;
  }

  .gh-row:hover .gh-row__side {
    visibility: visible;
  }
}

@media (prefers-reduced-motion: reduce) {
  .gh-row {
    transition: none;
  }
}
</style>
