<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { Check, Copy, Download, Loader2, Share2 } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";
import MarkdownRenderer from "@/components/visual-renderers/MarkdownRenderer.vue";
import type { AccumulatedMessage } from "@/lib/client-types";
import { loadWholeHistory } from "@/lib/export-history";
import {
  DEFAULT_EXPORT_OPTIONS,
  buildMarkdown,
  exportFileName,
  hasReasoning,
  looksLikeKey,
  type ExportOptions,
  type ExportToolMode,
} from "@/lib/export-markdown";

const props = defineProps<{
  open: boolean;
  sessionId: string;
  title: string;
  harness?: string | null;
  model?: string | null;
  /** The session's folder, cut off paths when that option is on. */
  directory?: string | null;
}>();

const emit = defineEmits<{ "update:open": [value: boolean] }>();

const options = ref<ExportOptions>({ ...DEFAULT_EXPORT_OPTIONS });
const view = ref<"source" | "preview">("source");
const messages = ref<AccumulatedMessage[] | null>(null);
const loaded = ref(0);
const loadError = ref<string | null>(null);
const copied = ref(false);
const actionError = ref<string | null>(null);
let loading: AbortController | null = null;
let copiedTimer: ReturnType<typeof setTimeout> | undefined;

const canShare = computed(() => typeof navigator !== "undefined" && typeof navigator.share === "function"
  && typeof window !== "undefined" && window.matchMedia?.("(max-width: 640px)").matches === true);

async function load(): Promise<void> {
  loading?.abort();
  const controller = new AbortController();
  loading = controller;
  messages.value = null;
  loaded.value = 0;
  loadError.value = null;
  try {
    const all = await loadWholeHistory(props.sessionId, (count) => { loaded.value = count; }, controller.signal);
    if (loading === controller) messages.value = all;
  } catch (error) {
    if (controller.signal.aborted) return;
    loadError.value = error instanceof Error ? error.message : "Could not load the conversation.";
  }
}

watch(
  () => [props.open, props.sessionId] as const,
  ([open]) => {
    if (open) {
      options.value = { ...DEFAULT_EXPORT_OPTIONS };
      view.value = "source";
      actionError.value = null;
      void load();
    } else {
      loading?.abort();
    }
  },
  { immediate: true },
);

const thinkingAvailable = computed(() => messages.value !== null && hasReasoning(messages.value));
// Thinking can't be on when the session has none.
const effective = computed<ExportOptions>(() => ({ ...options.value, thinking: options.value.thinking && thinkingAvailable.value }));

const model = computed(() => {
  if (props.model) return props.model;
  const last = [...(messages.value ?? [])].reverse().find((message) => message.role === "assistant" && message.modelID);
  return last?.modelID ?? null;
});

const built = computed(() => {
  if (messages.value === null) return null;
  return buildMarkdown(messages.value, effective.value, {
    title: props.title,
    harness: props.harness,
    model: model.value,
    directory: props.directory,
    exportedAt: new Date(),
  });
});

const markdown = computed(() => built.value?.markdown ?? "");
const lines = computed(() => markdown.value.split("\n").map((text) => ({ text, key: looksLikeKey(text) })));
const sizeLabel = computed(() => `${(new Blob([markdown.value]).size / 1024).toFixed(1)} KB`);
const fileName = computed(() => exportFileName(props.title, props.sessionId));

const toolChoices: { value: ExportToolMode; label: string; hint: string }[] = [
  { value: "line", label: "One line each", hint: "“Run bun test upload”" },
  { value: "output", label: "With output", hint: "Commands, results, diffs" },
  { value: "off", label: "Leave out", hint: "" },
];

async function copy(): Promise<void> {
  actionError.value = null;
  try {
    await navigator.clipboard.writeText(markdown.value);
    copied.value = true;
    clearTimeout(copiedTimer);
    copiedTimer = setTimeout(() => { copied.value = false; }, 2000);
  } catch {
    actionError.value = "Fleet could not copy. Use Save instead.";
  }
}

function save(): void {
  const url = URL.createObjectURL(new Blob([markdown.value], { type: "text/markdown;charset=utf-8" }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName.value;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

async function share(): Promise<void> {
  actionError.value = null;
  try {
    await navigator.share({ title: props.title, text: markdown.value });
  } catch (error) {
    // Closing the share sheet is not a failure.
    if (error instanceof DOMException && error.name === "AbortError") return;
    actionError.value = "Fleet could not open the share sheet. Use Copy instead.";
  }
}
</script>

<template>
  <Dialog
    :open="props.open"
    @update:open="emit('update:open', $event)"
  >
    <DialogContent
      data-testid="export-markdown-dialog"
      class="flex h-[100dvh] max-h-none w-full max-w-none flex-col gap-0 rounded-none p-0 sm:h-[80vh] sm:max-w-5xl sm:rounded-lg"
    >
      <div class="flex min-h-0 flex-1 flex-col overflow-hidden sm:flex-row">
        <div class="flex shrink-0 flex-col gap-3 overflow-y-auto border-b border-border p-4 text-sm sm:w-72 sm:border-b-0 sm:border-r">
          <DialogTitle class="text-base font-semibold">
            Export as Markdown
          </DialogTitle>
          <DialogDescription class="text-xs text-muted">
            A readable copy for an issue, a PR or a doc. It cannot be imported back.
          </DialogDescription>

          <fieldset class="flex flex-col gap-1.5">
            <legend class="mb-1 font-medium">
              Tool calls
            </legend>
            <label
              v-for="choice in toolChoices"
              :key="choice.value"
              class="flex cursor-pointer items-start gap-2"
            >
              <input
                v-model="options.tools"
                type="radio"
                name="export-tools"
                :value="choice.value"
                class="mt-1"
              >
              <span>{{ choice.label }}<small
                v-if="choice.hint"
                class="block text-xs text-muted"
              >{{ choice.hint }}</small></span>
            </label>
          </fieldset>

          <label
            class="flex items-start gap-2"
            :class="thinkingAvailable ? 'cursor-pointer' : 'opacity-60'"
          >
            <input
              v-model="options.thinking"
              type="checkbox"
              data-testid="export-thinking"
              class="mt-1"
              :disabled="!thinkingAvailable"
            >
            <span>Thinking<small class="block text-xs text-muted">{{
              thinkingAvailable ? "The model's notes, as quotes" : "This session has no thinking to include."
            }}</small></span>
          </label>
          <label class="flex cursor-pointer items-start gap-2">
            <input
              v-model="options.shortenPaths"
              type="checkbox"
              class="mt-1"
            >
            <span>Shorten folder paths<small class="block text-xs text-muted">Relative to the session's folder</small></span>
          </label>
          <label class="flex cursor-pointer items-start gap-2">
            <input
              v-model="options.meta"
              type="checkbox"
              class="mt-1"
            >
            <span>Times and model</span>
          </label>

          <p
            v-if="options.tools === 'output'"
            role="note"
            data-testid="export-output-warning"
            class="rounded-md border border-border bg-main-bg p-2 text-xs"
          >
            Tool output can hold keys and tokens. Fleet does not remove them. Read the file before you share it.
          </p>
          <p
            v-if="built && built.keyLines > 0"
            role="note"
            data-testid="export-key-lines"
            class="rounded-md border border-error/40 p-2 text-xs text-error"
          >
            {{ built.keyLines }} {{ built.keyLines === 1 ? "line looks" : "lines look" }} like a key. This is a hint, not a check.
            Read the file before you share it.
          </p>
        </div>

        <div class="flex min-h-0 min-w-0 flex-1 flex-col">
          <div class="flex items-center gap-2 border-b border-border px-3 py-2">
            <div
              class="flex gap-1"
              role="group"
              aria-label="Preview"
            >
              <Button
                variant="filter"
                size="sm"
                :data-active="view === 'source'"
                :aria-pressed="view === 'source'"
                @click="view = 'source'"
              >
                Markdown
              </Button>
              <Button
                variant="filter"
                size="sm"
                :data-active="view === 'preview'"
                :aria-pressed="view === 'preview'"
                @click="view = 'preview'"
              >
                Preview
              </Button>
            </div>
            <span
              v-if="built"
              class="ml-auto pr-8 text-xs text-muted"
              data-testid="export-size"
            >{{ built.turns }} {{ built.turns === 1 ? "turn" : "turns" }} · {{ sizeLabel }}</span>
          </div>

          <div class="min-h-0 flex-1 overflow-auto p-3">
            <p
              v-if="loadError"
              role="alert"
              data-testid="export-error"
              class="text-sm text-error"
            >
              {{ loadError }}
              <Button
                variant="outline"
                size="sm"
                class="ml-2"
                @click="load"
              >
                Try again
              </Button>
            </p>
            <p
              v-else-if="!built"
              class="flex items-center gap-2 text-sm text-muted"
              data-testid="export-loading"
            >
              <Loader2 class="size-4 animate-spin" />
              Loading the conversation… {{ loaded }} messages
            </p>
            <pre
              v-else-if="view === 'source'"
              class="whitespace-pre-wrap break-words font-mono text-xs"
              data-testid="export-source"
            ><template
              v-for="(line, index) in lines"
              :key="index"
            ><mark
              v-if="line.key"
              class="bg-error/20 text-error"
            >{{ line.text }}</mark><template v-else>{{ line.text }}</template>{{ index < lines.length - 1 ? "\n" : "" }}</template></pre>
            <MarkdownRenderer
              v-else
              :content="markdown"
              data-testid="export-rendered"
            />
          </div>

          <div class="flex flex-wrap items-center gap-2 border-t border-border px-3 py-2">
            <span class="truncate font-mono text-xs text-muted">{{ fileName }}</span>
            <span
              v-if="actionError"
              role="alert"
              class="text-xs text-error"
            >{{ actionError }}</span>
            <span class="flex-1" />
            <Button
              variant="outline"
              size="sm"
              data-testid="export-save"
              :disabled="!built"
              @click="save"
            >
              <Download />
              Save .md
            </Button>
            <Button
              :variant="canShare ? 'outline' : 'default'"
              size="sm"
              data-testid="export-copy"
              :disabled="!built"
              @click="copy"
            >
              <Check v-if="copied" />
              <Copy v-else />
              {{ copied ? "Copied" : "Copy" }}
            </Button>
            <Button
              v-if="canShare"
              size="sm"
              data-testid="export-share"
              :disabled="!built"
              @click="share"
            >
              <Share2 />
              Share…
            </Button>
          </div>
        </div>
      </div>
    </DialogContent>
  </Dialog>
</template>
