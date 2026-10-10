<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import ModDialogShell from "@/components/mods/review/ModDialogShell.vue";
import type { ModFile } from "@/lib/mods/kept";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";

/**
 * A mod's files, read-only, highlighted the way the conversation highlights code. Used for a draft and for a kept
 * version; the caller says how to load them.
 */
const props = defineProps<{
  open: boolean;
  /** E.g. "test-chips · draft" or "test-chips · v2". */
  title: string;
  load: () => Promise<ModFile[]>;
  /** On the phone it's a sheet. */
  phone?: boolean;
}>();

defineEmits<{ "update:open": [value: boolean] }>();

const files = shallowRef<ModFile[] | null>(null);
const error = shallowRef<string | null>(null);
const selected = shallowRef<string | null>(null);
let loadId = 0;

/** The manifest and the hooks file first: they're what a reader wants to see. */
function rank(path: string): number {
  if (path === "mod.json") return 0;
  if (/^hooks\.[cm]?[jt]s$/.test(path)) return 1;
  return 2;
}

const ordered = computed(() =>
  [...(files.value ?? [])].sort((a, b) => rank(a.path) - rank(b.path) || a.path.localeCompare(b.path)),
);
const current = computed(() => ordered.value.find((file) => file.path === selected.value) ?? ordered.value[0] ?? null);

const LANGUAGES: Record<string, string> = { ts: "ts", mts: "ts", js: "js", mjs: "js", json: "json", html: "html", css: "css" };

const html = computed(() => {
  const file = current.value;
  if (!file) return "";
  // A fence longer than any run of backticks in the source, so the source can't end it early.
  const longest = Math.max(0, ...(file.content.match(/`+/g) ?? []).map((run) => run.length));
  const fence = "`".repeat(Math.max(3, longest + 1));
  const language = LANGUAGES[file.path.split(".").pop()?.toLowerCase() ?? ""] ?? "";
  return sharedMarkdownRenderer().render(`${fence}${language}\n${file.content}\n${fence}`, {});
});

watch(() => props.open, async (isOpen) => {
  if (!isOpen) return;
  const mine = ++loadId;
  files.value = null;
  error.value = null;
  selected.value = null;
  try {
    const loaded = await props.load();
    if (mine === loadId) files.value = loaded;
  } catch (caught) {
    if (mine === loadId) error.value = caught instanceof Error ? caught.message : "Couldn't load the code.";
  }
}, { immediate: true });
</script>

<template>
  <ModDialogShell
    :open="open"
    :label="title"
    description="Read-only. This is exactly what runs."
    :phone="phone"
    content-class="mod-code-dialog max-w-3xl"
    testid="mod-code-dialog"
    @update:open="(value) => $emit('update:open', value)"
  >
    <div
      v-if="!files && !error"
      class="flex items-center gap-2 text-sm text-muted"
      data-testid="mod-code-loading"
    >
      <LoaderCircle
        class="h-4 w-4 animate-spin"
        aria-hidden="true"
      />
      Loading the code…
    </div>

    <p
      v-else-if="error"
      class="text-sm text-error"
      role="alert"
      data-testid="mod-code-error"
    >
      {{ error }}
    </p>

    <p
      v-else-if="ordered.length === 0"
      class="text-sm text-muted"
      data-testid="mod-code-empty"
    >
      This mod has no files yet.
    </p>

    <template v-else>
      <div
        class="mod-code-dialog__tabs"
        role="tablist"
        aria-label="Files"
      >
        <button
          v-for="file in ordered"
          :key="file.path"
          type="button"
          role="tab"
          class="mod-code-dialog__tab"
          :aria-selected="file.path === current?.path"
          data-testid="mod-code-file"
          @click="selected = file.path"
        >
          {{ file.path }}
        </button>
      </div>
      <!-- eslint-disable vue/no-v-html -->
      <div
        class="mod-code-dialog__body md-content"
        role="tabpanel"
        data-testid="mod-code-body"
        v-html="html"
      />
      <!-- eslint-enable vue/no-v-html -->
    </template>
  </ModDialogShell>
</template>

<style scoped>
.mod-code-dialog__tabs {
  display: flex;
  gap: 4px;
  overflow-x: auto;
  flex-shrink: 0;
  padding-bottom: 2px;
}

.mod-code-dialog__tab {
  flex-shrink: 0;
  padding: 3px 10px;
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  min-height: 28px;
}

.mod-code-dialog__tab:hover {
  color: var(--text);
}

.mod-code-dialog__tab[aria-selected="true"] {
  border-color: color-mix(in srgb, var(--accent) 55%, transparent);
  color: var(--accent);
}

.mod-code-dialog__tab:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.mod-code-dialog__body {
  min-width: 0;
  margin-top: 8px;
  overflow-x: auto;
}

.mod-code-dialog__body :deep(pre) {
  margin: 0;
}

.mod-code-dialog__body :deep(code) {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
