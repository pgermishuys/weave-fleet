<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { ExternalLink, RotateCw, TriangleAlert } from "lucide-vue-next";
import { appendDraftText } from "@/composables/use-draft-state";
import { apiFetchOn, apiUrlOn } from "@/lib/api-client";
import { useMachineTarget } from "@/lib/machine-target";
import { runSessionCommand } from "@/lib/session-commands";
import { keepPageState, pageStateMessage, readPageMessage } from "@/lib/page-bridge";
import { pageFrameName, pageThemeMessage, usePageTheme } from "@/lib/page-theme";
import { isChoiceLive, pageChoiceBar } from "@/lib/page-choice";
import { pageAddress, type ShownPage } from "@/lib/server-canvas";
import { useSessionsStore } from "@/stores/sessions";

/**
 * A page the agent wrote, from Fleet's copy of it (`fleet_page_show`). Every show is a new `shownAt`, which
 * reloads the frame, so the user sees each edit without doing anything.
 *
 * The frame's sandbox matches the one Fleet serves the page with: scripts run, but the page gets an opaque
 * origin, so it can't reach Fleet's cookies or API. Its one way out is a few postMessages to this canvas
 * (lib/page-bridge): text for the composer, which the user then sends, and state that Fleet keeps for the page.
 * The page gets Fleet's theme as `--fleet-*` variables (lib/page-theme), from the frame's name and then by message.
 */
const props = defineProps<{
  sessionId: string;
  page: ShownPage & { title: string };
  /** On the phone: no path or reload toolbar, and the choice bar's sentence sits on its own line. */
  compact?: boolean;
}>();

const machine = useMachineTarget();

const sessionsStore = useSessionsStore();

// What the page proposed for a page that waits for a pick; the user sends it from the bar, or presses Change.
const proposal = ref<{ value: string; summary: string } | null>(null);
const sending = ref(false);
const sendError = ref("");
const choice = computed(() => props.page.choice);
const live = computed(() => isChoiceLive(choice.value, sessionsStore.sessionById(props.sessionId)?.activityStatus ?? undefined));
const bar = computed(() => (choice.value ? pageChoiceBar(choice.value, live.value, proposal.value) : null));
const answeredAt = computed(() => {
  const at = bar.value?.at ? new Date(bar.value.at) : null;
  return at && !Number.isNaN(at.getTime()) && bar.value?.kind === "answered"
    ? at.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : "";
});

// A new wait starts clean.
watch(() => choice.value?.callId, () => {
  proposal.value = null;
  sending.value = false;
  sendError.value = "";
});
watch(() => choice.value?.status, () => {
  sending.value = false;
});

async function answerChoice(event: MouseEvent, action: "send" | "skip"): Promise<void> {
  // Only a real click on this bar answers; a page can't script one.
  if (!event.isTrusted || !choice.value || sending.value) return;
  if (action === "send" && !proposal.value) return;
  sending.value = true;
  sendError.value = "";
  try {
    const response = await apiFetchOn(
      machine.connection,
      `/api/sessions/${encodeURIComponent(props.sessionId)}/page-choices/${encodeURIComponent(choice.value.callId)}/${action}`,
      action === "send"
        ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(proposal.value) }
        : { method: "POST" },
    );
    if (response.ok) return;
    const body = (await response.json().catch(() => null)) as { error?: unknown } | null;
    sendError.value = typeof body?.error === "string" ? body.error : "Couldn't send that. Try again.";
  } catch {
    sendError.value = "Couldn't send that. Try again.";
  }
  sending.value = false;
}

const reloads = ref(0);
const frame = ref<HTMLIFrameElement | null>(null);

const address = computed(() => apiUrlOn(machine.connection, pageAddress(props.page)));
const frameKey = computed(() => `${props.page.pageId}:${props.page.shownAt}:${reloads.value}`);
const fileName = computed(() => props.page.source.split(/[\\/]/).pop() || props.page.entry);
const theme = usePageTheme();
// The page reads its frame's name when it loads; a change of theme while it's open goes by message.
const frameName = computed(() => pageFrameName("tab", theme.value));

function sendTheme(): void {
  frame.value?.contentWindow?.postMessage(pageThemeMessage(theme.value), "*");
}

watch(theme, sendTheme);

function openOutside(): void {
  window.open(address.value, "_blank", "noopener");
}

function onMessage(event: MessageEvent): void {
  // Only this canvas's own frame, which the sandbox keeps at an opaque origin wherever it navigates.
  const page = frame.value?.contentWindow;
  if (!page || event.source !== page || event.origin !== "null") return;
  const message = readPageMessage(event.data);
  if (!message) return;

  switch (message.type) {
    case "fleet:page-hello":
      page.postMessage(pageStateMessage(props.page.pageId), "*");
      return;
    case "fleet:page-state":
      keepPageState(props.page.pageId, message.state);
      return;
    case "fleet:page-reply":
      appendDraftText(props.sessionId, message.text);
      runSessionCommand("focus-prompt", props.sessionId);
      return;
    case "fleet:page-choice":
      // Only while the agent waits, and only the first: Change clears it, so a page can't swap it under the user.
      if (live.value && !proposal.value) proposal.value = { value: message.value, summary: message.summary };
      return;
  }
}

onMounted(() => window.addEventListener("message", onMessage));
onBeforeUnmount(() => window.removeEventListener("message", onMessage));
</script>

<template>
  <section
    class="page-canvas"
    :class="{ 'page-canvas--compact': compact }"
  >
    <div
      v-if="!compact"
      class="page-canvas__bar"
    >
      <button
        type="button"
        class="page-canvas__icon"
        title="Reload"
        aria-label="Reload"
        @click="reloads++"
      >
        <RotateCw :size="14" />
      </button>
      <p
        v-if="page.label"
        class="page-canvas__source"
        :title="page.label"
      >
        <span class="page-canvas__file">{{ page.label }}</span>
      </p>
      <p
        v-else
        class="page-canvas__source"
        :title="page.source"
      >
        <span class="page-canvas__file">{{ fileName }}</span>
        <span class="page-canvas__path">{{ page.source }}</span>
      </p>
      <button
        type="button"
        class="page-canvas__icon"
        title="Open in a new tab"
        aria-label="Open in a new tab"
        @click="openOutside"
      >
        <ExternalLink :size="14" />
      </button>
    </div>

    <ul
      v-if="page.warnings.length > 0"
      class="page-canvas__warnings"
      aria-label="Links that won't load"
    >
      <li
        v-for="warning in page.warnings"
        :key="warning"
      >
        <TriangleAlert
          :size="13"
          aria-hidden="true"
        />
        <span>{{ warning }}</span>
      </li>
    </ul>

    <div class="page-canvas__page">
      <iframe
        ref="frame"
        :key="frameKey"
        :src="address"
        :name="frameName"
        :title="page.title"
        class="page-canvas__frame"
        sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox allow-modals allow-downloads"
        referrerpolicy="no-referrer"
        @load="sendTheme"
      />
    </div>

    <div
      v-if="bar"
      class="page-canvas__choice"
      :class="`page-canvas__choice--${bar.kind}`"
      data-testid="page-choice-bar"
    >
      <p class="page-canvas__choice-text">
        {{ bar.text }}<b v-if="bar.strong !== undefined">{{ bar.strong }}</b>{{ bar.after }}
        <span
          v-if="answeredAt"
          class="page-canvas__choice-time"
        >Answered {{ answeredAt }}</span>
        <span
          v-if="sendError"
          class="page-canvas__choice-error"
          role="alert"
        >{{ sendError }}</span>
      </p>
      <template v-if="bar.kind === 'waiting' || bar.kind === 'proposed'">
        <button
          v-if="bar.kind === 'proposed'"
          type="button"
          class="page-canvas__choice-btn"
          :disabled="sending"
          data-testid="page-choice-change"
          @click="proposal = null"
        >
          Change
        </button>
        <button
          v-else
          type="button"
          class="page-canvas__choice-btn"
          :disabled="sending"
          data-testid="page-choice-skip"
          @click="answerChoice($event, 'skip')"
        >
          Skip
        </button>
        <button
          type="button"
          class="page-canvas__choice-btn page-canvas__choice-btn--primary"
          :disabled="sending || bar.kind === 'waiting'"
          data-testid="page-choice-send"
          @click="answerChoice($event, 'send')"
        >
          {{ sending ? "Sending…" : "Send to agent" }}
        </button>
      </template>
    </div>
  </section>
</template>

<style scoped>
.page-canvas {
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  min-height: 0;
}

.page-canvas__bar {
  display: flex;
  align-items: center;
  gap: 2px;
  height: 40px;
  flex-shrink: 0;
  padding: 0 8px;
  border-bottom: 1px solid var(--border);
}

.page-canvas__icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  flex-shrink: 0;
  border-radius: var(--radius-btn);
  color: var(--muted);
  transition: background var(--transition), color var(--transition);
}

.page-canvas__icon:hover {
  background: var(--accent-dim);
  color: var(--text);
}

.page-canvas__source {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex: 1;
  min-width: 0;
  margin: 0 4px;
  font-size: 12px;
  white-space: nowrap;
}

.page-canvas__file {
  flex-shrink: 0;
  color: var(--text);
  font-weight: 500;
}

.page-canvas__path {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.page-canvas__warnings {
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex-shrink: 0;
  margin: 0;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  list-style: none;
  color: var(--text);
  font-size: 12px;
  background: color-mix(in srgb, var(--status-waiting) 8%, transparent);
}

.page-canvas__warnings li {
  display: flex;
  align-items: flex-start;
  gap: 6px;
}

.page-canvas__warnings svg {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--status-waiting);
}

.page-canvas__page {
  position: relative;
  flex: 1;
  min-height: 0;
  background: var(--panel-bg);
}

.page-canvas__choice {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
  padding: 8px 12px;
  border-top: 1px solid var(--border);
  font-size: 12px;
  color: var(--muted);
}

.page-canvas__choice--waiting,
.page-canvas__choice--proposed {
  color: var(--text);
  border-top-color: color-mix(in srgb, var(--status-waiting) 40%, var(--border));
  background: color-mix(in srgb, var(--status-waiting) 6%, transparent);
}

.page-canvas--compact .page-canvas__choice {
  flex-wrap: wrap;
}

.page-canvas--compact .page-canvas__choice-text {
  flex-basis: 100%;
}

.page-canvas--compact .page-canvas__choice-btn {
  min-height: 40px;
}

.page-canvas--compact .page-canvas__choice-btn--primary {
  flex: 1;
}

.page-canvas__choice-text {
  flex: 1;
  min-width: 0;
  margin: 0;
}

.page-canvas__choice-text b {
  color: var(--text);
  font-weight: 600;
}

.page-canvas__choice-time,
.page-canvas__choice-error {
  margin-left: 8px;
  font-size: 11px;
}

.page-canvas__choice-error {
  color: var(--error);
}

.page-canvas__choice-btn {
  flex-shrink: 0;
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  font-size: 12px;
  color: var(--text);
  transition: background var(--transition);
}

.page-canvas__choice-btn:hover:not(:disabled) {
  background: var(--accent-dim);
}

.page-canvas__choice-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.page-canvas__choice-btn--primary {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--accent-contrast, #fff);
}

.page-canvas__choice-btn--primary:hover:not(:disabled) {
  background: var(--accent);
  filter: brightness(1.1);
}

.page-canvas__frame {
  display: block;
  width: 100%;
  height: 100%;
  border: 0;
  /* Pages without a background of their own expect a white one. */
  background: #fff;
  /* As in the browser canvas: keep Fleet's dark scheme off the frame, or a dark page shows its light text on the
     white above. */
  color-scheme: light;
}
</style>
