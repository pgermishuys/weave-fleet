<script setup lang="ts">
import { computed, ref } from "vue";
import {
  Check,
  ChevronRight,
  ExternalLink,
  GitMerge,
  LoaderCircle,
  MessageSquare,
  PinOff,
  Send,
  TriangleAlert,
  Users,
  X,
} from "lucide-vue-next";
import CheckIcon from "@/components/github/CheckIcon.vue";
import ChecksSummary from "@/components/github/ChecksSummary.vue";
import DiffStat from "@/components/github/DiffStat.vue";
import GitHubLabel from "@/components/github/GitHubLabel.vue";
import PrStatePill from "@/components/github/PrStatePill.vue";
import ReviewerAvatar from "@/components/github/ReviewerAvatar.vue";
import SmartLinkIcon from "@/components/session-context/SmartLinkIcon.vue";
import { useSendToAgent } from "@/composables/use-send-to-agent";
import { prState, prStateLabel, prWords, reviewerWords } from "@/lib/pr-state";
import {
  checkState,
  ciStatus,
  formatCheckFailurePrompt,
  formatReviewThreadPrompt,
  hasMergeConflict,
  isPullRequest,
  linkDiff,
  linkHref,
  linkLabels,
  linkNumber,
  linkPrFacts,
  linkReviewers,
  linkTitle,
  needsAttention,
  reviewThreads,
  summarizeChecks,
  type CheckRun,
  type ReviewThread,
  type SmartLink,
} from "@/lib/smart-links";
import { useSmartLinksStore } from "@/stores/smart-links";

const props = defineProps<{
  link: SmartLink;
}>();

const store = useSmartLinksStore();

const number = computed(() => linkNumber(props.link));
const title = computed(() => linkTitle(props.link));
const href = computed(() => linkHref(props.link));
const labels = computed(() => linkLabels(props.link));
const resolved = computed(() => props.link.enrichmentStatus === "resolved");
const isOpenPullRequest = computed(() => isPullRequest(props.link) && resolved.value && !props.link.isTerminal);
const checks = computed(() => summarizeChecks(props.link));
const runs = computed(() => ciStatus(props.link)?.checkRuns ?? []);
const threads = computed(() => reviewThreads(props.link));
const conflict = computed(() => hasMergeConflict(props.link));
const attention = computed(() => needsAttention(props.link));
const headRef = computed(() => (typeof props.link.metadata.headRef === "string" ? props.link.metadata.headRef : null));
const baseRef = computed(() => (typeof props.link.metadata.baseRef === "string" ? props.link.metadata.baseRef : null));
const facts = computed(() => linkPrFacts(props.link));
const diff = computed(() => linkDiff(props.link));
const reviewers = computed(() => linkReviewers(props.link));
const stateLabel = computed(() => {
  if (!isPullRequest(props.link)) return { state: props.link.status === "closed" ? "closed" as const : "open" as const, label: props.link.statusLabel };
  const state = prState(facts.value);
  // The words say what it's waiting on while it's open; the signals below list it all.
  return { state, label: state === "open" ? prWords(facts.value) : prStateLabel(state) };
});
// "Changes requested by sarah", "Approved by tvdb", "Waiting for kim".
const reviewSummary = computed(() => {
  const by = (state: string) => reviewers.value.filter((r) => r.state === state).map((r) => r.login).join(", ");
  if (by("CHANGES_REQUESTED")) return `${reviewerWords("CHANGES_REQUESTED")} by ${by("CHANGES_REQUESTED")}`;
  if (by("APPROVED")) return `Approved by ${by("APPROVED")}`;
  if (by("REQUESTED")) return `Waiting for ${by("REQUESTED")}`;
  return `${reviewers.value.length} reviewer${reviewers.value.length === 1 ? "" : "s"}`;
});

const checksOpen = ref(checks.value.state === "failing");
const reviewOpen = ref(false);

const pendingNote = computed(() => {
  switch (props.link.enrichmentStatus) {
    case "pending": return "Checking GitHub…";
    case "not_connected": return "Connect GitHub to see status";
    case "not_found": return "GitHub couldn't find this";
    case "error": return "Couldn't check GitHub";
    default: return null;
  }
});

function runState(run: CheckRun) {
  switch (checkState(run)) {
    case "failing": return "failure";
    case "running": return "pending";
    case "passed": return "success";
    default: return "neutral";
  }
}

// ── Send to agent ─────────────────────────────────────────────────────────────

const { send, sending, sent, error: sendError } = useSendToAgent(() => props.link.sessionId);

const sendCheck = (run: CheckRun) => send(`check:${run.name}`, formatCheckFailurePrompt(props.link, run));
const sendThread = (thread: ReviewThread) => send(`thread:${thread.threadNodeId}`, formatReviewThreadPrompt(props.link, thread));
</script>

<template>
  <article
    class="link-card"
    :class="{ 'link-card--attention': attention }"
    :data-link-id="link.id"
    :aria-label="`${isPullRequest(link) ? 'Pull request' : 'Issue'} #${number}: ${title}`"
  >
    <header class="link-card__head">
      <SmartLinkIcon
        :link="link"
        class="link-card__icon"
      />
      <div class="link-card__main">
        <a
          class="link-card__title"
          :href="href"
          target="_blank"
          rel="noopener noreferrer"
        >{{ title }}</a>
        <div class="link-card__meta">
          <PrStatePill
            v-if="resolved && link.statusLabel"
            :kind="isPullRequest(link) ? 'pull' : 'issue'"
            :state="stateLabel.state"
            :label="stateLabel.label"
          />
          <span
            v-else-if="pendingNote"
            class="link-card__note"
          >{{ pendingNote }}</span>
          <span
            v-if="headRef && baseRef && isOpenPullRequest"
            class="link-card__branch"
            :title="`${headRef} into ${baseRef}`"
          >{{ headRef }} → {{ baseRef }}</span>
          <DiffStat
            v-if="diff && isOpenPullRequest"
            :additions="diff.additions"
            :deletions="diff.deletions"
          />
          <GitHubLabel
            v-for="label in labels"
            :key="label.name"
            :name="label.name"
            :color="label.color"
          />
        </div>
      </div>
      <div class="link-card__trail">
        <a
          class="link-card__number"
          :href="href"
          target="_blank"
          rel="noopener noreferrer"
          :aria-label="`Open #${number} on GitHub`"
        >#{{ number }}<ExternalLink
          :size="11"
          aria-hidden="true"
        /></a>
        <button
          v-if="link.relationship === 'pinned'"
          type="button"
          class="link-card__icon-btn"
          :aria-label="`Unpin #${number}`"
          title="Unpin"
          @click="store.setPinned(link.sessionId, link.id, false)"
        >
          <PinOff
            :size="12"
            aria-hidden="true"
          />
        </button>
        <button
          v-if="link.relationship !== 'origin'"
          type="button"
          class="link-card__icon-btn"
          :aria-label="`Dismiss #${number}`"
          title="Dismiss"
          @click="store.dismiss(link.sessionId, link.id)"
        >
          <X
            :size="12"
            aria-hidden="true"
          />
        </button>
      </div>
    </header>

    <div
      v-if="isPullRequest(link) && resolved && link.status === 'merged'"
      class="link-card__signals"
    >
      <div class="signal signal--static">
        <GitMerge
          :size="14"
          class="signal__icon"
          data-tone="merged"
          aria-hidden="true"
        />
        <span class="signal__what">Merged</span>
        <span class="signal__detail">No longer checked</span>
      </div>
    </div>

    <div
      v-else-if="isOpenPullRequest"
      class="link-card__signals"
    >
      <template v-if="checks.state !== 'none'">
        <button
          type="button"
          class="signal"
          :class="{ 'signal--bad': checks.state === 'failing' }"
          :aria-expanded="checksOpen"
          @click="checksOpen = !checksOpen"
        >
          <CheckIcon
            :state="checks.state === 'failing' ? 'failure' : checks.state === 'running' ? 'pending' : 'success'"
            class="signal__icon"
          />
          <span class="signal__what">Checks</span>
          <span class="signal__detail">{{ checks.text }}</span>
          <ChevronRight
            :size="12"
            class="signal__chevron"
            aria-hidden="true"
          />
        </button>
        <ChecksSummary
          class="signal__bar"
          bar-only
          :counts="facts.checkCounts!"
        />
        <ul
          v-if="checksOpen"
          class="signal__list"
          aria-label="Checks"
        >
          <li
            v-for="run in runs"
            :key="run.id"
            class="signal__item"
          >
            <CheckIcon
              :state="runState(run)"
              :size="12"
              class="signal__icon"
            />
            <span
              class="signal__name"
              :title="run.name"
            >{{ run.name }}</span>
            <span class="signal__actions">
              <button
                v-if="checkState(run) === 'failing' && sent.has(`check:${run.name}`)"
                type="button"
                class="send-btn send-btn--sent"
                disabled
              >
                <Check
                  :size="11"
                  aria-hidden="true"
                />Sent
              </button>
              <button
                v-else-if="checkState(run) === 'failing'"
                type="button"
                class="send-btn"
                :disabled="sending.has(`check:${run.name}`)"
                :title="`Send the ${run.name} failure to the agent`"
                @click="sendCheck(run)"
              >
                <LoaderCircle
                  v-if="sending.has(`check:${run.name}`)"
                  :size="11"
                  class="send-btn__spinner"
                  aria-hidden="true"
                />
                <Send
                  v-else
                  :size="11"
                  aria-hidden="true"
                />Send to agent
              </button>
              <span
                v-else-if="checkState(run) === 'running'"
                class="signal__muted"
              >running</span>
              <a
                v-if="run.htmlUrl"
                :href="run.htmlUrl"
                class="signal__ext"
                target="_blank"
                rel="noopener noreferrer"
                :aria-label="`Open ${run.name} on GitHub`"
              ><ExternalLink
                :size="11"
                aria-hidden="true"
              /></a>
            </span>
          </li>
        </ul>
      </template>

      <div
        v-if="reviewers.length > 0"
        class="signal signal--static signal--avatars"
      >
        <Users
          :size="14"
          class="signal__icon"
          aria-hidden="true"
        />
        <span class="signal__what">Reviews</span>
        <span class="signal__detail">{{ reviewSummary }}</span>
        <span class="signal__avatars">
          <ReviewerAvatar
            v-for="reviewer in reviewers"
            :key="reviewer.login"
            :login="reviewer.login"
            :avatar-url="reviewer.avatarUrl"
            :state="reviewer.state"
            :size="16"
          />
        </span>
      </div>

      <template v-if="threads.length > 0">
        <button
          type="button"
          class="signal"
          :aria-expanded="reviewOpen"
          @click="reviewOpen = !reviewOpen"
        >
          <MessageSquare
            :size="14"
            class="signal__icon"
            data-tone="warn"
            aria-hidden="true"
          />
          <span class="signal__what">Review</span>
          <span class="signal__detail">{{ threads.length }} unresolved thread{{ threads.length === 1 ? "" : "s" }}</span>
          <ChevronRight
            :size="12"
            class="signal__chevron"
            aria-hidden="true"
          />
        </button>
        <ul
          v-if="reviewOpen"
          class="signal__list"
          aria-label="Unresolved review threads"
        >
          <li
            v-for="thread in threads"
            :key="thread.threadNodeId"
            class="signal__item signal__item--thread"
          >
            <MessageSquare
              :size="12"
              class="signal__icon"
              data-tone="warn"
              aria-hidden="true"
            />
            <span class="signal__name">
              <span
                class="signal__path"
                :title="`${thread.path}${thread.line ? `:${thread.line}` : ''}`"
              >{{ thread.path.split("/").pop() }}{{ thread.line ? `:${thread.line}` : "" }}</span>
              <span
                v-if="thread.comments[0]"
                class="signal__quote"
                :title="thread.comments[0].body"
              >{{ thread.comments[0].authorLogin }}: {{ thread.comments[0].body }}</span>
            </span>
            <span class="signal__actions">
              <button
                v-if="sent.has(`thread:${thread.threadNodeId}`)"
                type="button"
                class="send-btn send-btn--sent"
                disabled
              >
                <Check
                  :size="11"
                  aria-hidden="true"
                />Sent
              </button>
              <button
                v-else
                type="button"
                class="send-btn"
                :disabled="sending.has(`thread:${thread.threadNodeId}`)"
                title="Send this review comment to the agent"
                @click="sendThread(thread)"
              >
                <LoaderCircle
                  v-if="sending.has(`thread:${thread.threadNodeId}`)"
                  :size="11"
                  class="send-btn__spinner"
                  aria-hidden="true"
                />
                <Send
                  v-else
                  :size="11"
                  aria-hidden="true"
                />Send
              </button>
              <a
                v-if="thread.comments[0]?.url"
                :href="thread.comments[0].url"
                class="signal__ext"
                target="_blank"
                rel="noopener noreferrer"
                :aria-label="`Open the thread on ${thread.path} on GitHub`"
              ><ExternalLink
                :size="11"
                aria-hidden="true"
              /></a>
            </span>
          </li>
        </ul>
      </template>

      <div
        v-if="conflict"
        class="signal signal--static signal--bad"
      >
        <TriangleAlert
          :size="14"
          class="signal__icon"
          data-tone="warn"
          aria-hidden="true"
        />
        <span class="signal__what">Merge conflicts</span>
        <span class="signal__detail">{{ baseRef ? `with ${baseRef}` : "" }}</span>
      </div>
    </div>

    <p
      v-if="sendError"
      class="link-card__error"
      role="alert"
    >
      {{ sendError }}
    </p>
  </article>
</template>

<style scoped>
.link-card {
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  overflow: hidden;
}

.link-card__head {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr) auto;
  gap: 8px;
  padding: 10px 10px 10px 12px;
}

.link-card__icon {
  margin-top: 2px;
}

.link-card__main {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 5px;
}

.link-card__title {
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  line-height: 1.35;
  text-decoration: none;
  overflow-wrap: anywhere;
}

.link-card__title:hover {
  text-decoration: underline;
  text-underline-offset: 2px;
}

.link-card__meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 6px;
}

.signal__bar {
  padding: 0 12px 8px 34px;
}

.signal.signal--avatars {
  grid-template-columns: 16px auto minmax(0, 1fr) auto;
}

.signal__avatars {
  display: inline-flex;
  gap: 5px;
  padding-right: 2px;
}

.link-card__note {
  font-size: 11.5px;
  color: var(--muted);
}

.link-card__branch {
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono-stack);
  font-size: 11px;
  color: var(--muted);
}

.link-card__trail {
  display: flex;
  align-items: flex-start;
  gap: 2px;
}

.link-card__number {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  height: 22px;
  padding: 0 4px;
  border-radius: 5px;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  text-decoration: none;
}

.link-card__number:hover {
  color: var(--text);
}

.link-card__icon-btn {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.link-card__icon-btn:hover {
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--text);
}

.link-card__icon-btn:focus-visible,
.link-card__number:focus-visible,
.signal:focus-visible,
.send-btn:focus-visible,
.signal__ext:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}

.link-card__signals {
  display: flex;
  flex-direction: column;
  border-top: 1px solid var(--border);
}

.signal {
  display: grid;
  grid-template-columns: 16px auto minmax(0, 1fr) 12px;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 12px;
  border: 0;
  background: transparent;
  color: var(--text);
  font-size: 12.5px;
  text-align: left;
  cursor: pointer;
}

.signal + .signal,
.signal__list + .signal {
  border-top: 1px solid var(--border);
}

.signal:hover:not(.signal--static) {
  background: color-mix(in srgb, var(--text) 4%, transparent);
}

.signal--static {
  cursor: default;
}

.signal--bad {
  background: color-mix(in srgb, var(--error) 6%, transparent);
}

.signal--bad:hover:not(.signal--static) {
  background: color-mix(in srgb, var(--error) 10%, transparent);
}

.signal__what {
  font-weight: 500;
}

.signal__detail {
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  text-align: right;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.signal__chevron {
  color: var(--muted);
  transition: transform var(--transition);
}

.signal[aria-expanded="true"] .signal__chevron {
  transform: rotate(90deg);
}

.signal__icon {
  flex-shrink: 0;
  color: var(--muted);
}

.signal__icon[data-tone="bad"] {
  color: var(--error);
}

.signal__icon[data-tone="warn"] {
  color: var(--status-waiting);
}

.signal__icon[data-tone="good"] {
  color: var(--running);
}

.signal__icon[data-tone="merged"] {
  color: var(--queued);
}

.signal__list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 2px 12px 8px 36px;
  list-style: none;
}

.signal__item {
  display: grid;
  grid-template-columns: 14px minmax(0, 1fr) auto;
  align-items: center;
  gap: 7px;
  min-height: 26px;
  font-size: 12px;
}

.signal__item--thread {
  align-items: start;
  padding-block: 3px;
}

.signal__item--thread > .signal__icon {
  margin-top: 3px;
}

.signal__name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.signal__path {
  font-family: var(--font-mono-stack);
  font-size: 11px;
}

.signal__quote {
  display: block;
  overflow: hidden;
  color: var(--muted);
  font-size: 11.5px;
  text-overflow: ellipsis;
}

.signal__actions {
  display: flex;
  align-items: center;
  gap: 4px;
}

.signal__muted {
  color: var(--muted);
  font-size: 11px;
}

.signal__ext {
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  border-radius: 5px;
  color: var(--muted);
}

.signal__ext:hover {
  color: var(--text);
}

.send-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 11.5px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
}

.send-btn:hover:not(:disabled) {
  border-color: var(--accent);
  color: var(--accent);
}

.send-btn:disabled {
  cursor: default;
}

.send-btn--sent {
  border-color: transparent;
  color: var(--running);
}

.send-btn__spinner {
  animation: link-card-spin 0.9s linear infinite;
}

.link-card__error {
  margin: 0;
  padding: 0 12px 10px;
  color: var(--error);
  font-size: 11.5px;
}

.link-card--attention {
  border-color: color-mix(in srgb, var(--pr-blocked) 35%, var(--border));
}

@keyframes link-card-spin {
  to { transform: rotate(360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .signal__chevron {
    transition: none;
  }

  .send-btn__spinner {
    animation: none;
  }
}
</style>
