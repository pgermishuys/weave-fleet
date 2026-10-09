<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import {
  ChevronDown,
  ChevronRight,
  ExternalLink,
  GitBranch,
  LoaderCircle,
  MessageSquare,
  Plus,
  RefreshCw,
  Sparkles,
  TriangleAlert,
} from "lucide-vue-next";
import type { SessionListItem } from "@/api/client";
import CheckIcon from "@/components/github/CheckIcon.vue";
import ChecksSummary from "@/components/github/ChecksSummary.vue";
import DiffStat from "@/components/github/DiffStat.vue";
import GitHubItemIcon from "@/components/github/GitHubItemIcon.vue";
import GitHubLabel from "@/components/github/GitHubLabel.vue";
import PrStatePill from "@/components/github/PrStatePill.vue";
import ReviewerAvatar from "@/components/github/ReviewerAvatar.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import MarkdownRenderer from "@/components/visual-renderers/MarkdownRenderer.vue";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useGitHubSessions } from "@/composables/use-github-sessions";
import { useSendToAgent } from "@/composables/use-send-to-agent";
import { formatRelativeTime } from "@/lib/format-utils";
import {
  checkCounts,
  fetchGitHubItem,
  itemPrFacts,
  itemResourceId,
  itemSessionPreset,
  visibleBody,
  type GitHubItemDetail,
  type GitHubTimelineEntry,
} from "@/lib/github-items";
import { prState, prStateLabel, prWords, reviewerWords, type PrState } from "@/lib/pr-state";
import { isSessionLive, sessionRowStatus } from "@/lib/session-row-status";
import { checkState, ciStatus, formatFixPrompt, reviewThreads, type SmartLink } from "@/lib/smart-links";
import { useSmartLinksStore } from "@/stores/smart-links";

const props = defineProps<{
  owner: string;
  repo: string;
  number: string;
  kind: "issue" | "pull";
}>();

const router = useRouter();
const smartLinks = useSmartLinksStore();
const { useSessionsFor, openSession, startSession } = useGitHubSessions();

const detail = shallowRef<GitHubItemDetail | null>(null);
const errorMessage = shallowRef<string | null>(null);
const isLoading = shallowRef(true);
const isRefreshing = shallowRef(false);

const parsedNumber = computed(() => Number.parseInt(props.number, 10));
const repoFullName = computed(() => `${props.owner}/${props.repo}`);
const noun = computed(() => (props.kind === "pull" ? "pull request" : "issue"));

async function load(signal?: AbortSignal): Promise<void> {
  errorMessage.value = null;
  if (!props.owner || !props.repo || !(parsedNumber.value > 0)) {
    detail.value = null;
    errorMessage.value = `That isn't a GitHub ${noun.value} link.`;
    return;
  }
  try {
    detail.value = await fetchGitHubItem(props.owner, props.repo, parsedNumber.value, signal);
  } catch (error) {
    if (signal?.aborted) return;
    detail.value = null;
    errorMessage.value = error instanceof Error ? error.message : `Couldn't load the ${noun.value}.`;
  }
}

watch(
  () => [props.owner, props.repo, props.number] as const,
  async (_value, _previous, onCleanup) => {
    const controller = new AbortController();
    onCleanup(() => controller.abort());
    isLoading.value = true;
    detail.value = null;
    await load(controller.signal);
    if (!controller.signal.aborted) isLoading.value = false;
  },
  { immediate: true },
);

async function refresh(): Promise<void> {
  isRefreshing.value = true;
  await load();
  isRefreshing.value = false;
}

const summary = computed(() => detail.value?.summary ?? null);
const isPull = computed(() => summary.value?.kind === "pull");
const facts = computed(() => (summary.value ? itemPrFacts(summary.value, detail.value ?? undefined) : null));
const state = computed<PrState>(() => {
  if (!summary.value || !facts.value) return "open";
  if (!isPull.value) return summary.value.state === "closed" ? "closed" : "open";
  return prState(facts.value);
});
const stateLabel = computed(() => {
  if (!isPull.value || !facts.value) return summary.value?.state === "closed" ? "Closed" : "Open";
  return state.value === "open" || state.value === "blocked" ? prWords(facts.value) : prStateLabel(state.value);
});
const counts = computed(() => checkCounts(detail.value?.checks ?? []));
const reviewers = computed(() => summary.value?.reviewers ?? []);
const linkedPullRequests = computed(() =>
  (detail.value?.timeline ?? []).filter((entry) => entry.kind === "referenced" && entry.reference?.kind === "pull"));

// ── Fleet ──────────────────────────────────────────────────────────────────────

const resourceId = computed(() => (summary.value ? itemResourceId(summary.value) : null));
const sessions = useSessionsFor(resourceId);
const firstSession = computed(() => sessions.value[0] ?? null);

/** The first session's link to this pull request, which carries the failing checks' logs and the threads. */
const sessionLink = computed<SmartLink | null>(() => {
  const session = firstSession.value;
  const id = resourceId.value?.toLowerCase();
  if (!session || !id) return null;
  const links = smartLinks.bySession[session.session.id] ?? smartLinks.headerBySession[session.session.id] ?? [];
  return links.find((link) => link.resourceId.toLowerCase() === id) ?? null;
});
const failingRuns = computed(() => (sessionLink.value ? (ciStatus(sessionLink.value)?.checkRuns ?? []).filter((run) => checkState(run) === "failing") : []));
const threads = computed(() => (sessionLink.value ? reviewThreads(sessionLink.value) : []));
const fixLabel = computed(() => {
  if (!isPull.value || summary.value?.state !== "open") return null;
  const checks = failingRuns.value.length > 0;
  const review = threads.value.length > 0;
  if (checks && review) return "Fix checks and address review";
  if (checks) return "Fix failing checks";
  if (review) return "Address review";
  return null;
});

const { send, sending, sent, error: sendError } = useSendToAgent(() => firstSession.value?.session.id ?? "");

async function sendFix(): Promise<void> {
  if (!sessionLink.value) return;
  await send("fix", formatFixPrompt(sessionLink.value, failingRuns.value, threads.value));
}

function start(): void {
  if (summary.value) void startSession(itemSessionPreset(summary.value, detail.value?.body ?? null));
}

type Primary = { label: string; icon: "fix" | "open" | "start"; run: () => void };
const primary = computed<Primary>(() => {
  const session = firstSession.value;
  if (session && fixLabel.value) return { label: sent.value.has("fix") ? "Sent to the session" : fixLabel.value, icon: "fix", run: () => void sendFix() };
  if (session) return { label: "Open session", icon: "open", run: () => openSession(session) };
  return { label: isPull.value && summary.value?.state === "open" ? "Start a session on this branch" : "Start a session", icon: "start", run: start };
});

function sessionName(item: SessionListItem): string {
  return item.session.title?.trim() || "Untitled session";
}

function sessionWords(item: SessionListItem): string {
  if (item.sessionStatus === "waiting_input") return "Needs you";
  const status = sessionRowStatus(item, Date.now());
  return status.tone === "working" ? status.description : "Idle";
}

// ── Navigation ─────────────────────────────────────────────────────────────────

function goToRepo(): void {
  void router.navigate({ to: "/github/$owner/$repo", params: { owner: props.owner, repo: props.repo } });
}

function goHome(): void {
  void router.navigate({ to: "/github" });
}

function referenceHref(entry: GitHubTimelineEntry): string | undefined {
  return entry.url ?? undefined;
}

function openReference(event: MouseEvent, entry: GitHubTimelineEntry): void {
  const reference = entry.reference;
  if (!reference || reference.repository.toLowerCase() !== repoFullName.value.toLowerCase()) return;
  event.preventDefault();
  const kind = reference.kind === "pull" ? "pulls" : "issues";
  void router.navigate({ to: `/github/${props.owner}/${props.repo}/${kind}/${reference.number}` as string });
}

// ── Timeline words ─────────────────────────────────────────────────────────────

function timelineWords(entry: GitHubTimelineEntry): string {
  switch (entry.kind) {
    case "comment": return "commented";
    case "review":
      return entry.state === "APPROVED" ? "approved"
        : entry.state === "CHANGES_REQUESTED" ? "requested changes"
          : entry.state === "DISMISSED" ? "had a review dismissed" : "reviewed";
    case "merged": return "merged this";
    case "closed": return "closed this";
    case "reopened": return "reopened this";
    case "ready": return "marked this ready for review";
    case "draft": return "marked this as a draft";
    case "referenced": return "mentioned this in";
  }
}

function timelineTone(entry: GitHubTimelineEntry): string {
  if (entry.kind === "merged") return "merged";
  if (entry.kind === "closed") return "closed";
  if (entry.kind === "review") return entry.state === "APPROVED" ? "approved" : entry.state === "CHANGES_REQUESTED" ? "changes" : "plain";
  return "plain";
}

const ago = (value: string | null | undefined) => (value ? formatRelativeTime(value) : "");
const fullDate = (value: string | null | undefined) => (value ? new Date(value).toLocaleString() : "");
</script>

<template>
  <section
    class="gh-item"
    :aria-busy="isLoading || isRefreshing"
  >
    <div
      v-if="isLoading"
      class="gh-item__state"
    >
      <LoaderCircle
        :size="16"
        class="gh-item__spin"
        aria-hidden="true"
      />
      Loading the {{ noun }}…
    </div>

    <div
      v-else-if="errorMessage || !summary"
      class="gh-item__state gh-item__state--error"
      role="alert"
    >
      <TriangleAlert
        :size="16"
        aria-hidden="true"
      />
      <span>{{ errorMessage ?? `Couldn't load the ${noun}.` }}</span>
      <button
        type="button"
        class="gh-btn"
        @click="refresh"
      >
        <RefreshCw
          :size="13"
          aria-hidden="true"
        />
        Try again
      </button>
    </div>

    <template v-else>
      <header class="gh-item__head">
        <nav
          class="gh-item__crumbs"
          aria-label="Breadcrumbs"
        >
          <a
            href="/github"
            @click.prevent="goHome"
          >GitHub</a>
          <ChevronRight
            :size="12"
            aria-hidden="true"
          />
          <a
            :href="`/github/${owner}/${repo}`"
            @click.prevent="goToRepo()"
          >{{ owner }} / {{ repo }}</a>
          <ChevronRight
            :size="12"
            aria-hidden="true"
          />
          <span>{{ isPull ? "Pull requests" : "Issues" }}</span>
        </nav>

        <div class="gh-item__title-row">
          <h1 class="gh-item__title">
            {{ summary.title }} <span class="gh-item__number">#{{ summary.number }}</span>
          </h1>
          <div class="gh-item__actions">
            <button
              type="button"
              class="gh-btn gh-btn--ghost"
              :disabled="isRefreshing"
              aria-label="Refresh"
              title="Refresh"
              @click="refresh"
            >
              <RefreshCw
                :size="14"
                :class="{ 'gh-item__spin': isRefreshing }"
                aria-hidden="true"
              />
            </button>
            <a
              class="gh-btn"
              :href="summary.url"
              target="_blank"
              rel="noreferrer noopener"
            >
              <ExternalLink
                :size="13"
                aria-hidden="true"
              />
              GitHub
            </a>
            <span class="gh-split">
              <button
                type="button"
                class="gh-btn gh-btn--primary"
                :disabled="sending.has('fix') || sent.has('fix')"
                data-testid="github-item-primary"
                @click="primary.run"
              >
                <LoaderCircle
                  v-if="sending.has('fix')"
                  :size="13"
                  class="gh-item__spin"
                  aria-hidden="true"
                />
                <Sparkles
                  v-else-if="primary.icon === 'fix'"
                  :size="13"
                  aria-hidden="true"
                />
                <MessageSquare
                  v-else-if="primary.icon === 'open'"
                  :size="13"
                  aria-hidden="true"
                />
                <Plus
                  v-else
                  :size="13"
                  aria-hidden="true"
                />
                {{ primary.label }}
              </button>
              <DropdownMenu>
                <DropdownMenuTrigger as-child>
                  <button
                    type="button"
                    class="gh-btn gh-btn--primary gh-split__more"
                    aria-label="More ways to work on this"
                  >
                    <ChevronDown
                      :size="13"
                      aria-hidden="true"
                    />
                  </button>
                </DropdownMenuTrigger>
                <DropdownMenuContent
                  align="end"
                  class="w-64"
                >
                  <template v-if="sessions.length">
                    <DropdownMenuLabel>In Fleet</DropdownMenuLabel>
                    <DropdownMenuItem
                      v-for="item in sessions"
                      :key="item.session.id"
                      @select="openSession(item)"
                    >
                      <MessageSquare class="size-3.5" />
                      <span class="truncate">{{ sessionName(item) }}</span>
                    </DropdownMenuItem>
                    <DropdownMenuSeparator />
                  </template>
                  <DropdownMenuItem @select="start">
                    <Plus class="size-3.5" />
                    {{ sessions.length ? "Start another session" : primary.label }}
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </span>
          </div>
        </div>

        <div class="gh-item__by">
          <PrStatePill
            :kind="isPull ? 'pull' : 'issue'"
            :state="state"
            :label="stateLabel"
          />
          <ReviewerAvatar
            v-if="summary.author"
            :login="summary.author"
            :avatar-url="summary.authorAvatarUrl"
          />
          <span v-if="isPull && summary.headRef">
            <strong>{{ summary.author }}</strong> {{ summary.state === "merged" ? "merged" : "wants to merge" }}
            <span class="gh-branch">{{ summary.headRef }}</span> → <span class="gh-branch">{{ summary.baseRef }}</span>
          </span>
          <span v-else><strong>{{ summary.author }}</strong> opened this</span>
          <span :title="fullDate(summary.createdAt)">· {{ ago(summary.createdAt) }}</span>
          <template v-if="isPull && summary.additions !== null && summary.deletions !== null">
            <span aria-hidden="true">·</span>
            <DiffStat
              :additions="summary.additions"
              :deletions="summary.deletions"
            />
            <span v-if="detail?.changedFiles">in {{ detail.changedFiles }} file{{ detail.changedFiles === 1 ? "" : "s" }}</span>
          </template>
        </div>
        <p
          v-if="sendError"
          class="gh-item__error"
          role="alert"
        >
          {{ sendError }}
        </p>
      </header>

      <div class="gh-item__body">
        <main class="gh-item__main">
          <MarkdownRenderer
            v-if="visibleBody(detail?.body)"
            class="gh-item__description"
            :content="visibleBody(detail?.body)"
          />
          <p
            v-else
            class="gh-item__empty"
          >
            No description.
          </p>

          <ol
            v-if="detail?.timeline.length"
            class="gh-timeline"
            aria-label="Conversation"
          >
            <li
              v-for="(entry, index) in detail.timeline"
              :key="index"
              class="gh-timeline__entry"
              :data-tone="timelineTone(entry)"
            >
              <div class="gh-timeline__head">
                <ReviewerAvatar
                  v-if="entry.author"
                  :login="entry.author"
                  :avatar-url="entry.authorAvatarUrl"
                  :size="20"
                />
                <strong>{{ entry.author ?? "Someone" }}</strong>
                <span>{{ timelineWords(entry) }}</span>
                <a
                  v-if="entry.kind === 'referenced' && entry.reference"
                  class="gh-timeline__ref"
                  :href="referenceHref(entry)"
                  target="_blank"
                  rel="noreferrer noopener"
                  @click="openReference($event, entry)"
                >
                  <GitHubItemIcon
                    :kind="entry.reference.kind"
                    :state="entry.state === 'merged' ? 'merged' : entry.state === 'closed' ? 'closed' : 'open'"
                    :size="12"
                  />
                  #{{ entry.reference.number }} {{ entry.body }}
                </a>
                <span
                  class="gh-timeline__when"
                  :title="fullDate(entry.createdAt)"
                >· {{ ago(entry.createdAt) }}</span>
              </div>
              <MarkdownRenderer
                v-if="entry.kind !== 'referenced' && visibleBody(entry.body)"
                class="gh-timeline__body"
                :content="visibleBody(entry.body)"
              />
            </li>
          </ol>
        </main>

        <aside
          class="gh-item__rail"
          aria-label="Details"
        >
          <section class="gh-rail__section">
            <h2 class="gh-rail__heading">
              In Fleet
            </h2>
            <ul
              v-if="sessions.length"
              class="gh-rail__sessions"
            >
              <li
                v-for="item in sessions"
                :key="item.session.id"
              >
                <button
                  type="button"
                  class="gh-rail__session"
                  data-testid="github-item-session"
                  @click="openSession(item)"
                >
                  <StatusGlyph
                    v-if="isSessionLive(item)"
                    :status="item.sessionStatus"
                    :activity="item.activityStatus"
                  />
                  <MessageSquare
                    v-else
                    :size="12"
                    aria-hidden="true"
                  />
                  <span class="gh-rail__session-name">{{ sessionName(item) }}</span>
                  <span class="gh-rail__muted">{{ sessionWords(item) }}</span>
                </button>
              </li>
            </ul>
            <p
              v-else
              class="gh-rail__muted"
            >
              No session is working on this yet.
            </p>
          </section>

          <section
            v-if="isPull && detail?.checks.length"
            class="gh-rail__section"
          >
            <h2 class="gh-rail__heading">
              Checks
            </h2>
            <ChecksSummary :counts="counts" />
            <ul class="gh-rail__list">
              <li
                v-for="check in detail.checks"
                :key="check.name"
                class="gh-rail__check"
              >
                <CheckIcon
                  :state="check.state"
                  :size="13"
                />
                <span
                  class="gh-rail__check-name"
                  :title="check.workflowName ? `${check.workflowName} / ${check.name}` : check.name"
                >{{ check.name }}</span>
                <a
                  v-if="check.url"
                  class="gh-rail__icon-link"
                  :href="check.url"
                  target="_blank"
                  rel="noreferrer noopener"
                  :aria-label="`Open ${check.name} on GitHub`"
                >
                  <ExternalLink
                    :size="12"
                    aria-hidden="true"
                  />
                </a>
              </li>
            </ul>
          </section>

          <section
            v-if="isPull && (reviewers.length || (detail?.unresolvedThreads ?? 0) > 0)"
            class="gh-rail__section"
          >
            <h2 class="gh-rail__heading">
              Reviewers
            </h2>
            <ul class="gh-rail__list">
              <li
                v-for="reviewer in reviewers"
                :key="reviewer.login"
                class="gh-rail__reviewer"
              >
                <ReviewerAvatar
                  :login="reviewer.login"
                  :avatar-url="reviewer.avatarUrl"
                  :state="reviewer.state"
                />
                <span>{{ reviewer.login }}</span>
                <span
                  class="gh-rail__verdict"
                  :data-state="reviewer.state"
                >{{ reviewerWords(reviewer.state) }}</span>
              </li>
            </ul>
            <p
              v-if="(detail?.unresolvedThreads ?? 0) > 0"
              class="gh-rail__muted"
            >
              {{ detail?.unresolvedThreads }} unresolved thread{{ detail?.unresolvedThreads === 1 ? "" : "s" }}
            </p>
          </section>

          <section
            v-if="linkedPullRequests.length"
            class="gh-rail__section"
          >
            <h2 class="gh-rail__heading">
              Linked pull requests
            </h2>
            <ul class="gh-rail__list">
              <li
                v-for="(entry, index) in linkedPullRequests"
                :key="index"
              >
                <a
                  class="gh-rail__linked"
                  :href="referenceHref(entry)"
                  target="_blank"
                  rel="noreferrer noopener"
                  @click="openReference($event, entry)"
                >
                  <GitHubItemIcon
                    kind="pull"
                    :state="entry.state === 'merged' ? 'merged' : entry.state === 'closed' ? 'closed' : 'open'"
                    :size="13"
                  />
                  <span class="gh-rail__session-name">#{{ entry.reference?.number }} {{ entry.body }}</span>
                </a>
              </li>
            </ul>
          </section>

          <section
            v-if="summary.labels.length"
            class="gh-rail__section"
          >
            <h2 class="gh-rail__heading">
              Labels
            </h2>
            <div class="gh-rail__labels">
              <GitHubLabel
                v-for="label in summary.labels"
                :key="label.name"
                :name="label.name"
                :color="label.color"
              />
            </div>
          </section>

          <section
            v-if="summary.assignees.length"
            class="gh-rail__section"
          >
            <h2 class="gh-rail__heading">
              Assignees
            </h2>
            <ul class="gh-rail__list">
              <li
                v-for="login in summary.assignees"
                :key="login"
                class="gh-rail__reviewer"
              >
                <ReviewerAvatar :login="login" />
                <span>{{ login }}</span>
              </li>
            </ul>
          </section>

          <section
            v-if="isPull && summary.headRef"
            class="gh-rail__section"
          >
            <h2 class="gh-rail__heading">
              Branch
            </h2>
            <p class="gh-rail__branch">
              <GitBranch
                :size="12"
                aria-hidden="true"
              />
              <span class="gh-branch">{{ summary.headRef }}</span>
            </p>
          </section>
        </aside>
      </div>
    </template>
  </section>
</template>

<style scoped>
.gh-item {
  container-type: inline-size;
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: auto;
  color: var(--text);
}

.gh-item__state {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 24px 28px;
  color: var(--muted);
}

.gh-item__state--error {
  color: var(--text);
}

.gh-item__state--error > svg {
  color: var(--error);
}

.gh-item__spin {
  animation: gh-item-spin 1s linear infinite;
}

@keyframes gh-item-spin {
  to { transform: rotate(360deg); }
}

.gh-item__head {
  display: grid;
  gap: 10px;
  padding: 18px 28px 16px;
  border-bottom: 1px solid var(--border);
}

.gh-item__crumbs {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12px;
}

.gh-item__crumbs a {
  color: inherit;
  text-decoration: none;
}

.gh-item__crumbs a:hover {
  color: var(--text);
}

.gh-item__title-row {
  display: flex;
  align-items: flex-start;
  gap: 16px;
}

.gh-item__title {
  flex: 1;
  min-width: 0;
  margin: 0;
  font-size: 20px;
  font-weight: 600;
  letter-spacing: -0.01em;
  line-height: 1.3;
  text-wrap: balance;
}

.gh-item__number {
  color: var(--muted);
  font-weight: 400;
}

.gh-item__actions {
  display: flex;
  flex-shrink: 0;
  gap: 6px;
}

.gh-item__by {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12.5px;
}

.gh-item__by strong {
  color: var(--text);
  font-weight: 500;
}

.gh-item__error {
  margin: 0;
  color: var(--error);
  font-size: 12px;
}

.gh-branch {
  padding: 1px 6px;
  border-radius: 5px;
  background: var(--accent-dim);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  overflow-wrap: anywhere;
}

.gh-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 11px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 12.5px;
  font-weight: 500;
  text-decoration: none;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color var(--transition), background-color var(--transition);
}

.gh-btn:hover:not(:disabled) {
  border-color: color-mix(in srgb, var(--text) 18%, transparent);
}

.gh-btn:disabled {
  opacity: 0.7;
  cursor: default;
}

.gh-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.gh-btn--ghost {
  border-color: transparent;
  background: transparent;
  color: var(--muted);
}

.gh-btn--primary {
  border-color: transparent;
  background: var(--accent);
  color: var(--primary-foreground);
}

.gh-btn--primary:hover:not(:disabled) {
  border-color: transparent;
  background: color-mix(in srgb, var(--accent) 88%, white);
}

.gh-split {
  display: inline-flex;
}

.gh-split > .gh-btn:first-child {
  border-top-right-radius: 0;
  border-bottom-right-radius: 0;
}

.gh-split__more {
  padding: 0 7px;
  border-left: 1px solid color-mix(in srgb, var(--primary-foreground) 25%, transparent);
  border-top-left-radius: 0;
  border-bottom-left-radius: 0;
}

.gh-item__body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 300px;
  flex: 1;
  min-height: 0;
}

.gh-item__main {
  display: grid;
  align-content: start;
  gap: 18px;
  min-width: 0;
  padding: 20px 28px 32px;
}

.gh-item__description {
  max-width: 78ch;
  font-size: 13.5px;
}

.gh-item__empty {
  margin: 0;
  color: var(--muted);
  font-style: italic;
}

.gh-timeline {
  display: grid;
  margin: 0;
  padding: 0 0 0 18px;
  border-left: 1px solid var(--border);
  list-style: none;
}

.gh-timeline__entry {
  position: relative;
  display: grid;
  gap: 6px;
  padding: 9px 0;
}

.gh-timeline__entry::before {
  content: "";
  position: absolute;
  top: 14px;
  left: -24px;
  width: 11px;
  height: 11px;
  border: 2px solid var(--gh-dot, color-mix(in srgb, var(--text) 18%, transparent));
  border-radius: 50%;
  background: var(--panel-bg);
}

.gh-timeline__entry[data-tone="approved"] { --gh-dot: var(--check-pass); }
.gh-timeline__entry[data-tone="changes"] { --gh-dot: var(--pr-blocked); }
.gh-timeline__entry[data-tone="merged"] { --gh-dot: var(--pr-merged); }
.gh-timeline__entry[data-tone="closed"] { --gh-dot: var(--pr-closed); }

.gh-timeline__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12.5px;
}

.gh-timeline__head strong {
  color: var(--text);
  font-weight: 500;
}

.gh-timeline__ref {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  color: var(--text);
  text-decoration: none;
}

.gh-timeline__ref:hover {
  text-decoration: underline;
}

.gh-timeline__body {
  max-width: 78ch;
  padding: 9px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  font-size: 13px;
}

.gh-item__rail {
  display: grid;
  align-content: start;
  gap: 20px;
  min-width: 0;
  padding: 20px 20px 32px;
  border-left: 1px solid var(--border);
  background: color-mix(in srgb, var(--text) 2%, transparent);
}

.gh-rail__section {
  display: grid;
  gap: 8px;
  min-width: 0;
}

.gh-rail__heading {
  margin: 0;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.gh-rail__muted {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
}

.gh-rail__list,
.gh-rail__sessions {
  display: grid;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.gh-rail__session,
.gh-rail__linked {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-width: 0;
  padding: 6px 8px;
  border: 1px solid color-mix(in srgb, var(--accent) 30%, transparent);
  border-radius: var(--radius-btn);
  background: var(--accent-dim);
  color: var(--text);
  font-size: 12.5px;
  text-align: left;
  text-decoration: none;
  cursor: pointer;
}

.gh-rail__linked {
  border-color: transparent;
  background: transparent;
  padding: 4px 0;
}

.gh-rail__session-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.gh-rail__check,
.gh-rail__reviewer {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 24px;
  font-size: 12.5px;
}

.gh-rail__check-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.gh-rail__icon-link {
  display: inline-grid;
  place-items: center;
  color: var(--muted);
}

.gh-rail__icon-link:hover {
  color: var(--text);
}

.gh-rail__verdict {
  margin-left: auto;
  color: var(--muted);
  font-size: 11.5px;
}

.gh-rail__verdict[data-state="APPROVED"] { color: var(--check-pass); }
.gh-rail__verdict[data-state="CHANGES_REQUESTED"] { color: var(--pr-blocked); }

.gh-rail__labels {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.gh-rail__branch {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--muted);
}

@container (max-width: 820px) {
  .gh-item__body {
    grid-template-columns: minmax(0, 1fr);
  }

  .gh-item__rail {
    order: -1;
    border-left: 0;
    border-bottom: 1px solid var(--border);
  }

  .gh-item__title-row {
    flex-direction: column;
  }
}

@container (max-width: 520px) {
  .gh-item__head,
  .gh-item__main {
    padding-inline: 16px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .gh-item__spin { animation: none; }
}
</style>
