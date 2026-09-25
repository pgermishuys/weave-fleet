<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Archive, Check, ChevronDown, ExternalLink, GitMerge, LoaderCircle, MessageSquare, Send, Sparkles, TriangleAlert } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import CheckIcon from "@/components/github/CheckIcon.vue";
import ChecksSummary from "@/components/github/ChecksSummary.vue";
import DiffStat from "@/components/github/DiffStat.vue";
import GitHubItemIcon from "@/components/github/GitHubItemIcon.vue";
import PrStatePill from "@/components/github/PrStatePill.vue";
import ReviewerAvatar from "@/components/github/ReviewerAvatar.vue";
import { useSendToAgent } from "@/composables/use-send-to-agent";
import { blockReasons, prState, prStateLabel, prWords } from "@/lib/pr-state";
import {
  checkState,
  ciStatus,
  formatCheckFailurePrompt,
  formatFixPrompt,
  formatReviewThreadPrompt,
  linkDiff,
  linkHref,
  linkNumber,
  linkPrFacts,
  linkReviewers,
  linkTitle,
  reviewThreads,
  type CheckRun,
  type ReviewThread,
  type SmartLink,
} from "@/lib/smart-links";
import { useArchiveQueueStore } from "@/stores/archive-queue";

/**
 * The session's pull request in the header: its number in its state's colour and what it's waiting on in words.
 * Opening it shows the checks and review threads, with the fix one click away.
 */
const props = defineProps<{
  link: SmartLink;
}>();

const emit = defineEmits<{
  showContext: [linkId: string];
}>();

const archiveQueue = useArchiveQueueStore();
const open = shallowRef(false);

const facts = computed(() => linkPrFacts(props.link));
const state = computed(() => prState(facts.value));
const words = computed(() => prWords(facts.value));
const number = computed(() => linkNumber(props.link));
const title = computed(() => linkTitle(props.link));
const href = computed(() => linkHref(props.link));
const diff = computed(() => linkDiff(props.link));
const reviewers = computed(() => linkReviewers(props.link));
const threads = computed(() => reviewThreads(props.link));
const runs = computed(() => ciStatus(props.link)?.checkRuns ?? []);
const failingRuns = computed(() => runs.value.filter((run) => checkState(run) === "failing"));
// Passed checks are the bar; the list is what's left to look at.
const listedRuns = computed(() => runs.value.filter((run) => checkState(run) !== "passed").slice(0, 4));
const headRef = computed(() => stringMeta("headRef"));
const baseRef = computed(() => stringMeta("baseRef") ?? "main");
const repo = computed(() => stringMeta("repo"));
const resolved = computed(() => props.link.enrichmentStatus === "resolved");
const isOpen = computed(() => facts.value.state === "open");
const changesBy = computed(() => reviewers.value.filter((r) => r.state === "CHANGES_REQUESTED").map((r) => r.login));

const description = computed(() => `Pull request #${number.value}: ${title.value}. ${words.value}`);

function stringMeta(key: string): string | null {
  const value = props.link.metadata[key];
  return typeof value === "string" && value ? value : null;
}

const { send, sending, sent, error } = useSendToAgent(() => props.link.sessionId);

const fixLabel = computed(() => {
  const checks = failingRuns.value.length > 0;
  const review = threads.value.length > 0;
  if (checks && review) return "Fix checks and address review";
  if (checks) return "Fix failing checks";
  if (review) return "Address review";
  return null;
});

async function sendFix(): Promise<void> {
  await send("fix", formatFixPrompt(props.link, failingRuns.value, threads.value));
}

const sendCheck = (run: CheckRun) => send(`check:${run.name}`, formatCheckFailurePrompt(props.link, run));
const sendThread = (thread: ReviewThread) => send(`thread:${thread.threadNodeId}`, formatReviewThreadPrompt(props.link, thread));

function showInContext(): void {
  open.value = false;
  emit("showContext", props.link.id);
}

function archiveSession(): void {
  open.value = false;
  archiveQueue.archive([props.link.sessionId]);
}

function runState(run: CheckRun) {
  switch (checkState(run)) {
    case "failing": return "failure";
    case "running": return "pending";
    case "passed": return "success";
    default: return "neutral";
  }
}
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button
        type="button"
        class="pr-pill-trigger"
        :data-pr="resolved ? state : 'draft'"
        :aria-label="description"
        :title="description"
        data-testid="session-pr-pill"
      >
        <span class="pr-pill-trigger__number">
          <GitHubItemIcon
            kind="pull"
            :state="resolved ? state : 'draft'"
            :size="13"
          />#{{ number }}
        </span>
        <span
          v-if="resolved"
          class="pr-pill-trigger__words"
        >
          <CheckIcon
            v-if="isOpen && facts.checks !== 'none'"
            :state="facts.checks === 'failing' ? 'failure' : facts.checks === 'pending' ? 'pending' : 'success'"
            :size="12"
          />
          {{ words }}
          <ChevronDown
            :size="12"
            aria-hidden="true"
          />
        </span>
      </button>
    </PopoverTrigger>

    <PopoverContent
      side="bottom"
      align="end"
      :side-offset="6"
      :collision-padding="8"
      class="pr-popover"
      data-testid="session-pr-popover"
    >
      <header class="pr-popover__head">
        <div class="pr-popover__meta">
          <PrStatePill
            :state="state"
            :label="prStateLabel(state)"
          />
          <span class="pr-popover__repo">{{ repo }} #{{ number }}</span>
          <DiffStat
            v-if="diff"
            class="pr-popover__diff"
            :additions="diff.additions"
            :deletions="diff.deletions"
          />
        </div>
        <a
          class="pr-popover__title"
          :href="href"
          target="_blank"
          rel="noopener noreferrer"
        >{{ title }}</a>
        <div class="pr-popover__meta">
          <template v-if="headRef">
            <span class="pr-popover__branch">{{ headRef }}</span>
            <span aria-hidden="true">→</span>
            <span class="pr-popover__branch">{{ baseRef }}</span>
          </template>
          <span
            v-if="reviewers.length"
            class="pr-popover__reviewers"
          >
            <ReviewerAvatar
              v-for="reviewer in reviewers"
              :key="reviewer.login"
              :login="reviewer.login"
              :avatar-url="reviewer.avatarUrl"
              :state="reviewer.state"
            />
          </span>
        </div>
      </header>

      <section
        v-if="facts.state === 'merged'"
        class="pr-popover__section"
      >
        <p class="pr-popover__line pr-popover__line--merged">
          <GitMerge
            :size="14"
            aria-hidden="true"
          />
          Merged into <span class="pr-popover__branch">{{ baseRef }}</span>
        </p>
        <p class="pr-popover__note">
          Nothing is left to do on this branch. You can archive the session.
        </p>
      </section>

      <template v-else-if="isOpen">
        <section
          v-if="runs.length"
          class="pr-popover__section"
          aria-label="Checks"
        >
          <ChecksSummary :counts="facts.checkCounts!" />
          <ul
            v-if="listedRuns.length"
            class="pr-popover__list"
          >
            <li
              v-for="run in listedRuns"
              :key="run.id"
              class="pr-popover__row"
            >
              <CheckIcon
                :state="runState(run)"
                :size="13"
              />
              <span class="pr-popover__name">{{ run.name }}</span>
              <button
                v-if="checkState(run) === 'failing'"
                type="button"
                class="pr-popover__send"
                :disabled="sending.has(`check:${run.name}`) || sent.has(`check:${run.name}`)"
                :title="`Send the ${run.name} failure to the agent`"
                @click="sendCheck(run)"
              >
                <Check
                  v-if="sent.has(`check:${run.name}`)"
                  :size="11"
                  aria-hidden="true"
                />
                <LoaderCircle
                  v-else-if="sending.has(`check:${run.name}`)"
                  :size="11"
                  class="pr-popover__spin"
                  aria-hidden="true"
                />
                <Send
                  v-else
                  :size="11"
                  aria-hidden="true"
                />
                {{ sent.has(`check:${run.name}`) ? "Sent" : "Send to agent" }}
              </button>
              <span
                v-else
                class="pr-popover__muted"
              >{{ checkState(run) === "running" ? "running" : run.conclusion ?? "" }}</span>
            </li>
          </ul>
        </section>

        <section
          v-if="threads.length || changesBy.length"
          class="pr-popover__section"
          aria-label="Review"
        >
          <p class="pr-popover__line">
            <MessageSquare
              :size="14"
              class="pr-popover__warn"
              aria-hidden="true"
            />
            <template v-if="changesBy.length">
              {{ changesBy.join(", ") }} requested changes
            </template>
            <template v-else>
              {{ threads.length }} unresolved thread{{ threads.length === 1 ? "" : "s" }}
            </template>
          </p>
          <ul
            v-if="threads.length"
            class="pr-popover__list"
          >
            <li
              v-for="thread in threads.slice(0, 3)"
              :key="thread.threadNodeId"
              class="pr-popover__thread"
            >
              <ReviewerAvatar
                v-if="thread.comments[0]"
                :login="thread.comments[0].authorLogin"
              />
              <span class="pr-popover__thread-body">
                <span class="pr-popover__path">{{ thread.path }}{{ thread.line ? `:${thread.line}` : "" }}</span>
                <span class="pr-popover__quote">{{ thread.comments[0]?.body }}</span>
              </span>
              <button
                type="button"
                class="pr-popover__send"
                :disabled="sending.has(`thread:${thread.threadNodeId}`) || sent.has(`thread:${thread.threadNodeId}`)"
                title="Send this review comment to the agent"
                @click="sendThread(thread)"
              >
                <Check
                  v-if="sent.has(`thread:${thread.threadNodeId}`)"
                  :size="11"
                  aria-hidden="true"
                />
                <Send
                  v-else
                  :size="11"
                  aria-hidden="true"
                />
                {{ sent.has(`thread:${thread.threadNodeId}`) ? "Sent" : "Send" }}
              </button>
            </li>
          </ul>
        </section>

        <section
          v-if="facts.conflict"
          class="pr-popover__section"
        >
          <p class="pr-popover__line">
            <TriangleAlert
              :size="14"
              class="pr-popover__warn"
              aria-hidden="true"
            />
            Conflicts with <span class="pr-popover__branch">{{ baseRef }}</span>
          </p>
        </section>
      </template>

      <p
        v-if="error"
        class="pr-popover__error"
        role="alert"
      >
        {{ error }}
      </p>

      <footer class="pr-popover__foot">
        <button
          v-if="facts.state === 'merged'"
          type="button"
          class="pr-popover__btn"
          @click="archiveSession"
        >
          <Archive
            :size="13"
            aria-hidden="true"
          />
          Archive session
        </button>
        <button
          v-else-if="isOpen && fixLabel"
          type="button"
          class="pr-popover__btn pr-popover__btn--primary"
          :disabled="sending.has('fix') || sent.has('fix')"
          data-testid="session-pr-fix"
          @click="sendFix"
        >
          <Check
            v-if="sent.has('fix')"
            :size="13"
            aria-hidden="true"
          />
          <LoaderCircle
            v-else-if="sending.has('fix')"
            :size="13"
            class="pr-popover__spin"
            aria-hidden="true"
          />
          <Sparkles
            v-else
            :size="13"
            aria-hidden="true"
          />
          {{ sent.has("fix") ? "Sent to the agent" : fixLabel }}
        </button>
        <a
          v-else-if="isOpen && blockReasons(facts).length === 0 && facts.review === 'APPROVED'"
          class="pr-popover__btn pr-popover__btn--primary"
          :href="href"
          target="_blank"
          rel="noopener noreferrer"
        >
          <ExternalLink
            :size="13"
            aria-hidden="true"
          />
          Merge on GitHub
        </a>
        <button
          type="button"
          class="pr-popover__btn pr-popover__btn--ghost pr-popover__btn--end"
          @click="showInContext"
        >
          Show in Context
        </button>
        <a
          class="pr-popover__btn pr-popover__btn--ghost"
          :href="href"
          target="_blank"
          rel="noopener noreferrer"
          aria-label="Open on GitHub"
          title="Open on GitHub"
        >
          <ExternalLink
            :size="13"
            aria-hidden="true"
          />
        </a>
      </footer>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.pr-pill-trigger {
  display: inline-flex;
  align-items: stretch;
  height: 26px;
  max-width: 280px;
  overflow: hidden;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color var(--transition), background-color var(--transition);
}

.pr-pill-trigger:hover {
  border-color: color-mix(in srgb, var(--text) 18%, transparent);
}

.pr-pill-trigger:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.pr-pill-trigger[data-pr="open"] { --pr-tone: var(--pr-open); }
.pr-pill-trigger[data-pr="draft"] { --pr-tone: var(--pr-draft); }
.pr-pill-trigger[data-pr="blocked"] { --pr-tone: var(--pr-blocked); }
.pr-pill-trigger[data-pr="merged"] { --pr-tone: var(--pr-merged); }
.pr-pill-trigger[data-pr="closed"] { --pr-tone: var(--pr-closed); }

.pr-pill-trigger__number {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 0 8px;
  background: color-mix(in srgb, var(--pr-tone) 12%, transparent);
  color: var(--pr-tone);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  font-weight: 500;
}

.pr-pill-trigger__number :deep(.gh-item-icon) {
  color: inherit;
}

.pr-pill-trigger__words {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  overflow: hidden;
  padding: 0 8px;
  text-overflow: ellipsis;
}

.pr-popover__head {
  display: grid;
  gap: 6px;
  padding: 12px 14px 10px;
  border-bottom: 1px solid var(--border);
}

.pr-popover__meta {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  color: var(--muted);
  font-size: 11.5px;
}

.pr-popover__repo {
  white-space: nowrap;
}

.pr-popover__diff,
.pr-popover__reviewers {
  margin-left: auto;
}

.pr-popover__reviewers {
  display: inline-flex;
  gap: 6px;
  padding-right: 2px;
}

.pr-popover__title {
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  line-height: 1.35;
  text-decoration: none;
}

.pr-popover__title:hover {
  text-decoration: underline;
}

.pr-popover__branch {
  max-width: 180px;
  overflow: hidden;
  padding: 1px 6px;
  border-radius: 5px;
  background: var(--accent-dim);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.pr-popover__section {
  display: grid;
  gap: 8px;
  padding: 10px 14px;
  border-bottom: 1px solid var(--border);
}

.pr-popover__line {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: 12.5px;
  font-weight: 500;
}

.pr-popover__line--merged {
  color: var(--pr-merged);
}

.pr-popover__warn {
  flex-shrink: 0;
  color: var(--pr-blocked);
}

.pr-popover__note {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
}

.pr-popover__list {
  display: grid;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.pr-popover__row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 24px;
  font-size: 12.5px;
}

.pr-popover__name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.pr-popover__muted {
  color: var(--muted);
  font-size: 11.5px;
}

.pr-popover__thread {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 3px 0;
  font-size: 12.5px;
}

.pr-popover__thread-body {
  display: grid;
  flex: 1;
  min-width: 0;
}

.pr-popover__path {
  overflow: hidden;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.pr-popover__quote {
  display: -webkit-box;
  overflow: hidden;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.pr-popover__send {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
  height: 22px;
  padding: 0 7px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: transparent;
  color: var(--text);
  font-size: 11.5px;
  cursor: pointer;
}

.pr-popover__send:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.pr-popover__send:disabled {
  color: var(--muted);
  cursor: default;
}

.pr-popover__error {
  margin: 0;
  padding: 8px 14px;
  color: var(--error);
  font-size: 12px;
}

.pr-popover__foot {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 10px 14px;
  background: color-mix(in srgb, var(--text) 3%, transparent);
}

.pr-popover__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 12.5px;
  font-weight: 500;
  text-decoration: none;
  white-space: nowrap;
  cursor: pointer;
}

.pr-popover__btn:disabled {
  opacity: 0.7;
  cursor: default;
}

.pr-popover__btn--primary {
  border-color: transparent;
  background: var(--accent);
  color: var(--primary-foreground);
}

.pr-popover__btn--ghost {
  border-color: transparent;
  background: transparent;
  color: var(--muted);
}

.pr-popover__btn--ghost:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.pr-popover__btn--end {
  margin-left: auto;
}

.pr-popover__spin {
  animation: pr-popover-spin 1s linear infinite;
}

@keyframes pr-popover-spin {
  to { transform: rotate(360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .pr-popover__spin { animation: none; }
  .pr-pill-trigger { transition: none; }
}
</style>

<style>
/* The popover is teleported out of this component, so its box is styled without scoping. */
.pr-popover {
  width: 380px;
  max-width: calc(100vw - 16px);
  padding: 0;
  overflow: hidden;
  border-radius: var(--radius-card);
  background: var(--popover, var(--panel-bg));
  color: var(--popover-foreground, var(--text));
}
</style>
