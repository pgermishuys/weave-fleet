<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import MarkdownIt from "markdown-it";
import hljs from "highlight.js";
import {
  CheckCircle2,
  CircleDot,
  ExternalLink,
  GitCommitHorizontal,
  GitMerge,
  GitPullRequest,
  GitPullRequestClosed,
  LoaderCircle,
  MessageSquare,
  RefreshCw,
  TriangleAlert,
} from "lucide-vue-next";
import { apiFetch } from "@/lib/api-client";
import { formatRelativeTime } from "@/lib/format-utils";
import type { GitHubIssue, GitHubPullRequest } from "@/plugins/builtin/github/composables/github-types";

interface GitHubComment {
  id: number;
  body: string | null;
  html_url: string;
  created_at: string;
  updated_at: string;
  user: {
    login: string;
    avatar_url: string;
  };
}

interface Props {
  owner: string;
  repo: string;
  number: string;
  kind: "issue" | "pull";
}

const props = defineProps<Props>();

const detail = shallowRef<GitHubIssue | GitHubPullRequest | null>(null);
const comments = shallowRef<readonly GitHubComment[]>([]);
const errorMessage = shallowRef<string | null>(null);
const commentsError = shallowRef<string | null>(null);
const isLoading = shallowRef(true);
const isRefreshing = shallowRef(false);

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

const markdownRenderer = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
  highlight(code, language) {
    if (language && hljs.getLanguage(language)) {
      return `<pre class="hljs"><code>${hljs.highlight(code, {
        language,
        ignoreIllegals: true,
      }).value}</code></pre>`;
    }

    return `<pre class="hljs"><code>${escapeHtml(code)}</code></pre>`;
  },
});

const isPullRequest = computed(() => props.kind === "pull");
const parsedNumber = computed(() => Number.parseInt(props.number, 10));
const hasValidNumber = computed(() => Number.isInteger(parsedNumber.value) && parsedNumber.value > 0);
const repoFullName = computed(() => `${props.owner}/${props.repo}`);
const detailLabel = computed(() => (isPullRequest.value ? "pull request" : "issue"));
const detailHeading = computed(() => (isPullRequest.value ? "Pull request" : "Issue"));
const detailEndpoint = computed(() => {
  const pathType = isPullRequest.value ? "pulls" : "issues";
  return `/api/integrations/github/repos/${encodeURIComponent(props.owner)}/${encodeURIComponent(props.repo)}/${pathType}/${encodeURIComponent(props.number)}`;
});
const commentsEndpoint = computed(() => {
  const pathType = isPullRequest.value ? "pulls" : "issues";
  return `${detailEndpoint.value}/comments`;
});

const resolvedState = computed(() => {
  if (!detail.value) {
    return null;
  }

  if (isPullRequest.value) {
    const pullRequest = detail.value as GitHubPullRequest;
    return pullRequest.merged_at ? "merged" : pullRequest.state;
  }

  const issue = detail.value as GitHubIssue;
  return issue.state;
});

const statusIcon = computed(() => {
  switch (resolvedState.value) {
    case "merged":
      return GitMerge;
    case "closed":
      return isPullRequest.value ? GitPullRequestClosed : CheckCircle2;
    case "open":
    default:
      return isPullRequest.value ? GitPullRequest : CircleDot;
  }
});

const statusClassName = computed(() => {
  switch (resolvedState.value) {
    case "merged":
      return "status-icon status-icon--merged";
    case "closed":
      return "status-icon status-icon--closed";
    case "open":
    default:
      return "status-icon status-icon--open";
  }
});

const title = computed(() => detail.value?.title ?? `${detailHeading.value} #${props.number}`);
const bodyHtml = computed(() => renderMarkdown(detail.value?.body));
const labels = computed(() => detail.value?.labels ?? []);
const authorLogin = computed(() => detail.value?.user.login ?? "Unknown user");
const authorAvatarUrl = computed(() => detail.value?.user.avatar_url ?? "");
const htmlUrl = computed(() => detail.value?.html_url ?? `https://github.com/${repoFullName.value}/${isPullRequest.value ? "pull" : "issues"}/${props.number}`);
const createdAtLabel = computed(() => (detail.value ? formatDateTime(detail.value.created_at) : "—"));
const updatedAtLabel = computed(() => (detail.value ? formatDateTime(detail.value.updated_at) : "—"));
const updatedRelativeLabel = computed(() => (detail.value ? formatRelativeTime(detail.value.updated_at) : ""));
const commentsCountLabel = computed(() => {
  const count = detail.value?.comments ?? comments.value.length;
  return `${count} comment${count === 1 ? "" : "s"}`;
});
const pullRequestStats = computed(() => {
  if (!detail.value || !isPullRequest.value) {
    return null;
  }

  const pullRequest = detail.value as GitHubPullRequest;
  return {
    additions: pullRequest.additions,
    deletions: pullRequest.deletions,
    changedFiles: pullRequest.changed_files,
    baseRef: pullRequest.base.ref,
    headRef: pullRequest.head.ref,
  };
});

function renderMarkdown(markdown: string | null | undefined): string {
  if (!markdown || !markdown.trim()) {
    return "";
  }

  return markdownRenderer.render(markdown);
}

function formatDateTime(value: string): string {
  return new Date(value).toLocaleString();
}

function getLabelStyle(color: string): { backgroundColor: string; borderColor: string; color: string } {
  return {
    backgroundColor: `#${color}22`,
    borderColor: `#${color}55`,
    color: `#${color}`,
  };
}

async function fetchDetail(signal?: AbortSignal): Promise<void> {
  detail.value = null;
  comments.value = [];
  commentsError.value = null;
  errorMessage.value = null;

  if (!props.owner || !props.repo || !hasValidNumber.value) {
    errorMessage.value = `Invalid GitHub ${detailLabel.value} URL.`;
    return;
  }

  try {
    const detailResponse = await apiFetch(detailEndpoint.value, { signal });
    if (!detailResponse.ok) {
      throw new Error(
        detailResponse.status === 404
          ? `GitHub ${detailLabel.value} not found.`
          : `Unable to load GitHub ${detailLabel.value} (HTTP ${detailResponse.status}).`,
      );
    }

    detail.value = (await detailResponse.json()) as GitHubIssue | GitHubPullRequest;

    const commentsResponse = await apiFetch(commentsEndpoint.value, { signal });
    if (!commentsResponse.ok) {
      commentsError.value = `Unable to load comments (HTTP ${commentsResponse.status}).`;
      return;
    }

    comments.value = (await commentsResponse.json()) as readonly GitHubComment[];
  } catch (error) {
    if (signal?.aborted) {
      return;
    }

    detail.value = null;
    comments.value = [];
    errorMessage.value = error instanceof Error ? error.message : `Unable to load GitHub ${detailLabel.value}.`;
  }
}

watch(
  () => [props.owner, props.repo, props.number, props.kind] as const,
  async (_value, _previousValue, onCleanup) => {
    const abortController = new AbortController();
    onCleanup(() => {
      abortController.abort();
    });

    isLoading.value = true;
    await fetchDetail(abortController.signal);

    if (!abortController.signal.aborted) {
      isLoading.value = false;
    }
  },
  { immediate: true },
);

async function handleRefresh(): Promise<void> {
  isRefreshing.value = true;
  await fetchDetail();
  isRefreshing.value = false;
}
</script>

<template>
  <section class="detail-page" :aria-busy="isLoading || isRefreshing">
    <div v-if="isLoading" class="detail-state detail-state--loading">
      <LoaderCircle :size="18" class="detail-state__icon detail-state__icon--spin" aria-hidden="true" />
      <span>Loading GitHub {{ detailLabel }}…</span>
    </div>

    <div v-else-if="errorMessage" class="detail-state detail-state--error" role="alert">
      <TriangleAlert :size="18" class="detail-state__icon" aria-hidden="true" />
      <div class="detail-state__copy">
        <p class="detail-state__title">Unable to load GitHub {{ detailLabel }}</p>
        <p>{{ errorMessage }}</p>
      </div>
      <button type="button" class="detail-action-button" @click="handleRefresh">
        <RefreshCw :size="14" :class="{ 'detail-state__icon--spin': isRefreshing }" aria-hidden="true" />
        Retry
      </button>
    </div>

    <article v-else-if="detail" class="detail-shell">
      <header class="detail-hero">
        <div class="detail-hero__content">
          <div class="detail-status-row">
            <component :is="statusIcon" :class="statusClassName" :size="16" aria-hidden="true" />
            <span class="detail-status-row__state">{{ resolvedState }}</span>
            <span aria-hidden="true">•</span>
            <span>{{ repoFullName }}</span>
            <span aria-hidden="true">•</span>
            <span>#{{ detail.number }}</span>
          </div>

          <h1 class="detail-title">{{ title }}</h1>

          <div class="detail-labels" aria-label="GitHub labels">
            <span
              v-for="label in labels"
              :key="label.name"
              class="detail-label"
              :style="getLabelStyle(label.color)"
            >
              {{ label.name }}
            </span>
          </div>
        </div>

        <div class="detail-actions">
          <button type="button" class="detail-action-button" :disabled="isRefreshing" @click="handleRefresh">
            <RefreshCw :size="14" :class="{ 'detail-state__icon--spin': isRefreshing }" aria-hidden="true" />
            Refresh
          </button>

          <a class="detail-link-button" :href="htmlUrl" target="_blank" rel="noreferrer noopener">
            <ExternalLink :size="14" aria-hidden="true" />
            Open on GitHub
          </a>
        </div>
      </header>

      <section class="detail-panel detail-metadata" :aria-label="`${detailHeading} metadata`">
        <div class="detail-meta-item">
          <span class="detail-meta-item__label">Author</span>
          <div class="detail-author">
            <img v-if="authorAvatarUrl" class="detail-author__avatar" :src="authorAvatarUrl" :alt="`${authorLogin} avatar`">
            <span class="detail-author__name">{{ authorLogin }}</span>
          </div>
        </div>

        <div class="detail-meta-item">
          <span class="detail-meta-item__label">Created</span>
          <span class="detail-meta-item__value">{{ createdAtLabel }}</span>
        </div>

        <div class="detail-meta-item">
          <span class="detail-meta-item__label">Updated</span>
          <span class="detail-meta-item__value">{{ updatedAtLabel }}</span>
          <span class="detail-meta-item__hint">{{ updatedRelativeLabel }}</span>
        </div>

        <div class="detail-meta-item">
          <span class="detail-meta-item__label">Discussion</span>
          <span class="detail-meta-item__value">{{ commentsCountLabel }}</span>
        </div>

        <template v-if="pullRequestStats">
          <div class="detail-meta-item">
            <span class="detail-meta-item__label">Branches</span>
            <div class="detail-branch-list">
              <span>{{ pullRequestStats.headRef }}</span>
              <GitCommitHorizontal :size="14" aria-hidden="true" />
              <span>{{ pullRequestStats.baseRef }}</span>
            </div>
          </div>

          <div class="detail-meta-item">
            <span class="detail-meta-item__label">Changes</span>
            <div class="detail-change-list">
              <span class="detail-change-list__additions">+{{ pullRequestStats.additions }}</span>
              <span class="detail-change-list__deletions">-{{ pullRequestStats.deletions }}</span>
              <span>{{ pullRequestStats.changedFiles }} files</span>
            </div>
          </div>
        </template>
      </section>

      <section class="detail-panel" aria-label="Overview">
        <h2 class="detail-panel__title">Overview</h2>

        <div v-if="bodyHtml" class="detail-markdown" v-html="bodyHtml" />
        <p v-else class="detail-panel__empty">No description was provided for this {{ detailLabel }}.</p>
      </section>

      <section class="detail-panel" aria-label="Comments">
        <div class="detail-panel__header">
          <h2 class="detail-panel__title">Comments</h2>
          <span class="detail-panel__count">
            <MessageSquare :size="14" aria-hidden="true" />
            {{ commentsCountLabel }}
          </span>
        </div>

        <p v-if="commentsError" class="detail-panel__banner detail-panel__banner--warning">
          {{ commentsError }}
        </p>

        <p v-if="comments.length === 0" class="detail-panel__empty">
          No comments yet.
        </p>

        <div v-else class="detail-comment-list">
          <article v-for="comment in comments" :key="comment.id" class="detail-comment">
            <header class="detail-comment__header">
              <div class="detail-author">
                <img class="detail-author__avatar" :src="comment.user.avatar_url" :alt="`${comment.user.login} avatar`">
                <span class="detail-author__name">{{ comment.user.login }}</span>
              </div>

              <div class="detail-comment__meta">
                <span>{{ formatDateTime(comment.updated_at) }}</span>
                <span aria-hidden="true">•</span>
                <span>{{ formatRelativeTime(comment.updated_at) }}</span>
              </div>
            </header>

            <div v-if="renderMarkdown(comment.body)" class="detail-markdown" v-html="renderMarkdown(comment.body)" />
            <p v-else class="detail-panel__empty">Comment body is empty.</p>

            <footer class="detail-comment__footer">
              <a :href="comment.html_url" target="_blank" rel="noreferrer noopener">View on GitHub</a>
            </footer>
          </article>
        </div>
      </section>
    </article>
  </section>
</template>

<style scoped>
.detail-page {
  height: 100%;
  overflow: auto;
  padding: 24px;
}

.detail-shell {
  display: flex;
  max-width: 1080px;
  margin: 0 auto;
  flex-direction: column;
  gap: 20px;
}

.detail-hero,
.detail-panel,
.detail-state {
  border: 1px solid var(--border);
  border-radius: 16px;
  background: var(--card-bg);
}

.detail-hero {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 16px;
  padding: 24px;
}

.detail-hero__content {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 12px;
}

.detail-status-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  color: var(--muted);
}

.detail-status-row__state {
  font-weight: 600;
  text-transform: capitalize;
  color: var(--text);
}

.detail-title {
  margin: 0;
  font-size: 28px;
  line-height: 1.2;
  color: var(--text);
}

.detail-labels {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.detail-label {
  display: inline-flex;
  align-items: center;
  min-height: 22px;
  padding: 0 8px;
  border: 1px solid transparent;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
}

.detail-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.detail-action-button,
.detail-link-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  min-height: 34px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  font-size: 12px;
  font-weight: 600;
  text-decoration: none;
}

.detail-action-button:disabled {
  opacity: 0.7;
  cursor: not-allowed;
}

.detail-metadata {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 16px;
  padding: 20px 24px;
}

.detail-meta-item {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 6px;
}

.detail-meta-item__label {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--muted);
}

.detail-meta-item__value {
  font-size: 14px;
  color: var(--text);
}

.detail-meta-item__hint {
  font-size: 12px;
  color: var(--muted);
}

.detail-author {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.detail-author__avatar {
  width: 24px;
  height: 24px;
  border-radius: 999px;
  object-fit: cover;
}

.detail-author__name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text);
}

.detail-branch-list,
.detail-change-list {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  color: var(--text);
}

.detail-change-list__additions {
  color: #22c55e;
}

.detail-change-list__deletions {
  color: #ef4444;
}

.detail-panel {
  padding: 24px;
}

.detail-panel__header {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.detail-panel__title {
  margin: 0 0 16px;
  font-size: 18px;
  color: var(--text);
}

.detail-panel__count {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--muted);
}

.detail-panel__empty,
.detail-panel__banner {
  margin: 0;
  font-size: 14px;
  color: var(--muted);
}

.detail-panel__banner--warning {
  margin-bottom: 16px;
  color: #fca5a5;
}

.detail-comment-list {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.detail-comment {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding-top: 16px;
  border-top: 1px solid var(--border);
}

.detail-comment:first-child {
  padding-top: 0;
  border-top: 0;
}

.detail-comment__header,
.detail-comment__footer {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.detail-comment__meta,
.detail-comment__footer a {
  font-size: 12px;
  color: var(--muted);
}

.detail-comment__meta {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.detail-markdown {
  color: var(--text);
  line-height: 1.65;
}

.detail-markdown :deep(p),
.detail-markdown :deep(ul),
.detail-markdown :deep(ol),
.detail-markdown :deep(pre),
.detail-markdown :deep(blockquote),
.detail-markdown :deep(h1),
.detail-markdown :deep(h2),
.detail-markdown :deep(h3),
.detail-markdown :deep(h4) {
  margin: 0 0 12px;
}

.detail-markdown :deep(ul),
.detail-markdown :deep(ol) {
  padding-left: 20px;
}

.detail-markdown :deep(pre) {
  overflow: auto;
  padding: 12px;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.04);
}

.detail-markdown :deep(code) {
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Menlo, Monaco, Consolas, Liberation Mono, Courier New, monospace;
}

.detail-markdown :deep(a) {
  color: var(--accent);
}

.detail-state {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  max-width: 960px;
  margin: 0 auto;
  padding: 18px 20px;
}

.detail-state--loading {
  align-items: center;
  justify-content: center;
}

.detail-state--error {
  color: #fca5a5;
}

.detail-state__copy {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
}

.detail-state__title {
  margin: 0;
  font-weight: 700;
}

.detail-state__copy p:last-child {
  margin: 0;
}

.detail-state__icon {
  flex-shrink: 0;
}

.detail-state__icon--spin {
  animation: detail-spin 1s linear infinite;
}

.status-icon {
  flex-shrink: 0;
}

.status-icon--open {
  color: #22c55e;
}

.status-icon--closed {
  color: var(--muted);
}

.status-icon--merged {
  color: #a855f7;
}

@keyframes detail-spin {
  from {
    transform: rotate(0deg);
  }

  to {
    transform: rotate(360deg);
  }
}

@media (max-width: 720px) {
  .detail-page {
    padding: 16px;
  }

  .detail-hero,
  .detail-panel,
  .detail-metadata {
    padding: 18px;
  }

  .detail-title {
    font-size: 22px;
  }
}
</style>
