<script setup lang="ts">
import { computed, ref } from "vue";
import { X, User, Bot, Copy } from "lucide-vue-next";
import ToolCard from "@/components/session/ToolCard.vue";
import QuestionCard from "@/components/session/QuestionCard.vue";
import type { AccumulatedToolPart } from "@/lib/api-types";
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
const displayAuthor = computed(() => {
  const author = props.author.trim();
  if (!author) {
    return author;
  }

  return author.charAt(0).toUpperCase() + author.slice(1);
});

const displayModelId = computed(() => {
  return props.role === "assistant" ? props.modelId?.trim() ?? "" : "";
});

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
            class="msg-body__content"
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

          <ToolCard
            v-for="tool in tools ?? []"
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
            @expand-visual="handleExpandVisual"
          />

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
@import "highlight.js/styles/github.css";

.message {
  width: var(--activity-bubble-width, 100%);
  box-sizing: border-box;
  padding: 12px;
  border-bottom: 1px solid var(--border);
  border-left: 3px solid transparent;
  position: relative;
  background: transparent;
  transition: background var(--transition), border-left-color var(--transition);
}

.message:hover {
  border-left-color: var(--indigo);
  background: rgba(91, 110, 199, 0.03);
}

.msg-layout {
  display: flex;
  gap: 12px;
  align-items: flex-start;
}

.msg-icon {
  flex-shrink: 0;
  width: 20px;
  height: 20px;
  display: flex;
  align-items: center;
  justify-content: center;
  margin-top: 2px;
}

.msg-icon__svg {
  width: 16px;
  height: 16px;
  color: var(--muted);
}

.message--user .msg-icon__svg {
  color: var(--text);
}

.message--assistant .msg-icon__svg {
  color: var(--indigo);
}

.msg-content {
  flex: 1;
  min-width: 0;
}

.msg-timestamp {
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
  flex-shrink: 0;
  align-self: flex-start;
  margin-top: 3px;
  cursor: default;
}

.msg-body {
  font-size: 14px;
  line-height: 1.6;
  color: var(--text);
}

.message--user .msg-body__content {
  text-align: left;
}

.msg-body__content :deep(*) {
  max-width: 100%;
}

.msg-body__content :deep(p),
.msg-body__content :deep(ul),
.msg-body__content :deep(ol),
.msg-body__content :deep(pre),
.msg-body__content :deep(blockquote) {
  margin: 0 0 8px;
}

.msg-body__content :deep(*:last-child) {
  margin-bottom: 0;
}

.msg-body__content :deep(ul) {
  padding-left: 16px;
  list-style-type: disc;
}

.msg-body__content :deep(ol) {
  padding-left: 16px;
  list-style-type: decimal;
}

.msg-body__content :deep(li + li) {
  margin-top: 2px;
}

.msg-body__content :deep(a) {
  color: var(--indigo);
}

.msg-body__content :deep(code:not(pre code)) {
  padding: 0.12rem 0.35rem;
  border-radius: 0;
  background: var(--bg, rgba(0, 0, 0, 0.04));
  color: var(--text);
  font-family: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 0.88em;
}

.msg-body__content :deep(pre) {
  overflow-x: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
}

.msg-body__content :deep(pre code) {
  display: block;
  padding: 10px 12px;
  font-size: 10px;
}

.msg-body__content :deep(blockquote) {
  padding-left: 10px;
  border-left: 2px solid var(--border);
  color: var(--muted);
}

.msg-body__content :deep(table) {
  width: auto;
  margin: 0 0 8px;
  border-collapse: collapse;
  font-size: 0.92em;
}

.msg-body__content :deep(th),
.msg-body__content :deep(td) {
  padding: 4px 10px;
  border: 1px solid var(--border);
  text-align: left;
}

.msg-body__content :deep(th) {
  font-weight: 600;
  background: var(--bg, rgba(0, 0, 0, 0.04));
}

.msg-body__content :deep(tr:nth-child(even)) {
  background: var(--bg, rgba(0, 0, 0, 0.02));
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
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 0;
  background: transparent;
  cursor: pointer;
  overflow: hidden;
  transition: border-color var(--transition);
}

.msg-image-thumb:hover {
  border-color: rgba(255, 255, 255, 0.3);
}

.msg-image-thumb__img {
  display: block;
  max-width: 180px;
  max-height: 120px;
  object-fit: cover;
  border-radius: 0;
}

.msg-copy-btn {
  position: absolute;
  top: 8px;
  right: 8px;
  width: 26px;
  height: 26px;
  display: flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--border);
  border-radius: 0;
  background: var(--surface, #fff);
  color: var(--muted);
  cursor: pointer;
  opacity: 0;
  transition: opacity var(--transition);
  padding: 0;
}

.message:hover .msg-copy-btn {
  opacity: 1;
}

.msg-copy-btn:hover {
  background: var(--bg, rgba(0, 0, 0, 0.04));
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
