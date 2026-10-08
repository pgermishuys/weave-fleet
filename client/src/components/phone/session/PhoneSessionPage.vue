<script setup lang="ts">
import { computed, inject, nextTick, onMounted, onUnmounted, shallowRef, useTemplateRef, watch } from "vue";
import { showToast } from "@/composables/phone/use-phone-toast";
import { haptic } from "@/lib/phone/haptics";
import { holdKeyboard } from "@/lib/phone/keyboard";
import { shareLink } from "@/lib/phone/share";
import { ago } from "@/lib/phone/time";
import { Check, CornerDownRight, ImageIcon, LoaderCircle } from "lucide-vue-next";
import PhoneGlyph from "@/components/phone/PhoneGlyph.vue";
import ShellCommandBlock from "@/components/session/ShellCommandBlock.vue";
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
import PhoneToolRun from "@/components/phone/session/PhoneToolRun.vue";
import PhoneRetryLine from "@/components/phone/session/PhoneRetryLine.vue";
import DockedPermission from "@/components/phone/session/DockedPermission.vue";
import DockedQuestion from "@/components/phone/session/DockedQuestion.vue";
import PhoneComposer from "@/components/phone/session/PhoneComposer.vue";
import { harnessCapabilities } from "@/composables/use-composer-actions";
import { useDeskPresence } from "@/composables/use-desk-presence";
import { useMachineReachability } from "@/composables/phone/use-machine-reachability";
import { useMachineWebApp } from "@/composables/phone/use-machine-web-app";
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
import { useMachineTarget } from "@/lib/machine-target";
import { foldMessages, groupTools, type FoldedStep } from "@/lib/phone/fold-steps";
import { sessionFolder } from "@/lib/phone/inbox";
import { lastSeenAt, markSeen, sinceYouLookedIndex } from "@/lib/phone/last-seen";
import { headerStatus } from "@/lib/phone/session-status";
import { useSessionsStore } from "@/stores/sessions";
import { usePhoneNav } from "@/composables/phone/use-phone-nav";
import { INBOX, inboxSession } from "@/composables/phone/use-inbox";

/**
 * A session on the phone (`/phone/s/<machine>/<session>`, pushed over the inbox by PhoneStack), in one panel on the
 * window chrome: the head says what it's doing, a plan row when there's a plan, the conversation as the desktop draws
 * it (your messages in bubbles, the agent's words, each run of tool calls as a box of tool rows), and the dock at the
 * bottom — whatever the agent waits on, compact (Allow once and More…), above the composer — which stays above the
 * keyboard. The conversation always clears the dock, whatever its height.
 */
const props = defineProps<{ machineId: string; sessionId: string; ask?: string }>();
const nav = usePhoneNav();
const search = computed(() => ({ ask: props.ask }));
const now = useRelativeTime();
useDeskPresence("phone");

const machineId = computed(() => props.machineId);
const sessionId = computed(() => props.sessionId);

// The session's machine: the live one, or another opened in place (PhoneStack provides it from the address). The live
// machine's row comes from its list; another machine's from the inbox's feed for that machine.
const machine = useMachineTarget();
if (machine.isLive) useSessions({ retentionStatus: "all" });
const sessionsStore = useSessionsStore();
const inbox = inject(INBOX, null);
const session = computed(() => machine.isLive
  ? sessionsStore.sessionById(sessionId.value)
  : inboxSession(inbox, machineId.value, sessionId.value));

const stream = useSessionStream(sessionId);
const { progress } = useSessionProgress(sessionId);
const blocks = computed(() => foldMessages(stream.messages.value));
const items = computed(() => groupTools(blocks.value));

const machineName = computed(() => machine.connection?.name ?? readCredentialsSync()?.homeMachineName ?? "This machine");
const title = computed(() => session.value?.session.title?.trim() || "Session");
const folder = computed(() => (session.value ? sessionFolder(session.value) : null));

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
const markerIndex = computed(() => sinceYouLookedIndex(items.value, seenAt.value));

function rememberSeen(): void {
  markSeen(machineId.value, sessionId.value, Date.now());
}

function onVisibility(): void {
  if (document.visibilityState === "hidden") rememberSeen();
}

const scrollRef = useTemplateRef<HTMLElement>("scroll");
const dockRef = useTemplateRef<HTMLElement>("dock");
let scrolledOnArrival = false;

// The conversation always clears the dock (a docked ask, a five-line message, the keyboard), staying at the bottom.
let dockObserver: ResizeObserver | null = null;
function watchDock(): void {
  const dock = dockRef.value;
  if (!dock || typeof ResizeObserver === "undefined") return;
  dockObserver = new ResizeObserver(() => {
    const el = scrollRef.value;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
    el.style.paddingBottom = `${dock.offsetHeight + 12}px`;
    if (atBottom) el.scrollTop = el.scrollHeight;
  });
  dockObserver.observe(dock);
}

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

/** The tool calls waiting on an ask, so their rows say "Needs you". */
const waitingCalls = computed(() => new Set(asks.value.map((ask) => ask.callId).filter((id): id is string => !!id)));

function putOff(id: string): void {
  later.value = new Set([...later.value, id]);
}

function bringBack(): void {
  later.value = new Set();
}

function onAnswered(text: string): void {
  showToast(text);
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
    showToast("Already answered.");
  }, 1500);
}, { immediate: true });

// The ⋯ menu and what it opens.
const sheet = shallowRef<"menu" | "changes" | "files" | "side" | "terminal" | null>(null);
const { harnesses } = useHarnesses();
const caps = computed(() => harnessCapabilities(session.value?.harnessType, harnesses.value));
const { diffs, base: diffsBase, isLoading: diffsLoading, fetchDiffs } = useDiffs(sessionId);
const { runShellCommand } = useRunShellCommand(sessionId.value);
const { abortSession } = useAbortSession();
const { archiveSession } = useArchiveSession();
const { forkSession } = useForkSession();
const { renameSession } = useRenameSession();
watch(sheet, (open) => {
  if (open === "menu") void fetchDiffs();
});
// A machine without the web app (a node) has no page for the session: it opens from home's Fleet instead.
const machineWebApp = useMachineWebApp();
const homeName = computed(() => readCredentialsSync()?.homeMachineName ?? "your computer");
const computerLink = computed(() => machineWebApp.value
  ? `${machine.connection?.baseUrl ?? window.location.origin}/sessions/${encodeURIComponent(sessionId.value)}`
  : null);

async function onMenu(action: MenuAction): Promise<void> {
  switch (action) {
    case "changes":
      sheet.value = "changes";
      void fetchDiffs();
      break;
    case "terminal":
      // Inside the tap, so iOS brings the keyboard up with the Run a command sheet.
      if (caps.value.supportsShell) holdKeyboard();
      sheet.value = action;
      break;
    case "files":
    case "side":
      sheet.value = action;
      break;
    case "computer": {
      sheet.value = null;
      if (!computerLink.value) break;
      const outcome = await shareLink(`${title.value} on ${machineName.value}`, computerLink.value);
      if (outcome === "copied") showToast("Link copied. Open it on the computer.");
      else if (outcome === "failed") showToast("Couldn't share the link.");
      break;
    }
    case "stop":
      sheet.value = null;
      await abortSession(sessionId.value).catch(() => undefined);
      break;
    case "archive":
      sheet.value = null;
      haptic("success");
      try {
        await archiveSession(sessionId.value);
        showToast("Archived");
        back();
      } catch (failure) {
        showToast(failure instanceof Error ? failure.message : "Couldn't archive it.");
      }
      break;
    case "fork": {
      sheet.value = null;
      const forked = await forkSession(sessionId.value).catch(() => null);
      if (forked) void nav.openSession(machineId.value, forked.session.id);
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

const stepsOpen = shallowRef<{ steps: readonly FoldedStep[]; focus: FoldedStep | null } | null>(null);
const planOpen = shallowRef(false);

function back(): void {
  rememberSeen();
  void nav.back();
}

function openChild(childId: string): void {
  rememberSeen();
  void nav.openSession(machineId.value, childId);
}

/** An ask waiting (docked, or put off) means the session needs you, whatever its last status said. */
const shown = computed(() => (dock.value.kind !== "composer" || dock.value.later > 0) && status.value.tone !== "unreachable"
  ? { tone: "needs-you" as const, state: "Needs you", detail: "" }
  : status.value);

/** What the head says the session is doing, before the machine's name. */
const statusText = computed(() => {
  const value = shown.value;
  switch (value.tone) {
    case "working": return value.detail ? `${value.state} · ${value.detail}` : value.state;
    case "needs-you": return "Needs you";
    case "finished": return lastMessageAt.value ? `Finished ${ago(lastMessageAt.value, now.value)}` : "Finished";
    case "unreachable": return `Can't reach ${machineName.value} · last heard ${value.detail}`;
    default: return value.state;
  }
});

onMounted(() => {
  sessionsStore.setActiveSessionId(sessionId.value);
  document.addEventListener("visibilitychange", onVisibility);
  watchDock();
});

onUnmounted(() => {
  rememberSeen();
  dockObserver?.disconnect();
  document.removeEventListener("visibilitychange", onVisibility);
});
</script>

<template>
  <div
    class="ps"
    data-testid="phone-session"
  >
    <div class="ph-panel ph-panel--full">
      <PhoneSessionHeader
        :title="title"
        :machine-name="machineName"
        :folder="folder"
        :tone="shown.tone"
        :status="statusText"
        @back="back"
        @menu="sheet = 'menu'"
      />

      <main
        ref="scroll"
        class="ph-scroller"
      >
        <div class="ph-convo">
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
          <button
            v-if="stream.hasMore.value"
            type="button"
            class="ph-btn ph-btn--outline ph-btn--sm ps__older"
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
              class="ph-spinner"
              :size="22"
              aria-hidden="true"
            />
          </div>

          <template
            v-for="(block, index) in items"
            :key="block.key"
          >
            <SinceYouLookedMarker
              v-if="index === markerIndex && seenAt"
              :at="seenAt"
            />
            <template v-if="block.kind === 'user'">
              <p
                class="ph-umsg"
                data-testid="phone-user-message"
              >
                {{ block.text }}
              </p>
              <span
                v-if="block.steered || block.images"
                class="ph-umsg-meta"
              >
                <template v-if="block.steered"><CornerDownRight
                  :size="12"
                  aria-hidden="true"
                /> sent into the turn</template>
                <template v-if="block.images"><ImageIcon
                  :size="12"
                  aria-hidden="true"
                /> {{ block.images }} image{{ block.images === 1 ? "" : "s" }}</template>
              </span>
            </template>
            <PhoneMarkdown
              v-else-if="block.kind === 'text'"
              :text="block.text"
            />
            <PhoneToolRun
              v-else-if="block.kind === 'tools'"
              :parts="block.parts"
              :waiting-calls="waitingCalls"
              @open="(step, run) => (stepsOpen = { steps: run, focus: step })"
              @more="(rest) => (stepsOpen = { steps: rest, focus: null })"
              @child="openChild"
            />
            <p
              v-else-if="block.kind === 'question' && !block.pending"
              class="ps__asked"
            >
              Asked · {{ block.question }} <template v-if="block.answer">
                → {{ block.answer }} <Check
                  :size="14"
                  class="ps__answered"
                  aria-hidden="true"
                />
              </template>
            </p>
            <ShellCommandBlock
              v-else-if="block.kind === 'shell'"
              :command="block.view"
              class="ps__shell"
            />
            <p
              v-else-if="block.kind === 'error'"
              class="ph-banner ps__error"
              :class="{ 'ph-banner--bad': !block.limit }"
              role="alert"
            >
              {{ block.text }}
            </p>
          </template>
          <PhoneRetryLine
            :key="sessionId"
            :session-id="sessionId"
          />

          <p
            v-if="shown.tone === 'working'"
            class="ph-working"
            data-testid="phone-working"
          >
            <PhoneGlyph
              kind="working"
              label="Working"
            />
            <span class="ph-working__word">{{ status.state }}</span>
            <span
              v-if="status.detail"
              class="ph-working__t"
            >· {{ status.detail }}</span>
          </p>
          <p
            v-else-if="shown.tone === 'needs-you'"
            class="ph-working ph-working--waiting"
          >
            <PhoneGlyph
              kind="waiting"
              label="Waiting for you"
            />Waiting for you
          </p>
          <p
            v-else-if="status.tone === 'unreachable' && lastStatus.tone === 'working'"
            class="ph-working"
          >
            Working when last heard
          </p>
        </div>
      </main>

      <div
        ref="dock"
        class="ph-dock"
      >
        <Transition name="ph-pill">
          <button
            v-if="dock.later > 0 && dock.kind === 'composer'"
            type="button"
            class="ph-waiting-pill ph-press"
            data-testid="later-pill"
            @click="bringBack"
          >
            <PhoneGlyph kind="waiting" />{{ dock.later }} waiting · Review
          </button>
        </Transition>
        <Transition name="ph-dock">
          <DockedPermission
            v-if="dock.kind === 'permission'"
            :key="dock.ask.id"
            :ask="dock.ask"
            :answer="answerPermission"
            :machine-name="machineName"
            :session-title="title"
            @later="putOff(dock.ask.id)"
            @answered="onAnswered"
          />
          <DockedQuestion
            v-else-if="dock.kind === 'question'"
            :key="dock.pending.requestId"
            :pending="dock.pending"
            :answer="questions.answerQuestion"
            :reject="questions.rejectQuestion"
            :machine-name="machineName"
            :session-title="title"
            @later="putOff(dock.pending.requestId)"
          />
        </Transition>
        <PhoneComposer
          :key="sessionId"
          :session-id="sessionId"
          :machine-id="machineId"
          :machine-name="machineName"
          :reachable="reachability.reachable.value"
          @side="sheet = 'side'"
        />
      </div>
    </div>

    <SessionMenuSheet
      :open="sheet === 'menu'"
      :title="title"
      :machine-name="machineName"
      :changed-files="diffs.length"
      :working="status.tone === 'working'"
      :supports-side="caps.supportsSide"
      :supports-shell="caps.supportsShell"
      :can-fork="session?.capabilities?.canFork ?? true"
      :can-open-on-computer="computerLink !== null"
      @pick="onMenu"
      @rename="onRename"
      @close="sheet = null"
    />
    <ChangesSheet
      :open="sheet === 'changes'"
      :session-id="sessionId"
      :diffs="diffs"
      :base="diffsBase"
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
      title="Run a command"
      :machine-name="machineName"
      :folder="session?.workspaceDirectory ?? null"
      :link="computerLink"
      :home-name="homeName"
      :supports-shell="caps.supportsShell"
      @run="runCommand"
      @close="sheet = null"
    />

    <StepsSheet
      :open="stepsOpen !== null"
      :steps="stepsOpen?.steps ?? []"
      :focus="stepsOpen?.focus ?? null"
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
  position: absolute;
  inset: 0;
  background: var(--ph-chrome);
}

.ps__loading {
  display: grid;
  min-height: 40vh;
  place-items: center;
}

.ps__older {
  align-self: center;
}

.ps__asked {
  margin: 0;
  font-size: var(--ph-t-meta);
  color: var(--muted);
}

.ps__answered {
  display: inline;
  color: var(--running);
}

.ps__shell {
  font-size: 0.8rem;
}

.ps__error {
  width: auto;
  margin: 0;
}
</style>
