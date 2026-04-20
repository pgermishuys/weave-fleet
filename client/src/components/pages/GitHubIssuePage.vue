<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { AlertCircle, CheckCircle2, CircleDot, ExternalLink, LoaderCircle, MessageSquare, RefreshCw } from "lucide-vue-next";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { apiFetch } from "@/lib/api-client";
import { formatRelativeTime } from "@/lib/format-utils";
import type { GitHubIssue } from "@/plugins/builtin/github/composables/github-types";

interface GitHubIssueComment {
  id: number;
  body: string;
  htmlUrl: string | null;
  createdAt: string;
  updatedAt: string;
  user: {
    login: string;
    avatarUrl: string;
  };
}

interface Props {
  owner?: string | null;
  repo?: string | null;
  issueNumber?: number | null;
  repositoryFullName?: string | null;
  issue?: GitHubIssue | null;
  comments?: readonly GitHubIssueComment[] | null;
}

const props = withDefaults(defineProps<Props>(), {
  owner: null,
  repo: null,
  issueNumber: null,
  repositoryFullName: null,
  issue: null,
  comments: null,
});

const fetchedIssue = shallowRef<GitHubIssue | null>(null);
const fetchedComments = shallowRef<GitHubIssueComment[]>([]);
const isLoadingIssue = shallowRef(false);
const isLoadingComments = shallowRef(false);
const issueError = shallowRef<string | null>(null);
const commentsError = shallowRef<string | null>(null);
const issueReloadKey = shallowRef(0);
const commentsReloadKey = shallowRef(0);

function getRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : null;
}

function getString(value: Record<string, unknown>, key: string): string | null {
  const candidate = value[key];
  return typeof candidate === "string" ? candidate : null;
}

function getNumber(value: Record<string, unknown>, key: string): number | null {
  const candidate = value[key];
  return typeof candidate === "number" ? candidate : null;
}

function readErrorMessage(response: Response, fallback: string): Promise<string> {
  return response
    .json()
    .then((payload: unknown) => {
      const record = getRecord(payload);
      return getString(record ?? {}, "error") ?? getString(record ?? {}, "message") ?? fallback;
    })
    .catch(() => fallback);
}

function normalizeComment(payload: unknown): GitHubIssueComment | null {
  const value = getRecord(payload);
  if (!value) {
    return null;
  }

  const id = getNumber(value, "id");
  const user = getRecord(value.user);
  const login = user ? getString(user, "login") : null;
  const avatarUrl = user ? getString(user, "avatar_url") : null;

  if (id === null || login === null || avatarUrl === null) {
    return null;
  }

  return {
    id,
    body: getString(value, "body") ?? "",
    htmlUrl: getString(value, "html_url"),
    createdAt: getString(value, "created_at") ?? "",
    updatedAt: getString(value, "updated_at") ?? "",
    user: {
      login,
      avatarUrl,
    },
  };
}

function buildLabelStyle(color: string): { backgroundColor: string; borderColor: string; color: string } {
  return {
    backgroundColor: `#${color}22`,
    borderColor: `#${color}55`,
    color: `#${color}`,
  };
}

function formatAbsoluteDate(timestamp: string): string {
  if (!timestamp) {
    return "Unknown date";
  }

  const parsed = new Date(timestamp);
  return Number.isNaN(parsed.getTime()) ? "Unknown date" : parsed.toLocaleString();
}

function getAvatarFallback(login: string): string {
  const trimmedLogin = login.trim();
  return trimmedLogin ? trimmedLogin.slice(0, 2).toUpperCase() : "GH";
}

function retryIssueLoad(): void {
  issueReloadKey.value += 1;
}

function retryCommentsLoad(): void {
  commentsReloadKey.value += 1;
}

function openIssueOnGitHub(): void {
  if (!effectiveIssue.value?.html_url) {
    return;
  }

  window.open(effectiveIssue.value.html_url, "_blank", "noopener,noreferrer");
}

watch(
  () => [props.owner, props.repo, props.issueNumber, props.issue, issueReloadKey.value] as const,
  async ([owner, repo, issueNumber, issue], _previousValue, onCleanup) => {
    if (issue) {
      fetchedIssue.value = null;
      isLoadingIssue.value = false;
      issueError.value = null;
      return;
    }

    if (!owner || !repo || issueNumber === null) {
      fetchedIssue.value = null;
      isLoadingIssue.value = false;
      issueError.value = null;
      return;
    }

    const abortController = new AbortController();
    onCleanup(() => abortController.abort());

    isLoadingIssue.value = true;
    issueError.value = null;

    try {
      const response = await apiFetch(
        `/api/integrations/github/repos/${owner}/${repo}/issues/${issueNumber}`,
        { signal: abortController.signal },
      );

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Unable to load issue details."));
      }

      fetchedIssue.value = (await response.json()) as GitHubIssue;
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }

      issueError.value = error instanceof Error ? error.message : "Unable to load issue details.";
      fetchedIssue.value = null;
    } finally {
      if (!abortController.signal.aborted) {
        isLoadingIssue.value = false;
      }
    }
  },
  { immediate: true },
);

watch(
  () => [props.owner, props.repo, props.issueNumber, props.comments, commentsReloadKey.value] as const,
  async ([owner, repo, issueNumber, comments], _previousValue, onCleanup) => {
    if (comments) {
      fetchedComments.value = [];
      isLoadingComments.value = false;
      commentsError.value = null;
      return;
    }

    if (!owner || !repo || issueNumber === null) {
      fetchedComments.value = [];
      isLoadingComments.value = false;
      commentsError.value = null;
      return;
    }

    const abortController = new AbortController();
    onCleanup(() => abortController.abort());

    isLoadingComments.value = true;
    commentsError.value = null;

    try {
      const response = await apiFetch(
        `/api/integrations/github/repos/${owner}/${repo}/issues/${issueNumber}/comments`,
        { signal: abortController.signal },
      );

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Unable to load issue comments."));
      }

      const payload = (await response.json()) as unknown[];
      fetchedComments.value = payload
        .map((comment) => normalizeComment(comment))
        .filter((comment): comment is GitHubIssueComment => comment !== null);
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }

      commentsError.value = error instanceof Error ? error.message : "Unable to load issue comments.";
      fetchedComments.value = [];
    } finally {
      if (!abortController.signal.aborted) {
        isLoadingComments.value = false;
      }
    }
  },
  { immediate: true },
);

const effectiveIssue = computed(() => props.issue ?? fetchedIssue.value);
const effectiveComments = computed(() => props.comments ?? fetchedComments.value);
const repositoryFullName = computed(() => {
  if (props.repositoryFullName) {
    return props.repositoryFullName;
  }

  return props.owner && props.repo ? `${props.owner}/${props.repo}` : "GitHub repository";
});

const issueStatusIcon = computed(() => (effectiveIssue.value?.state === "closed" ? CheckCircle2 : CircleDot));
const issueStatusClassName = computed(() => {
  return effectiveIssue.value?.state === "closed" ? "issue-page__status-icon issue-page__status-icon--closed" : "issue-page__status-icon issue-page__status-icon--open";
});
const issueStatusLabel = computed(() => (effectiveIssue.value?.state === "closed" ? "Closed" : "Open"));
const issueBody = computed(() => effectiveIssue.value?.body?.trim() || "No description provided.");
const renderedComments = computed(() => {
  return effectiveComments.value.map((comment) => ({
    ...comment,
    body: comment.body.trim() || "No comment body provided.",
    relativeTime: formatRelativeTime(comment.updatedAt || comment.createdAt),
    absoluteTime: formatAbsoluteDate(comment.updatedAt || comment.createdAt),
  }));
});
const commentSummary = computed(() => {
  const count = effectiveIssue.value?.comments ?? effectiveComments.value.length;
  return count === 1 ? "1 comment" : `${count} comments`;
});
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <div
      v-if="isLoadingIssue && !effectiveIssue"
      class="flex flex-1 items-center justify-center gap-3 rounded-xl border border-border bg-card p-8 text-sm text-muted-foreground"
    >
      <LoaderCircle
        :size="18"
        class="animate-spin"
      />
      <span>Loading issue details…</span>
    </div>

    <div
      v-else-if="issueError && !effectiveIssue"
      class="flex flex-1 flex-col items-start gap-4 rounded-xl border border-red-500/30 bg-red-500/10 p-6 text-sm text-red-200"
      role="alert"
    >
      <div
        class="flex items-start gap-3"
      >
        <AlertCircle
          :size="18"
          class="mt-0.5 shrink-0"
        />
        <div>
          <p class="font-medium">
            Unable to load issue details
          </p>
          <p class="mt-1">
            {{ issueError }}
          </p>
        </div>
      </div>

      <Button
        variant="outline"
        size="sm"
        @click="retryIssueLoad"
      >
        <RefreshCw :size="14" />
        Retry
      </Button>
    </div>

    <div
      v-else-if="!effectiveIssue"
      class="flex flex-1 items-center justify-center rounded-xl border border-dashed border-border bg-card/40 p-8 text-center text-sm text-muted-foreground"
    >
      Select an issue to view its details.
    </div>

    <template v-else>
      <header
        class="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between"
      >
        <div class="space-y-4">
          <div
            class="flex flex-wrap items-center gap-2 text-sm text-muted-foreground"
          >
            <component
              :is="issueStatusIcon"
              :class="issueStatusClassName"
              :size="16"
              aria-hidden="true"
            />
            <span class="font-medium text-foreground">{{ issueStatusLabel }}</span>
            <span>•</span>
            <span>{{ repositoryFullName }}</span>
            <span>•</span>
            <span>#{{ effectiveIssue.number }}</span>
          </div>

          <div class="space-y-3">
            <h1 class="text-2xl font-semibold tracking-tight text-foreground">
              {{ effectiveIssue.title }}
            </h1>

            <div class="flex flex-wrap gap-2">
              <span
                v-for="label in effectiveIssue.labels"
                :key="label.name"
                class="inline-flex items-center rounded-full border px-2.5 py-1 text-xs font-semibold"
                :style="buildLabelStyle(label.color)"
              >
                {{ label.name }}
              </span>
            </div>
          </div>

          <div
            class="flex flex-wrap items-center gap-3 text-sm text-muted-foreground"
          >
            <Avatar class="size-8">
              <AvatarImage
                :src="effectiveIssue.user.avatar_url"
                :alt="`${effectiveIssue.user.login} avatar`"
              />
              <AvatarFallback>{{ getAvatarFallback(effectiveIssue.user.login) }}</AvatarFallback>
            </Avatar>

            <span class="font-medium text-foreground">{{ effectiveIssue.user.login }}</span>
            <span>Updated {{ formatRelativeTime(effectiveIssue.updated_at) }}</span>
            <span>Created {{ formatAbsoluteDate(effectiveIssue.created_at) }}</span>
          </div>
        </div>

        <Button
          variant="outline"
          size="sm"
          @click="openIssueOnGitHub"
        >
          <ExternalLink :size="14" />
          Open on GitHub
        </Button>
      </header>

      <Card>
        <CardHeader>
          <CardTitle>Description</CardTitle>
        </CardHeader>

        <CardContent>
          <article class="github-body text-sm text-foreground">
            {{ issueBody }}
          </article>
        </CardContent>
      </Card>

      <Card>
        <CardHeader
          class="flex flex-row items-center justify-between gap-3 space-y-0"
        >
          <div class="flex items-center gap-2">
            <MessageSquare
              :size="18"
              class="text-muted-foreground"
            />
            <CardTitle>Comments</CardTitle>
          </div>

          <span class="text-sm text-muted-foreground">{{ commentSummary }}</span>
        </CardHeader>

        <CardContent class="space-y-4">
          <div
            v-if="commentsError"
            class="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-500/30 bg-red-500/10 p-3 text-sm text-red-200"
            role="alert"
          >
            <span>{{ commentsError }}</span>
            <Button
              variant="outline"
              size="sm"
              @click="retryCommentsLoad"
            >
              <RefreshCw :size="14" />
              Retry
            </Button>
          </div>

          <div
            v-if="isLoadingComments && renderedComments.length === 0"
            class="flex items-center gap-2 text-sm text-muted-foreground"
          >
            <LoaderCircle
              :size="16"
              class="animate-spin"
            />
            <span>Loading comments…</span>
          </div>

          <ol
            v-else-if="renderedComments.length > 0"
            class="space-y-4"
          >
            <li
              v-for="comment in renderedComments"
              :key="comment.id"
              class="rounded-xl border border-border bg-background/50 p-4"
            >
              <div
                class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between"
              >
                <div
                  class="flex items-center gap-3"
                >
                  <Avatar class="size-9">
                    <AvatarImage
                      :src="comment.user.avatarUrl"
                      :alt="`${comment.user.login} avatar`"
                    />
                    <AvatarFallback>{{ getAvatarFallback(comment.user.login) }}</AvatarFallback>
                  </Avatar>

                  <div>
                    <p class="text-sm font-medium text-foreground">
                      {{ comment.user.login }}
                    </p>
                    <p
                      class="text-xs text-muted-foreground"
                      :title="comment.absoluteTime"
                    >
                      {{ comment.relativeTime }}
                    </p>
                  </div>
                </div>

                <a
                  v-if="comment.htmlUrl"
                  :href="comment.htmlUrl"
                  target="_blank"
                  rel="noreferrer noopener"
                  class="inline-flex items-center gap-1 text-xs font-medium text-primary hover:underline"
                >
                  <ExternalLink :size="12" />
                  View on GitHub
                </a>
              </div>

              <article class="github-body mt-4 text-sm text-foreground">
                {{ comment.body }}
              </article>
            </li>
          </ol>

          <p
            v-else
            class="text-sm text-muted-foreground"
          >
            No comments yet.
          </p>
        </CardContent>
      </Card>
    </template>
  </section>
</template>

<style scoped>
.issue-page__status-icon {
  flex-shrink: 0;
}

.issue-page__status-icon--open {
  color: #22c55e;
}

.issue-page__status-icon--closed {
  color: var(--muted-foreground);
}

.github-body {
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
