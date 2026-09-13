<script setup lang="ts">
import { computed, ref } from "vue";
import { X, User, Bot, Copy } from "lucide-vue-next";
import ToolCard from "@/components/session/ToolCard.vue";
import QuestionCard from "@/components/session/QuestionCard.vue";
import type { AccumulatedToolPart } from "@/lib/client-types";
import type { VisualPayload } from "@/lib/visual-payload";
import { useQuestionAnswer } from "@/composables/use-question-answer";
import { useRelativeTime } from "@/composables/use-relative-time";
import { formatRelativeTime, formatAbsoluteTimestamp } from "@/lib/format-utils";
import { createMarkdownRenderer } from "@/lib/markdown-renderer";
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
}

interface ImageAttachmentDisplay {
  url: string;
  filename: string;
}

const props = defineProps<{
  author: string;
  modelId?: string;
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

const markdownRenderer = createMarkdownRenderer();

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
            <ToolCard
              v-for="tool in tools"
              :id="tool.id"
              :key="tool.id"
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
            <span class="msg-timestamp">{{ relativeTime }}</span>
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

.message--user .msg-content {
  width: auto;
}

/* Out of flow so hidden timestamps don't add space between messages. */
.msg-timestamp {
  position: absolute;
  top: 5px;
  right: 34px;
  color: var(--muted);
  font-size: 11px;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
  cursor: default;
  opacity: 0;
  transition: opacity var(--transition);
}

.message--user .msg-timestamp {
  top: auto;
  right: calc(100% + 10px);
  bottom: 6px;
}

.message:hover .msg-timestamp,
.message:focus-within .msg-timestamp {
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
</style>
