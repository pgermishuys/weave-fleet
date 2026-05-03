<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { AlertCircle, ExternalLink, GitMerge, GitPullRequest, GitPullRequestClosed, LoaderCircle, MessageSquare, RefreshCw } from "lucide-vue-next";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { apiFetch } from "@/lib/api-client";
import { formatRelativeTime } from "@/lib/format-utils";
import type { GitHubPullRequest } from "@/plugins/builtin/github/composables/github-types";

interface GitHubPullRequestComment {
  id: number;
  key: string;
  body: string;
  htmlUrl: string | null;
  createdAt: string;
  updatedAt: string;
  source: "discussion" | "review";
  path: string | null;
  line: number | null;
  user: {
    login: string;
    avatarUrl: string;
  };
}

interface Props {
  owner?: string | null;
  repo?: string | null;
  pullRequestNumber?: number | null;
  repositoryFullName?: string | null;
  pullRequest?: GitHubPullRequest | null;
  comments?: readonly GitHubPullRequestComment[] | null;
}

const props = withDefaults(defineProps<Props>(), {
  owner: null,
  repo: null,
  pullRequestNumber: null,
  repositoryFullName: null,
  pullRequest: null,
  comments: null,
});

const fetchedPullRequest = shallowRef<GitHubPullRequest | null>(null);
const fetchedComments = shallowRef<GitHubPullRequestComment[]>([]);
const isLoadingPullRequest = shallowRef(false);
const isLoadingComments = shallowRef(false);
const pullRequestError = shallowRef<string | null>(null);
const commentsError = shallowRef<string | null>(null);
const pullRequestReloadKey = shallowRef(0);
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

function normalizeComment(payload: unknown, source: "discussion" | "review"): GitHubPullRequestComment | null {
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
    key: `${source}-${id}`,
    body: getString(value, "body") ?? "",
    htmlUrl: getString(value, "html_url"),
    createdAt: getString(value, "created_at") ?? "",
    updatedAt: getString(value, "updated_at") ?? "",
    source,
    path: getString(value, "path"),
    line: getNumber(value, "line"),
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

function retryPullRequestLoad(): void {
  pullRequestReloadKey.value += 1;
}

function retryCommentsLoad(): void {
  commentsReloadKey.value += 1;
}

function openPullRequestOnGitHub(): void {
  if (!effectivePullRequest.value?.html_url) {
    return;
  }

  window.open(effectivePullRequest.value.html_url, "_blank", "noopener,noreferrer");
}

async function fetchCommentCollection(path: string, signal: AbortSignal): Promise<unknown[]> {
  const response = await apiFetch(path, { signal });
  if (!response.ok) {
    throw new Error(await readErrorMessage(response, "Unable to load pull request comments."));
  }

  return (await response.json()) as unknown[];
}

watch(
  () => [props.owner, props.repo, props.pullRequestNumber, props.pullRequest, pullRequestReloadKey.value] as const,
  async ([owner, repo, pullRequestNumber, pullRequest], _previousValue, onCleanup) => {
    if (pullRequest) {
      fetchedPullRequest.value = null;
      isLoadingPullRequest.value = false;
      pullRequestError.value = null;
      return;
    }

    if (!owner || !repo || pullRequestNumber === null) {
      fetchedPullRequest.value = null;
      isLoadingPullRequest.value = false;
      pullRequestError.value = null;
      return;
    }

    const abortController = new AbortController();
    onCleanup(() => abortController.abort());

    isLoadingPullRequest.value = true;
    pullRequestError.value = null;

    try {
      const response = await apiFetch(
        `/api/integrations/github/repos/${owner}/${repo}/pulls/${pullRequestNumber}`,
        { signal: abortController.signal },
      );

      if (!response.ok) {
        throw new Error(await readErrorMessage(response, "Unable to load pull request details."));
      }

      fetchedPullRequest.value = (await response.json()) as GitHubPullRequest;
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }

      pullRequestError.value = error instanceof Error ? error.message : "Unable to load pull request details.";
      fetchedPullRequest.value = null;
    } finally {
      if (!abortController.signal.aborted) {
        isLoadingPullRequest.value = false;
      }
    }
  },
  { immediate: true },
);

watch(
  () => [props.owner, props.repo, props.pullRequestNumber, props.comments, commentsReloadKey.value] as const,
  async ([owner, repo, pullRequestNumber, comments], _previousValue, onCleanup) => {
    if (comments) {
      fetchedComments.value = [];
      isLoadingComments.value = false;
      commentsError.value = null;
      return;
    }

    if (!owner || !repo || pullRequestNumber === null) {
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
      const [discussionResult, reviewResult] = await Promise.allSettled([
        fetchCommentCollection(
          `/api/integrations/github/repos/${owner}/${repo}/issues/${pullRequestNumber}/comments`,
          abortController.signal,
        ),
        fetchCommentCollection(
          `/api/integrations/github/repos/${owner}/${repo}/pulls/${pullRequestNumber}/comments`,
          abortController.signal,
        ),
      ]);

      const nextComments: GitHubPullRequestComment[] = [];
      const errors: string[] = [];

      if (discussionResult.status === "fulfilled") {
        nextComments.push(
          ...discussionResult.value
            .map((comment) => normalizeComment(comment, "discussion"))
            .filter((comment): comment is GitHubPullRequestComment => comment !== null),
        );
      } else {
        errors.push(discussionResult.reason instanceof Error ? discussionResult.reason.message : "Unable to load discussion comments.");
      }

      if (reviewResult.status === "fulfilled") {
        nextComments.push(
          ...reviewResult.value
            .map((comment) => normalizeComment(comment, "review"))
            .filter((comment): comment is GitHubPullRequestComment => comment !== null),
        );
      } else {
        errors.push(reviewResult.reason instanceof Error ? reviewResult.reason.message : "Unable to load review comments.");
      }

      fetchedComments.value = nextComments.sort((left, right) => {
        const leftDate = Date.parse(left.createdAt || left.updatedAt);
        const rightDate = Date.parse(right.createdAt || right.updatedAt);

        if (Number.isNaN(leftDate) && Number.isNaN(rightDate)) {
          return left.id - right.id;
        }

        if (Number.isNaN(leftDate)) {
          return 1;
        }

        if (Number.isNaN(rightDate)) {
          return -1;
        }

        return leftDate - rightDate;
      });

      commentsError.value = errors.length > 0 && fetchedComments.value.length > 0 ? errors.join(" ") : errors[0] ?? null;
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }

      commentsError.value = error instanceof Error ? error.message : "Unable to load pull request comments.";
      fetchedComments.value = [];
    } finally {
      if (!abortController.signal.aborted) {
        isLoadingComments.value = false;
      }
    }
  },
  { immediate: true },
);

const effectivePullRequest = computed(() => props.pullRequest ?? fetchedPullRequest.value);
const effectiveComments = computed(() => props.comments ?? fetchedComments.value);
const repositoryFullName = computed(() => {
  if (props.repositoryFullName) {
    return props.repositoryFullName;
  }

  return props.owner && props.repo ? `${props.owner}/${props.repo}` : "GitHub repository";
});

const pullRequestStatusIcon = computed(() => {
  switch (effectivePullRequest.value?.state) {
    case "merged":
      return GitMerge;
    case "closed":
      return GitPullRequestClosed;
    case "open":
    default:
      return GitPullRequest;
  }
});

const pullRequestStatusClassName = computed(() => {
  switch (effectivePullRequest.value?.state) {
    case "merged":
      return "pull-request-page__status-icon pull-request-page__status-icon--merged";
    case "closed":
      return "pull-request-page__status-icon pull-request-page__status-icon--closed";
    case "open":
    default:
      return "pull-request-page__status-icon pull-request-page__status-icon--open";
  }
});

const pullRequestStatusLabel = computed(() => {
  switch (effectivePullRequest.value?.state) {
    case "merged":
      return "Merged";
    case "closed":
      return "Closed";
    case "open":
    default:
      return effectivePullRequest.value?.draft ? "Draft" : "Open";
  }
});

const pullRequestBody = computed(() => effectivePullRequest.value?.body?.trim() || "No description provided.");
const githubActionButtonClass = "self-start rounded-[var(--radius-btn)] border-border bg-white/[0.04] px-2.5 text-xs font-medium text-foreground shadow-none hover:bg-white/[0.08] hover:text-foreground";
const renderedComments = computed(() => {
  return effectiveComments.value.map((comment) => ({
    ...comment,
    body: comment.body.trim() || "No comment body provided.",
    relativeTime: formatRelativeTime(comment.updatedAt || comment.createdAt),
    absoluteTime: formatAbsoluteDate(comment.updatedAt || comment.createdAt),
    sourceLabel: comment.source === "review" ? "Review" : "Discussion",
  }));
});
const commentSummary = computed(() => {
  const count = effectiveComments.value.length;
  return count === 1 ? "1 comment" : `${count} comments`;
});
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <div
      v-if="isLoadingPullRequest && !effectivePullRequest"
      class="flex flex-1 items-center justify-center gap-3 rounded-xl border border-border bg-card p-8 text-sm text-muted-foreground"
    >
      <LoaderCircle
        :size="18"
        class="animate-spin"
      />
      <span>Loading pull request details…</span>
    </div>

    <div
      v-else-if="pullRequestError && !effectivePullRequest"
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
            Unable to load pull request details
          </p>
          <p class="mt-1">
            {{ pullRequestError }}
          </p>
        </div>
      </div>

      <Button
        variant="outline"
        size="sm"
        :class="githubActionButtonClass"
        @click="retryPullRequestLoad"
      >
        <RefreshCw :size="14" />
        Retry
      </Button>
    </div>

    <div
      v-else-if="!effectivePullRequest"
      class="flex flex-1 items-center justify-center rounded-xl border border-dashed border-border bg-card/40 p-8 text-center text-sm text-muted-foreground"
    >
      Select a pull request to view its details.
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
              :is="pullRequestStatusIcon"
              :class="pullRequestStatusClassName"
              :size="16"
              aria-hidden="true"
            />
            <span class="font-medium text-foreground">{{ pullRequestStatusLabel }}</span>
            <span>•</span>
            <span>{{ repositoryFullName }}</span>
            <span>•</span>
            <span>#{{ effectivePullRequest.number }}</span>
            <span
              v-if="effectivePullRequest.draft && effectivePullRequest.state === 'open'"
              class="rounded-full bg-amber-500/15 px-2 py-0.5 text-xs font-semibold text-amber-300"
            >
              Draft
            </span>
          </div>

          <div class="space-y-3">
            <h1 class="text-2xl font-semibold tracking-tight text-foreground">
              {{ effectivePullRequest.title }}
            </h1>

            <div class="flex flex-wrap gap-2">
              <span
                v-for="label in effectivePullRequest.labels"
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
                :src="effectivePullRequest.user.avatar_url"
                :alt="`${effectivePullRequest.user.login} avatar`"
              />
              <AvatarFallback>{{ getAvatarFallback(effectivePullRequest.user.login) }}</AvatarFallback>
            </Avatar>

            <span class="font-medium text-foreground">{{ effectivePullRequest.user.login }}</span>
            <span>{{ effectivePullRequest.head.ref }} → {{ effectivePullRequest.base.ref }}</span>
            <span>{{ effectivePullRequest.additions }} additions</span>
            <span>{{ effectivePullRequest.deletions }} deletions</span>
            <span>{{ effectivePullRequest.changed_files }} files changed</span>
            <span>Updated {{ formatRelativeTime(effectivePullRequest.updated_at) }}</span>
          </div>
        </div>

        <Button
          variant="outline"
          size="sm"
          :class="githubActionButtonClass"
          @click="openPullRequestOnGitHub"
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
            {{ pullRequestBody }}
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
              :class="githubActionButtonClass"
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
              :key="comment.key"
              class="rounded-xl border border-border bg-background/50 p-4"
            >
              <div
                class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between"
              >
                <div class="flex items-center gap-3">
                  <Avatar class="size-9">
                    <AvatarImage
                      :src="comment.user.avatarUrl"
                      :alt="`${comment.user.login} avatar`"
                    />
                    <AvatarFallback>{{ getAvatarFallback(comment.user.login) }}</AvatarFallback>
                  </Avatar>

                  <div>
                    <div
                      class="flex flex-wrap items-center gap-2"
                    >
                      <p class="text-sm font-medium text-foreground">
                        {{ comment.user.login }}
                      </p>
                      <span
                        class="rounded-full border border-border bg-muted/40 px-2 py-0.5 text-[10px] font-medium text-muted-foreground"
                      >
                        {{ comment.sourceLabel }}
                      </span>
                      <span
                        v-if="comment.path"
                        class="text-xs text-muted-foreground"
                      >
                        {{ comment.path }}<template v-if="comment.line">:{{ comment.line }}</template>
                      </span>
                    </div>

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
.pull-request-page__status-icon {
  flex-shrink: 0;
}

.pull-request-page__status-icon--open {
  color: #22c55e;
}

.pull-request-page__status-icon--closed {
  color: var(--muted-foreground);
}

.pull-request-page__status-icon--merged {
  color: #a855f7;
}

.github-body {
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
