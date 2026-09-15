<script setup lang="ts">
import { closeBrackets, closeBracketsKeymap } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { bracketMatching, indentOnInput } from "@codemirror/language";
import { Compartment, EditorState } from "@codemirror/state";
import { EditorView, highlightActiveLine, keymap, lineNumbers } from "@codemirror/view";
import { onBeforeUnmount, onMounted, useTemplateRef, watch } from "vue";
import { loadLanguage } from "@/lib/code-editor/languages";
import { fleetTheme } from "@/lib/code-editor/theme";

/** A small JSON-with-comments editor for a profile's config. Mod-S saves. */
const props = defineProps<{
  label: string;
}>();

const emit = defineEmits<{
  save: [];
}>();

const content = defineModel<string>({ required: true });

const host = useTemplateRef<HTMLDivElement>("host");
const language = new Compartment();
let view: EditorView | null = null;

onMounted(async () => {
  if (!host.value) return;
  view = new EditorView({
    parent: host.value,
    state: EditorState.create({
      doc: content.value,
      extensions: [
        lineNumbers(),
        history(),
        indentOnInput(),
        bracketMatching(),
        closeBrackets(),
        highlightActiveLine(),
        fleetTheme,
        language.of([]),
        EditorView.contentAttributes.of({ "aria-label": props.label }),
        keymap.of([
          { key: "Mod-s", preventDefault: true, run: () => (emit("save"), true) },
          ...closeBracketsKeymap,
          ...defaultKeymap,
          ...historyKeymap,
          indentWithTab,
        ]),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) content.value = update.state.doc.toString();
        }),
      ],
    }),
  });

  const json = await loadLanguage("opencode.jsonc");
  if (json && view) view.dispatch({ effects: language.reconfigure(json) });
});

// Content set from outside (Duplicate, a reset) replaces the document.
watch(content, (next) => {
  if (view && next !== view.state.doc.toString()) {
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: next } });
  }
});

onBeforeUnmount(() => {
  view?.destroy();
  view = null;
});
</script>

<template>
  <div
    ref="host"
    class="profile-config-editor"
    data-testid="profile-config-editor"
  />
</template>

<style scoped>
.profile-config-editor {
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  font-size: 12px;
}

.profile-config-editor:focus-within {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.profile-config-editor :deep(.cm-editor) {
  max-height: 360px;
}

.profile-config-editor :deep(.cm-scroller) {
  min-height: 160px;
  font-family: var(--font-mono-stack);
}
</style>
