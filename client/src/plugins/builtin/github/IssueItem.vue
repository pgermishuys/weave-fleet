<script setup lang="ts">
import { useRouter } from "@tanstack/vue-router";
import { computed } from "vue";
import { CheckCircle2, CircleDot } from "lucide-vue-next";
import { formatRelativeTime } from "@/lib/format-utils";

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
  updatedAt: string;
  htmlUrl: string;
}

const props = defineProps<{
  item: GitHubIssueItemData;
}>();

const router = useRouter();

const statusIcon = computed(() => (props.item.state === "open" ? CircleDot : CheckCircle2));
const statusClassName = computed(() =>
  props.item.state === "open" ? "issue-status-icon issue-status-icon--open" : "issue-status-icon issue-status-icon--closed",
);
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
    class="issue-item"
    role="button"
    tabindex="0"
    @click="openIssue"
    @keydown="handleKeydown"
  >
    <component :is="statusIcon" :class="statusClassName" :size="15" aria-hidden="true" />

    <div class="issue-body">
      <div class="issue-row issue-row--title">
        <p class="issue-title">
          {{ item.title }}
        </p>
        <span class="issue-number">#{{ item.number }}</span>
      </div>

      <div class="issue-row issue-row--meta">
        <span class="issue-repo">{{ item.repoFullName }}</span>
        <div class="issue-labels" aria-label="Issue labels">
          <span
            v-for="label in item.labels"
            :key="label.name"
            class="issue-label"
            :style="getLabelStyle(label.color)"
          >
            {{ label.name }}
          </span>
        </div>
      </div>

      <div class="issue-row issue-row--footer">
        <img class="issue-avatar" :src="item.user.avatarUrl" :alt="`${item.user.login} avatar`">
        <span class="issue-user">{{ item.user.login }}</span>
        <span class="issue-time">{{ relativeTime }}</span>
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
.issue-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  cursor: pointer;
  position: relative;
  outline: none;
}

.issue-item:hover .link-action,
.issue-item:focus-within .link-action {
  opacity: 1;
}

.issue-item:focus-visible {
  background: rgba(255, 255, 255, 0.04);
}

.issue-status-icon {
  flex-shrink: 0;
  margin-top: 2px;
}

.issue-status-icon--open {
  color: #22c55e;
}

.issue-status-icon--closed {
  color: var(--muted);
}

.issue-body {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 6px;
  padding-right: 52px;
}

.issue-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
}

.issue-row--title {
  align-items: flex-start;
}

.issue-title {
  margin: 0;
  min-width: 0;
  flex: 1;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.issue-number {
  flex-shrink: 0;
  font-size: 11px;
  color: var(--muted);
}

.issue-repo,
.issue-user,
.issue-time {
  font-size: 11px;
  color: var(--muted);
}

.issue-labels {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  flex-wrap: wrap;
}

.issue-label {
  display: inline-flex;
  align-items: center;
  min-height: 18px;
  padding: 0 6px;
  border: 1px solid transparent;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
}

.issue-avatar {
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
