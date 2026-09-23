<script setup lang="ts">
import { computed, ref } from "vue";
import { X, User, Bot, Copy } from "lucide-vue-next";
import ToolCard from "@/components/session/ToolCard.vue";
import AgentTaskRow from "@/components/session/AgentTaskRow.vue";
import type { ToolCardDelegation } from "@/components/session/activity-stream-tool-card";
import QuestionCard from "@/components/session/QuestionCard.vue";
import type { AccumulatedToolPart } from "@/lib/client-types";
import type { VisualPayload } from "@/lib/visual-payload";
import { useQuestionAnswer } from "@/composables/use-question-answer";
import { useRelativeTime } from "@/composables/use-relative-time";
import { formatRelativeTime, formatAbsoluteTimestamp } from "@/lib/format-utils";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";

interface ToolCardDiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

interface ToolCardItem {
  id: string;
  title: string;
  kind?: string;
  status?: string;
  summary?: string;
  output?: string;
  diffLines?: ToolCardDiffLine[];
  initiallyCollapsed?: boolean;
  preview?: string;
  isPatternTool?: boolean;
  canvasId?: string;
  delegation?: ToolCardDelegation;
}

interface ImageAttachmentDisplay {
  url: string;
  filename: string;
}

const props = defineProps<{
  author: string;
  /** The friendly name of the model that wrote this, resolved by the stream; empty when it names none. */
  modelName?: string;
  role: "user" | "assistant";
  createdAt?: number;
  body: string;
  images?: ImageAttachmentDisplay[];
  tools?: ToolCardItem[];
  questionParts?: AccumulatedToolPart[];
  sessionId?: string;
  showIdentity: boolean;
  clusterPosition: "single" | "first" | "middle" | "last";
}>();

const emit = defineEmits<{
  "expand-visual": [payload: VisualPayload];
  "show-canvas": [canvasId: string];
}>();

const lightboxUrl = ref<string | null>(null);
const copied = ref(false);

const now = useRelativeTime();
const relativeTime = computed(() => props.createdAt ? formatRelativeTime(props.createdAt, now.value) : "");
const absoluteTime = computed(() => formatAbsoluteTimestamp(props.createdAt));
// Only the agent's replies say which model wrote them; yours are yours.
const showModel = computed(() => props.role === "assistant" && Boolean(props.modelName));

// ── Question answer handler (only created when there are question parts) ──
const questionAnswer = props.sessionId ? useQuestionAnswer(props.sessionId) : null;

function makeSubmitHandler(callId: string) {
  return async (answers: string[][]) => {
    if (!questionAnswer) throw new Error("No session ID");
    await questionAnswer.answerQuestion(callId, answers);
  };
}

function makeDismissHandler(callId: string) {
  return async () => {
    if (!questionAnswer) throw new Error("No session ID");
    await questionAnswer.rejectQuestion(callId);
  };
}

const markdownRenderer = sharedMarkdownRenderer();

const bodyHtml = computed(() => markdownRenderer.render(props.body));

function copyMessage() {
  navigator.clipboard.writeText(props.body);
  copied.value = true;
  setTimeout(() => {
    copied.value = false;
  }, 1500);
}

function handleExpandVisual(payload: VisualPayload): void {
  emit("expand-visual", payload);
}
</script>

<template>
  <article
    class="message"
    :class="[
      `message--${clusterPosition}`,
      `message--${role}`,
      { 'message--identity-hidden': !showIdentity },
    ]"
    data-testid="message-item"
    :data-role="role"
  >
    <button
      type="button"
      class="msg-copy-btn"
      :title="copied ? 'Copied' : 'Copy message'"
      @click="copyMessage"
    >
      <Copy
        v-if="!copied"
        class="msg-copy-btn__icon"
        aria-hidden="true"
      />
      <span
        v-else
        class="msg-copy-btn__text"
      >Copied</span>
    </button>
    
    <div class="msg-layout">
      <div class="msg-icon">
        <User
          v-if="role === 'user'"
          class="msg-icon__svg"
          aria-hidden="true"
        />
        <Bot
          v-else
          class="msg-icon__svg"
          aria-hidden="true"
        />
      </div>
      
      <div class="msg-content">
        <div class="msg-body">
          <!-- eslint-disable-next-line vue/no-v-html -->
          <div
            v-if="body"
            class="msg-body__content md-content"
            v-html="bodyHtml"
          />

          <div
            v-if="images && images.length > 0"
            class="msg-images"
          >
            <button
              v-for="(img, idx) in images"
              :key="idx"
              type="button"
              class="msg-image-thumb"
              :title="img.filename"
              @click="lightboxUrl = img.url"
            >
              <img
                :src="img.url"
                :alt="img.filename"
                class="msg-image-thumb__img"
              >
            </button>
          </div>

          <Teleport to="body">
            <div
              v-if="lightboxUrl"
              class="lightbox-overlay"
              @click="lightboxUrl = null"
            >
              <img
                :src="lightboxUrl"
                alt="Image preview"
                class="lightbox-image"
                @click.stop
              >
              <button
                type="button"
                class="lightbox-close"
                @click="lightboxUrl = null"
              >
                <X
                  class="lightbox-close__icon"
                  aria-hidden="true"
                />
              </button>
            </div>
          </Teleport>

          <div
            v-if="tools && tools.length > 0"
            class="msg-tools"
          >
            <template
              v-for="tool in tools"
              :key="tool.id"
            >
              <AgentTaskRow
                v-if="tool.delegation"
                :delegation="tool.delegation"
              />
              <ToolCard
                v-else
                :id="tool.id"
                :title="tool.title"
                :kind="tool.kind"
                :status="tool.status"
                :summary="tool.summary"
                :output="tool.output"
                :diff-lines="tool.diffLines"
                :initially-collapsed="tool.initiallyCollapsed"
                :preview="tool.preview"
                :is-pattern-tool="tool.isPatternTool"
                :canvas-id="tool.canvasId"
                @expand-visual="handleExpandVisual"
                @show-canvas="emit('show-canvas', $event)"
              />
            </template>
          </div>

          <QuestionCard
            v-for="qpart in questionParts ?? []"
            :key="qpart.partId"
            :part="qpart"
            :session-id="sessionId ?? ''"
            :on-submit="makeSubmitHandler(qpart.callId)"
            :on-dismiss="makeDismissHandler(qpart.callId)"
          />
        </div>
      </div>

      <TooltipProvider v-if="createdAt">
        <Tooltip>
          <TooltipTrigger as-child>
            <span class="msg-meta">
              <template v-if="showModel">
                <span
                  class="msg-meta__swatch"
                  aria-hidden="true"
                />
                <span class="msg-meta__model">{{ modelName }}</span>
                <span class="msg-meta__sep">·</span>
              </template>
              {{ relativeTime }}
            </span>
          </TooltipTrigger>
          <TooltipContent side="top">
            {{ absoluteTime }}
          </TooltipContent>
        </Tooltip>
      </TooltipProvider>
    </div>
  </article>
</template>

<style scoped>
/* A conversation, not a log: no dividers or hover bars. Your messages are
   bubbles on the right; the agent's text flows in the reading column. */
.message {
  width: var(--activity-bubble-width, 100%);
  box-sizing: border-box;
  position: relative;
  padding: 2px 0;
  background: transparent;
}

.message--user {
  width: auto;
  max-width: 82%;
  align-self: flex-end;
}

.msg-layout {
  display: flex;
  flex-direction: column;
  gap: 4px;
  align-items: flex-start;
}

.message--user .msg-layout {
  align-items: flex-end;
}

.msg-icon {
  display: none;
}

.msg-content {
  width: 100%;
  min-width: 0;
}

/* Cap at the bubble's width: as a cross-axis item under align-items: flex-end
   it would otherwise size to the widest unwrapped <pre> line and spill left. */
.message--user .msg-content {
  width: auto;
  max-width: 100%;
}

/* Which model answered, and when. Out of flow so hover adds no space, and on a
   surface like every other hover overlay in the stream — bare text here lands on
   the words of the reply. It sits in the 20px between message groups. */
.msg-meta {
  position: absolute;
  bottom: -20px;
  left: 0;
  z-index: 1;
  display: inline-flex;
  box-sizing: border-box;
  align-items: center;
  gap: 7px;
  height: 20px;
  padding: 0 9px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  box-shadow: var(--sheet-shadow);
  color: var(--muted);
  font-size: 11px;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
  cursor: default;
  opacity: 0;
  transition: opacity var(--transition);
}

.msg-meta__swatch {
  flex-shrink: 0;
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--accent) 75%, transparent);
}

.msg-meta__model {
  color: color-mix(in srgb, var(--text) 72%, transparent);
}

.msg-meta__sep {
  color: color-mix(in srgb, var(--muted) 65%, transparent);
}

/* Your own messages sit clear of their text already: outside the bubble, no surface. */
.message--user .msg-meta {
  right: calc(100% + 10px);
  bottom: 6px;
  left: auto;
  height: auto;
  padding: 0;
  border: 0;
  background: transparent;
  box-shadow: none;
}

.message:hover .msg-meta,
.message:focus-within .msg-meta {
  opacity: 1;
}

.msg-body {
  font-size: 14px;
  line-height: 1.65;
  color: var(--text);
}

.message--user .msg-body {
  padding: 10px 14px;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-panel) + 2px) calc(var(--radius-panel) + 2px) 4px calc(var(--radius-panel) + 2px);
  background: color-mix(in srgb, var(--text) 5%, transparent);
  line-height: 1.55;
}

.message--user .msg-body__content {
  text-align: left;
}

.msg-tools {
  display: flex;
  flex-direction: column;
  margin: 10px 0 4px;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--text) 3%, transparent);
}

.msg-images {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 6px;
}

.msg-image-thumb {
  display: block;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  cursor: pointer;
  overflow: hidden;
  transition: border-color var(--transition);
}

.msg-image-thumb:hover {
  border-color: color-mix(in srgb, var(--text) 30%, transparent);
}

.msg-image-thumb__img {
  display: block;
  max-width: 180px;
  max-height: 120px;
  object-fit: cover;
}

.msg-copy-btn {
  position: absolute;
  top: 0;
  right: 0;
  width: 26px;
  height: 26px;
  display: flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--muted);
  cursor: pointer;
  opacity: 0;
  transition: opacity var(--transition), color var(--transition);
  padding: 0;
}

.message--user .msg-copy-btn {
  right: auto;
  left: -34px;
  top: 6px;
}

.message:hover .msg-copy-btn,
.msg-copy-btn:focus-visible {
  opacity: 1;
}

.msg-copy-btn:hover {
  color: var(--text);
}

.msg-copy-btn__icon {
  width: 13px;
  height: 13px;
}

.msg-copy-btn__text {
  font-size: 9px;
  font-weight: 500;
  white-space: nowrap;
}

@media (prefers-reduced-motion: reduce) {
  .msg-meta,
  .msg-copy-btn {
    transition: none;
  }
}
</style>
