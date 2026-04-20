<script setup lang="ts">
import { useRouter } from "@tanstack/vue-router";
import { computed } from "vue";
import { GitMerge, GitPullRequest, GitPullRequestClosed } from "lucide-vue-next";
import { formatRelativeTime } from "@/lib/format-utils";

interface GitHubLabel {
  name: string;
  color: string;
}

interface GitHubUser {
  login: string;
  avatarUrl: string;
}

interface GitHubPullRequestItemData {
  id: number;
  number: number;
  title: string;
  state: "open" | "closed" | "merged";
  draft: boolean;
  repoFullName: string;
  labels: readonly GitHubLabel[];
  user: GitHubUser;
  updatedAt: string;
  htmlUrl: string;
}

const props = defineProps<{
  item: GitHubPullRequestItemData;
}>();

const router = useRouter();

const statusIcon = computed(() => {
  switch (props.item.state) {
    case "merged":
      return GitMerge;
    case "closed":
      return GitPullRequestClosed;
    case "open":
    default:
      return GitPullRequest;
  }
});

const statusClassName = computed(() => {
  switch (props.item.state) {
    case "merged":
      return "pull-request-status-icon pull-request-status-icon--merged";
    case "closed":
      return "pull-request-status-icon pull-request-status-icon--closed";
    case "open":
    default:
      return "pull-request-status-icon pull-request-status-icon--open";
  }
});

const relativeTime = computed(() => formatRelativeTime(props.item.updatedAt));

function getLabelStyle(color: string): { backgroundColor: string; borderColor: string; color: string } {
  return {
    backgroundColor: `#${color}22`,
    borderColor: `#${color}55`,
    color: `#${color}`,
  };
}

function getRepoRouteParams(): { owner: string; repo: string } | null {
  const [owner, repo] = props.item.repoFullName.split("/");

  if (!owner || !repo) {
    return null;
  }

  return { owner, repo };
}

function openPullRequest(): void {
  const params = getRepoRouteParams();

  if (!params) {
    window.open(props.item.htmlUrl, "_blank", "noopener,noreferrer");
    return;
  }

  void router.navigate({
    to: "/github/$owner/$repo/pulls/$number",
    params: {
      ...params,
      number: String(props.item.number),
    },
  });
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    openPullRequest();
  }
}
</script>

<template>
  <article
    class="pull-request-item"
    role="button"
    tabindex="0"
    @click="openPullRequest"
    @keydown="handleKeydown"
  >
    <component
      :is="statusIcon"
      :class="statusClassName"
      :size="15"
      aria-hidden="true"
    />

    <div class="pull-request-body">
      <div class="pull-request-row pull-request-row--title">
        <p class="pull-request-title">
          {{ item.title }}
        </p>
        <span
          v-if="item.draft"
          class="pull-request-draft"
        >Draft</span>
        <span class="pull-request-number">#{{ item.number }}</span>
      </div>

      <div class="pull-request-row pull-request-row--meta">
        <span class="pull-request-repo">{{ item.repoFullName }}</span>
        <div
          class="pull-request-labels"
          aria-label="Pull request labels"
        >
          <span
            v-for="label in item.labels"
            :key="label.name"
            class="pull-request-label"
            :style="getLabelStyle(label.color)"
          >
            {{ label.name }}
          </span>
        </div>
      </div>

      <div class="pull-request-row pull-request-row--footer">
        <img
          class="pull-request-avatar"
          :src="item.user.avatarUrl"
          :alt="`${item.user.login} avatar`"
        >
        <span class="pull-request-user">{{ item.user.login }}</span>
        <span class="pull-request-time">{{ relativeTime }}</span>
      </div>
    </div>

    <a
      class="link-action"
      :href="item.htmlUrl"
      target="_blank"
      rel="noreferrer noopener"
      @click.stop
    >
      Link →
    </a>
  </article>
</template>

<style scoped>
.pull-request-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  cursor: pointer;
  position: relative;
  outline: none;
}

.pull-request-item:hover .link-action,
.pull-request-item:focus-within .link-action {
  opacity: 1;
}

.pull-request-item:focus-visible {
  background: rgba(255, 255, 255, 0.04);
}

.pull-request-status-icon {
  flex-shrink: 0;
  margin-top: 2px;
}

.pull-request-status-icon--open {
  color: #22c55e;
}

.pull-request-status-icon--closed {
  color: var(--muted);
}

.pull-request-status-icon--merged {
  color: #a855f7;
}

.pull-request-body {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 6px;
  padding-right: 52px;
}

.pull-request-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
}

.pull-request-row--title {
  align-items: flex-start;
}

.pull-request-title {
  margin: 0;
  min-width: 0;
  flex: 1;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.pull-request-number,
.pull-request-repo,
.pull-request-user,
.pull-request-time {
  font-size: 11px;
  color: var(--muted);
}

.pull-request-draft {
  display: inline-flex;
  align-items: center;
  min-height: 18px;
  padding: 0 6px;
  border-radius: 999px;
  background: rgba(245, 158, 11, 0.16);
  color: #f59e0b;
  font-size: 10px;
  font-weight: 700;
}

.pull-request-labels {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  flex-wrap: wrap;
}

.pull-request-label {
  display: inline-flex;
  align-items: center;
  min-height: 18px;
  padding: 0 6px;
  border: 1px solid transparent;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
}

.pull-request-avatar {
  width: 16px;
  height: 16px;
  border-radius: 999px;
  object-fit: cover;
}

.link-action {
  position: absolute;
  top: 50%;
  right: 12px;
  transform: translateY(-50%);
  font-size: 11px;
  color: var(--accent);
  text-decoration: none;
  opacity: 0;
}
</style>
