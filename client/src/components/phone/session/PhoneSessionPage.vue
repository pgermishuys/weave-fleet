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
import PlanSheet from "@/components/phone/session/PlanSheet.vue";
import SinceYouLookedMarker from "@/components/phone/session/SinceYouLookedMarker.vue";
import StepsSheet from "@/components/phone/session/StepsSheet.vue";
import { useDeskPresence } from "@/composables/use-desk-presence";
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

const status = computed(() => headerStatus({
  sessionStatus: session.value?.sessionStatus ?? null,
  streamStatus: stream.isLoading.value ? null : stream.sessionStatus.value,
  turnStartedAt: turnStartedAt.value,
  updatedAt: updatedAt.value,
  lastMessageAt: lastMessageAt.value,
  hasMessages: stream.messages.value.length > 0,
  unreachableSince: null,
  now: now.value,
}));

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
      @menu="() => undefined"
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
    </main>

    <slot name="dock" />

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
