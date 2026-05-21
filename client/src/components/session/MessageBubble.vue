<script setup lang="ts">
import { computed, ref } from "vue";
import { X } from "lucide-vue-next";
import ToolCard from "@/components/session/ToolCard.vue";
import QuestionCard from "@/components/session/QuestionCard.vue";
import type { AccumulatedToolPart } from "@/lib/api-types";
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

const lightboxUrl = ref<string | null>(null);

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
    <div
      v-if="showIdentity || createdAt"
      class="msg-header"
    >
      <span
        v-if="showIdentity"
        class="msg-author"
        data-testid="message-sender-name"
      >
        {{ displayAuthor }}
      </span>
      <span
        v-if="showIdentity && displayModelId"
        class="msg-model"
        data-testid="message-model-id"
      >
        · {{ displayModelId }}
      </span>
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
  </article>
</template>

<style scoped>
@import "highlight.js/styles/github-dark.css";

.message {
  width: var(--activity-bubble-width, 100%);
  box-sizing: border-box;
  padding: 8px 10px 10px;
  border: 1px solid transparent;
  border-radius: 18px;
  background: transparent;
  box-shadow: 0 1px 0 rgba(255, 255, 255, 0.02);
  transition: background-color 140ms ease, border-color 140ms ease, box-shadow 140ms ease;
}

.message[data-role="user"] {
  border-color: rgba(161, 161, 170, 0.24);
  background: linear-gradient(180deg, rgba(255, 255, 255, 0.05), rgba(255, 255, 255, 0.025));
}

.message[data-role="assistant"] {
  border-color: rgba(99, 102, 241, 0.18);
  background: rgba(99, 102, 241, 0.06);
}

.message--user {
  border-top-right-radius: 6px;
}

.message--assistant {
  border-top-left-radius: 6px;
}

.message--first.message--user,
.message--middle.message--user {
  border-bottom-right-radius: 10px;
}

.message--last.message--user,
.message--middle.message--user {
  border-top-right-radius: 10px;
}

.message--first.message--assistant,
.message--middle.message--assistant {
  border-bottom-left-radius: 10px;
}

.message--last.message--assistant,
.message--middle.message--assistant {
  border-top-left-radius: 10px;
}

.msg-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
  width: 100%;
}

.message--user .msg-header {
  justify-content: flex-start;
  margin-bottom: 4px;
}

.msg-author {
  color: var(--text);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.01em;
}

.msg-model {
  color: var(--muted);
  font-size: 10px;
  font-weight: 500;
}

.msg-timestamp {
  color: var(--muted);
  font-size: 10px;
  margin-left: auto;
}

.msg-body {
  font-size: 12px;
  line-height: 1.45;
  color: #d4d4d8;
}

.message--user .msg-body {
  color: #f4f4f5;
}

.message--identity-hidden .msg-header {
  margin-bottom: 1px;
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

.msg-body__content :deep(ul),
.msg-body__content :deep(ol) {
  padding-left: 16px;
}

.msg-body__content :deep(li + li) {
  margin-top: 2px;
}

.msg-body__content :deep(a) {
  color: #818cf8;
}

.msg-body__content :deep(code:not(pre code)) {
  padding: 0.12rem 0.35rem;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.06);
  color: #f4f4f5;
  font-family: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 0.88em;
}

.msg-body__content :deep(pre) {
  overflow-x: auto;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: var(--radius-card);
}

.msg-body__content :deep(pre code) {
  display: block;
  padding: 10px 12px;
  font-size: 10px;
}

.msg-body__content :deep(blockquote) {
  padding-left: 10px;
  border-left: 2px solid rgba(255, 255, 255, 0.12);
  color: #c4c4cc;
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
  border: 1px solid rgba(255, 255, 255, 0.12);
  text-align: left;
}

.msg-body__content :deep(th) {
  font-weight: 600;
  background: rgba(255, 255, 255, 0.06);
}

.msg-body__content :deep(tr:nth-child(even)) {
  background: rgba(255, 255, 255, 0.03);
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
  border-radius: 6px;
  background: transparent;
  cursor: pointer;
  overflow: hidden;
  transition: border-color 140ms ease;
}

.msg-image-thumb:hover {
  border-color: rgba(255, 255, 255, 0.3);
}

.msg-image-thumb__img {
  display: block;
  max-width: 180px;
  max-height: 120px;
  object-fit: cover;
  border-radius: 5px;
}
</style>
