<script setup lang="ts">
import { computed } from "vue";
import MarkdownIt from "markdown-it";
import hljs from "highlight.js";
import ToolCard from "@/components/session/ToolCard.vue";

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

const props = defineProps<{
  author: string;
  role: "user" | "assistant";
  timestamp: string;
  body: string;
  tools?: ToolCardItem[];
  showIdentity: boolean;
  clusterPosition: "single" | "first" | "middle" | "last";
}>();

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

const markdownRenderer = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
  highlight(code, language) {
    if (language && hljs.getLanguage(language)) {
      return `<pre class="hljs"><code>${hljs.highlight(code, {
        language,
        ignoreIllegals: true,
      }).value}</code></pre>`;
    }

    return `<pre class="hljs"><code>${escapeHtml(code)}</code></pre>`;
  },
});

const bodyHtml = computed(() => markdownRenderer.render(props.body));
const displayAuthor = computed(() => {
  const author = props.author.trim();
  if (!author) {
    return author;
  }

  return author.charAt(0).toUpperCase() + author.slice(1);
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
      v-if="showIdentity || timestamp"
      class="msg-header"
    >
      <span
        v-if="showIdentity && role !== 'user'"
        class="msg-author"
        data-testid="message-sender-name"
      >
        {{ displayAuthor }}
      </span>
      <span
        v-if="timestamp"
        class="msg-timestamp"
      >
        {{ timestamp }}
      </span>
    </div>

    <div class="msg-body">
      <div
        v-if="body"
        class="msg-body__content"
        v-html="bodyHtml"
      />

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
    </div>
  </article>
</template>

<style scoped>
@import "highlight.js/styles/github-dark.css";

.message {
  width: var(--activity-bubble-width, 100%);
  box-sizing: border-box;
  padding: 10px 12px 12px;
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
  margin-bottom: 6px;
}

.message--user .msg-header {
  justify-content: flex-end;
}

.msg-author {
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  letter-spacing: 0.01em;
}

.msg-timestamp {
  color: var(--muted);
  font-size: 12px;
}

.msg-body {
  line-height: 1.6;
  color: #d4d4d8;
}

.message--user .msg-body {
  color: #f4f4f5;
}

.message--identity-hidden .msg-header {
  margin-bottom: 2px;
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
  margin: 0 0 12px;
}

.msg-body__content :deep(ul),
.msg-body__content :deep(ol) {
  padding-left: 18px;
}

.msg-body__content :deep(li + li) {
  margin-top: 4px;
}

.msg-body__content :deep(a) {
  color: #818cf8;
}

.msg-body__content :deep(code:not(pre code)) {
  padding: 0.12rem 0.35rem;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.06);
  color: #f4f4f5;
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 0.92em;
}

.msg-body__content :deep(pre) {
  overflow-x: auto;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: var(--radius-card);
}

.msg-body__content :deep(pre code) {
  display: block;
  padding: 12px 14px;
  font-size: 12px;
}

.msg-body__content :deep(blockquote) {
  padding-left: 12px;
  border-left: 2px solid rgba(255, 255, 255, 0.12);
  color: #c4c4cc;
}
</style>
