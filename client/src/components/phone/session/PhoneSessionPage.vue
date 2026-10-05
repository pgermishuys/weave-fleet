<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, shallowRef, useTemplateRef, watch } from "vue";
import { useParams, useRouter, useSearch } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import { Check, CornerDownRight, ImageIcon, LoaderCircle } from "lucide-vue-next";
import ShellCommandBlock from "@/components/session/ShellCommandBlock.vue";
import FoldedStepsRow from "@/components/phone/session/FoldedStepsRow.vue";
import PhoneMarkdown from "@/components/phone/session/PhoneMarkdown.vue";
import PhonePlanBar from "@/components/phone/session/PhonePlanBar.vue";
import PhoneSessionHeader from "@/components/phone/session/PhoneSessionHeader.vue";
import ChangesSheet from "@/components/phone/session/ChangesSheet.vue";
import FilesSheet from "@/components/phone/session/FilesSheet.vue";
import OpenOnComputerCard from "@/components/phone/session/OpenOnComputerCard.vue";
import PlanSheet from "@/components/phone/session/PlanSheet.vue";
import SessionMenuSheet, { type MenuAction } from "@/components/phone/session/SessionMenuSheet.vue";
import SideConversationSheet from "@/components/phone/session/SideConversationSheet.vue";
import SinceYouLookedMarker from "@/components/phone/session/SinceYouLookedMarker.vue";
import StepsSheet from "@/components/phone/session/StepsSheet.vue";
import DockedPermission from "@/components/phone/session/DockedPermission.vue";
import DockedQuestion from "@/components/phone/session/DockedQuestion.vue";
import PhoneComposer from "@/components/phone/session/PhoneComposer.vue";
import { harnessCapabilities } from "@/composables/use-composer-actions";
import { useDeskPresence } from "@/composables/use-desk-presence";
import { useMachineReachability } from "@/composables/phone/use-machine-reachability";
import UnreachableBanner from "@/components/phone/session/UnreachableBanner.vue";
import { useDiffs } from "@/composables/use-diffs";
import { useHarnesses } from "@/composables/use-harnesses";
import { useRunShellCommand } from "@/composables/use-run-shell-command";
import { useAbortSession, useArchiveSession, useForkSession, useRenameSession } from "@/composables/use-session-actions";
import { useQuestionAnswer } from "@/composables/use-question-answer";
import { useSessionPermissions } from "@/composables/use-session-permissions";
import { chooseDock, pendingQuestion } from "@/lib/phone/dock-state";
import { useRelativeTime } from "@/composables/use-relative-time";
import { useSessionProgress } from "@/composables/use-session-progress";
import { useSessionStream } from "@/composables/use-session-stream";
import { useSessions } from "@/composables/use-sessions";
import { readCredentialsSync } from "@/lib/device-credentials";
import { getActiveMachine } from "@/lib/machines";
import { foldMessages, type FoldedStep } from "@/lib/phone/fold-steps";
import { lastSeenAt, markSeen, sinceYouLookedIndex } from "@/lib/phone/last-seen";
import { headerStatus } from "@/lib/phone/session-status";
import { useSessionsStore } from "@/stores/sessions";

/**
 * A session on the phone (`/phone/s/<machine>/<session>`), per mockups/phone-app/session.html: the header is the
 * status, a plan bar when there's a plan, the conversation with each run of tool calls folded into one row, and the
 * dock at the bottom (the composer, or whatever the agent is waiting on).
 */
const params = useParams({ from: "/phone/s/$machineId/$sessionId" });
const search = useSearch({ from: "/phone/s/$machineId/$sessionId" });
const router = useRouter();
const now = useRelativeTime();
useDeskPresence("phone");

const machineId = computed(() => params.value.machineId);
const sessionId = computed(() => params.value.sessionId);

useSessions({ retentionStatus: "all" });
const sessionsStore = useSessionsStore();
const { sessions } = storeToRefs(sessionsStore);
const session = computed(() => sessions.value.find((item) => item.session.id === sessionId.value) ?? null);

const stream = useSessionStream(sessionId);
const { progress } = useSessionProgress(sessionId);
const blocks = computed(() => foldMessages(stream.messages.value));

const machineName = computed(() => getActiveMachine()?.name ?? readCredentialsSync()?.homeMachineName ?? "This machine");
const title = computed(() => session.value?.session.title?.trim() || "Session");

const turnStartedAt = computed(() => {
  for (const message of [...stream.messages.value].reverse()) {
    if (message.role === "user") return message.createdAt ?? null;
  }
  return null;
});
const lastMessageAt = computed(() => stream.messages.value.at(-1)?.createdAt ?? null);
const updatedAt = computed(() => {
  const time = session.value?.session.time;
  const value = time?.updated ?? time?.created;
  return typeof value === "number" ? value : value ? Date.parse(value) : null;
});

// The machine dropping out: the conversation stays as last heard; what you type is held until it's back.
const reachability = useMachineReachability(() => undefined);
watch(() => stream.messages.value, () => reachability.heard());

const status = computed(() => headerStatus({
  sessionStatus: session.value?.sessionStatus ?? null,
  streamStatus: stream.isLoading.value ? null : stream.sessionStatus.value,
  turnStartedAt: turnStartedAt.value,
  updatedAt: updatedAt.value,
  lastMessageAt: lastMessageAt.value,
  hasMessages: stream.messages.value.length > 0,
  unreachableSince: reachability.reachable.value ? null : reachability.lastHeardAt.value,
  now: now.value,
}));
const lastStatus = shallowRef(status.value);
watch(status, (next) => {
  if (next.tone !== "unreachable") lastStatus.value = next;
});

// "Since you looked": read once on arrival; written when you leave or the app goes off screen.
const seenAt = shallowRef<number | null>(lastSeenAt(machineId.value, sessionId.value));
const markerIndex = computed(() => sinceYouLookedIndex(blocks.value, seenAt.value));

function rememberSeen(): void {
  markSeen(machineId.value, sessionId.value, Date.now());
}

function onVisibility(): void {
  if (document.visibilityState === "hidden") rememberSeen();
}

const scrollRef = useTemplateRef<HTMLElement>("scroll");
let scrolledOnArrival = false;

/** On arrival: to what's new when there is a marker (and no ask to jump to), else to the bottom. */
watch(() => [stream.isLoading.value, blocks.value.length] as const, async ([loading]) => {
  if (loading || scrolledOnArrival) return;
  scrolledOnArrival = true;
  await nextTick();
  const el = scrollRef.value;
  if (!el) return;
  const marker = el.querySelector("[data-testid=since-you-looked]");
  if (marker && !search.value.ask) marker.scrollIntoView?.({ block: "start" });
  else el.scrollTop = el.scrollHeight;
}, { immediate: true });

// Stay at the bottom while new things arrive, unless you scrolled up to read.
watch(() => blocks.value.length, async () => {
  const el = scrollRef.value;
  if (!el || !scrolledOnArrival) return;
  const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 120;
  await nextTick();
  if (nearBottom) el.scrollTop = el.scrollHeight;
});

// The bottom is for whatever needs you: the ask in the composer's place, until it's answered or put off.
const { asks, answer: answerPermission } = useSessionPermissions(sessionId);
const questions = useQuestionAnswer(sessionId.value);
const later = shallowRef<ReadonlySet<string>>(new Set());
const dock = computed(() => chooseDock({
  permissions: asks.value,
  question: pendingQuestion(stream.messages.value),
  later: later.value,
  focus: search.value.ask ?? null,
}));
const answered = shallowRef<string | null>(null);
const askGone = shallowRef(false);

function putOff(id: string): void {
  later.value = new Set([...later.value, id]);
}

function bringBack(): void {
  later.value = new Set();
}

function onAnswered(text: string): void {
  answered.value = text;
  setTimeout(() => {
    if (answered.value === text) answered.value = null;
  }, 4000);
}

// Opened from a notification for an ask that's no longer waiting: say so instead of looking for it. The asks load on
// their own, so look a moment after the conversation is in.
let askChecked = false;
watch(() => stream.isLoading.value, (loading) => {
  const target = search.value.ask;
  if (!target || loading || askChecked) return;
  askChecked = true;
  setTimeout(() => {
    const waiting = asks.value.some((ask) => ask.id === target) || pendingQuestion(stream.messages.value)?.requestId === target;
    if (waiting) return;
    askGone.value = true;
    setTimeout(() => {
      askGone.value = false;
    }, 4000);
  }, 1500);
}, { immediate: true });

// The ⋯ menu and what it opens.
const sheet = shallowRef<"menu" | "changes" | "files" | "side" | "terminal" | null>(null);
const { harnesses } = useHarnesses();
const caps = computed(() => harnessCapabilities(session.value?.harnessType, harnesses.value));
const { diffs, isLoading: diffsLoading, fetchDiffs } = useDiffs(sessionId);
const { runShellCommand } = useRunShellCommand(sessionId.value);
const { abortSession } = useAbortSession();
const { archiveSession } = useArchiveSession();
const { forkSession } = useForkSession();
const { renameSession } = useRenameSession();
watch(sheet, (open) => {
  if (open === "menu") void fetchDiffs();
});
const computerLink = computed(() => `${getActiveMachine()?.baseUrl ?? window.location.origin}/sessions/${encodeURIComponent(sessionId.value)}`);

async function onMenu(action: MenuAction): Promise<void> {
  switch (action) {
    case "changes":
      sheet.value = "changes";
      void fetchDiffs();
      break;
    case "files":
    case "side":
    case "terminal":
      sheet.value = action;
      break;
    case "computer":
      sheet.value = "terminal";
      break;
    case "stop":
      sheet.value = null;
      await abortSession(sessionId.value).catch(() => undefined);
      break;
    case "archive":
      sheet.value = null;
      await archiveSession(sessionId.value).catch(() => undefined);
      back();
      break;
    case "fork": {
      sheet.value = null;
      const forked = await forkSession(sessionId.value).catch(() => null);
      if (forked) void router.navigate({ to: "/phone/s/$machineId/$sessionId", params: { machineId: machineId.value, sessionId: forked.session.id } });
      break;
    }
  }
}

async function onRename(next: string): Promise<void> {
  sheet.value = null;
  if (next) await renameSession(sessionId.value, next).catch(() => undefined);
}

function runCommand(command: string): void {
  sheet.value = null;
  void runShellCommand(command);
}

const stepsOpen = shallowRef<readonly FoldedStep[] | null>(null);
const planOpen = shallowRef(false);

function back(): void {
  rememberSeen();
  // A session on another machine was opened with a page load there; going back loads home again.
  if (getActiveMachine()) window.location.assign("/phone");
  else void router.navigate({ to: "/phone" });
}

function openChild(childId: string): void {
  rememberSeen();
  void router.navigate({ to: "/phone/s/$machineId/$sessionId", params: { machineId: machineId.value, sessionId: childId } });
}

onMounted(() => {
  sessionsStore.setActiveSessionId(sessionId.value);
  document.addEventListener("visibilitychange", onVisibility);
});

onUnmounted(() => {
  rememberSeen();
  document.removeEventListener("visibilitychange", onVisibility);
});
</script>

<template>
  <div
    class="ps"
    data-testid="phone-session"
  >
    <PhoneSessionHeader
      :title="title"
      :machine-name="machineName"
      :tone="status.tone"
      :state="status.state"
      :detail="status.detail"
      @back="back"
      @menu="sheet = 'menu'"
    />
    <UnreachableBanner
      v-if="!reachability.reachable.value"
      :machine-name="machineName"
      :retry-in="reachability.retryIn.value"
      @retry="reachability.retry"
    />
    <PhonePlanBar
      v-if="progress && progress.total > 0"
      :progress="progress"
      @open="planOpen = true"
    />

    <main
      ref="scroll"
      class="ps__scroll"
    >
      <button
        v-if="stream.hasMore.value"
        type="button"
        class="ps__older"
        :disabled="stream.isLoadingOlder.value"
        @click="stream.loadOlder"
      >
        {{ stream.isLoadingOlder.value ? "Loading…" : "Earlier messages" }}
      </button>
      <div
        v-if="stream.isLoading.value && blocks.length === 0"
        class="ps__loading"
        role="status"
      >
        <LoaderCircle
          class="animate-spin"
          :size="20"
          aria-hidden="true"
        />
      </div>

      <template
        v-for="(block, index) in blocks"
        :key="block.key"
      >
        <SinceYouLookedMarker
          v-if="index === markerIndex && seenAt"
          :at="seenAt"
        />
        <div
          v-if="block.kind === 'user'"
          class="ps__you"
          data-testid="phone-user-message"
        >
          <span
            v-if="block.steered"
            class="ps__steered"
          ><CornerDownRight
            :size="12"
            aria-hidden="true"
          /> sent into the turn</span>
          <span
            v-if="block.images"
            class="ps__images"
          ><ImageIcon
            :size="12"
            aria-hidden="true"
          /> {{ block.images }} image{{ block.images === 1 ? "" : "s" }}</span>
          <p class="ps__you-text">
            {{ block.text }}
          </p>
        </div>
        <PhoneMarkdown
          v-else-if="block.kind === 'text'"
          :text="block.text"
          class="ps__agent"
        />
        <FoldedStepsRow
          v-else-if="block.kind === 'steps'"
          :summary="block.summary"
          :running="block.running"
          :failed="block.failed"
          :count="block.steps.length"
          @open="stepsOpen = block.steps"
        />
        <button
          v-else-if="block.kind === 'subagent'"
          type="button"
          class="ps__subagent"
          :disabled="!block.childSessionId"
          data-testid="phone-subagent"
          @click="block.childSessionId && openChild(block.childSessionId)"
        >
          <span class="ps__agent-name">{{ block.agent }}</span>
          <span class="ps__subagent-title">{{ block.title }}</span>
          <LoaderCircle
            v-if="block.running"
            class="animate-spin text-muted"
            :size="13"
            aria-hidden="true"
          />
          <span
            v-else
            class="ps__done"
          >Done</span>
        </button>
        <p
          v-else-if="block.kind === 'question' && !block.pending"
          class="ps__asked"
        >
          Asked · {{ block.question }} <template v-if="block.answer">
            → {{ block.answer }} <Check
              :size="12"
              class="inline text-running"
              aria-hidden="true"
            />
          </template>
        </p>
        <ShellCommandBlock
          v-else-if="block.kind === 'shell'"
          :command="block.view"
        />
        <p
          v-else-if="block.kind === 'error'"
          class="ps__error"
          role="alert"
        >
          {{ block.text }}
        </p>
      </template>

      <p
        v-if="status.tone === 'working'"
        class="ps__working"
        data-testid="phone-working"
      >
        <span
          class="ps__pulse"
          aria-hidden="true"
        />{{ status.state }} · {{ status.detail }}
      </p>
      <p
        v-else-if="status.tone === 'unreachable' && lastStatus.tone === 'working'"
        class="ps__working"
      >
        Working when last heard
      </p>
    </main>

    <p
      v-if="askGone"
      class="ps__toast"
      role="status"
      data-testid="already-answered"
    >
      Already answered.
    </p>
    <p
      v-if="answered"
      class="ps__toast"
      role="status"
    >
      {{ answered }}.
    </p>
    <button
      v-if="dock.later > 0 && dock.kind === 'composer'"
      type="button"
      class="ps__pill"
      data-testid="later-pill"
      @click="bringBack"
    >
      {{ dock.later }} waiting on you · Answer
    </button>
    <DockedPermission
      v-if="dock.kind === 'permission'"
      :key="dock.ask.id"
      :ask="dock.ask"
      :answer="answerPermission"
      @later="putOff(dock.ask.id)"
      @answered="onAnswered"
    />
    <DockedQuestion
      v-else-if="dock.kind === 'question'"
      :key="dock.pending.requestId"
      :pending="dock.pending"
      :answer="questions.answerQuestion"
      :reject="questions.rejectQuestion"
      @later="putOff(dock.pending.requestId)"
    />
    <PhoneComposer
      v-else
      :key="sessionId"
      :session-id="sessionId"
      :machine-id="machineId"
      :machine-name="machineName"
      :reachable="reachability.reachable.value"
      @side="sheet = 'side'"
    />

    <SessionMenuSheet
      :open="sheet === 'menu'"
      :title="title"
      :machine-name="machineName"
      :changed-files="diffs.length"
      :working="status.tone === 'working'"
      :supports-side="caps.supportsSide"
      :can-fork="session?.capabilities?.canFork ?? true"
      @pick="onMenu"
      @rename="onRename"
      @close="sheet = null"
    />
    <ChangesSheet
      :open="sheet === 'changes'"
      :session-id="sessionId"
      :diffs="diffs"
      :loading="diffsLoading"
      @close="sheet = null"
    />
    <FilesSheet
      :open="sheet === 'files'"
      :session-id="sessionId"
      @close="sheet = null"
    />
    <SideConversationSheet
      :open="sheet === 'side'"
      :session-id="sessionId"
      @kept="openChild"
      @close="sheet = null"
    />
    <OpenOnComputerCard
      :open="sheet === 'terminal'"
      title="Terminal"
      :machine-name="machineName"
      :link="computerLink"
      :supports-shell="caps.supportsShell"
      @run="runCommand"
      @close="sheet = null"
    />

    <StepsSheet
      :open="stepsOpen !== null"
      :steps="stepsOpen ?? []"
      @close="stepsOpen = null"
    />
    <PlanSheet
      :open="planOpen"
      :progress="progress"
      @close="planOpen = false"
    />
  </div>
</template>

<style scoped>
.ps {
  display: flex;
  flex-direction: column;
  height: 100dvh;
  min-height: 0;
}

.ps__scroll {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
  min-height: 0;
  overflow-y: auto;
  padding: 12px 14px 16px;
  overscroll-behavior: contain;
}

.ps__loading {
  display: grid;
  flex: 1;
  place-items: center;
  color: var(--muted);
}

.ps__older {
  align-self: center;
  min-height: 36px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  color: var(--muted);
  font: inherit;
  font-size: 12px;
}

.ps__you {
  display: grid;
  gap: 4px;
  align-self: flex-end;
  max-width: 86%;
  padding: 8px 12px;
  border-radius: var(--radius-panel);
  background: var(--accent-dim);
}

.ps__you-text {
  font-size: 15px;
  line-height: 1.45;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.ps__steered,
.ps__images {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: var(--muted);
}

.ps__agent {
  color: var(--text);
}

.ps__subagent {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 44px;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  text-align: left;
}

.ps__agent-name {
  font-family: var(--font-mono-stack);
  font-size: 11px;
  color: var(--accent);
}

.ps__subagent-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.ps__done {
  font-size: 12px;
  font-weight: 600;
  color: var(--accent);
}

.ps__asked {
  font-size: 12px;
  color: var(--muted);
}

.ps__error {
  padding: 8px 12px;
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--error) 10%, transparent);
  font-size: 13px;
  color: var(--error);
}

.ps__toast {
  align-self: center;
  margin: 0 0 6px;
  padding: 6px 12px;
  border-radius: 999px;
  background: var(--text);
  color: var(--main-bg);
  font-size: 12px;
}

.ps__pill {
  align-self: center;
  min-height: 36px;
  margin-bottom: 6px;
  padding: 0 14px;
  border: 1px solid color-mix(in srgb, var(--idle) 45%, transparent);
  border-radius: 999px;
  background: color-mix(in srgb, var(--idle) 12%, transparent);
  color: var(--text);
  font: inherit;
  font-size: 13px;
}

.ps__working {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--muted);
}

.ps__pulse {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--running);
  animation: ps-pulse var(--transition-pulse) ease-in-out infinite;
}

@keyframes ps-pulse {
  50% {
    opacity: 0.35;
  }
}
</style>
