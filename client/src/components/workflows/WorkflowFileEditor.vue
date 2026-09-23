<script setup lang="ts">
import { closeBrackets, closeBracketsKeymap } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { indentOnInput } from "@codemirror/language";
import { Compartment, EditorState, RangeSetBuilder, StateEffect, StateField } from "@codemirror/state";
import { Decoration, type DecorationSet, EditorView, highlightActiveLine, keymap, lineNumbers } from "@codemirror/view";
import { onBeforeUnmount, onMounted, useTemplateRef, watch } from "vue";
import { loadLanguage } from "@/lib/code-editor/languages";
import { fleetTheme } from "@/lib/code-editor/theme";

/**
 * The File view: the workflow file in CodeMirror, as it is, comments and all. The lines the parser has errors on are
 * marked. Mod-S saves.
 */
const props = defineProps<{
  /** 1-based lines with an error. */
  errorLines: number[];
  readOnly?: boolean;
}>();

const emit = defineEmits<{
  save: [];
}>();

const content = defineModel<string>({ required: true });

const host = useTemplateRef<HTMLDivElement>("host");
const language = new Compartment();
const setErrorLines = StateEffect.define<number[]>();
let view: EditorView | null = null;

const errorLineMark = Decoration.line({ class: "wf-file__error-line" });

function marks(state: EditorState, lines: number[]): DecorationSet {
  const builder = new RangeSetBuilder<Decoration>();
  for (const line of [...new Set(lines)].sort((a, b) => a - b)) {
    if (line < 1 || line > state.doc.lines) continue;
    const at = state.doc.line(line).from;
    builder.add(at, at, errorLineMark);
  }
  return builder.finish();
}

const errorField = StateField.define<{ lines: number[]; set: DecorationSet }>({
  create: (state) => ({ lines: props.errorLines, set: marks(state, props.errorLines) }),
  update(value, transaction) {
    let lines = value.lines;
    for (const effect of transaction.effects) {
      if (effect.is(setErrorLines)) lines = effect.value;
    }
    if (lines === value.lines && !transaction.docChanged) return value;
    return { lines, set: marks(transaction.state, lines) };
  },
  provide: (field) => EditorView.decorations.from(field, (value) => value.set),
});

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
        closeBrackets(),
        highlightActiveLine(),
        fleetTheme,
        errorField,
        language.of([]),
        EditorState.readOnly.of(props.readOnly ?? false),
        EditorView.contentAttributes.of({ "aria-label": "Workflow file" }),
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

  const support = await loadLanguage("workflow.yaml");
  if (support && view) view.dispatch({ effects: language.reconfigure(support) });
});

// Text set from outside (a reload, a save) replaces the document.
watch(content, (next) => {
  if (view && next !== view.state.doc.toString()) {
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: next } });
  }
});

watch(() => props.errorLines, (lines) => {
  view?.dispatch({ effects: setErrorLines.of(lines) });
});

onBeforeUnmount(() => {
  view?.destroy();
  view = null;
});
</script>

<template>
  <div
    ref="host"
    class="wf-file"
    data-testid="workflow-file-editor"
  />
</template>

<style scoped>
.wf-file {
  flex: 1;
  min-height: 0;
  overflow: hidden;
  background: var(--panel-bg);
}

.wf-file :deep(.cm-editor) {
  height: 100%;
}

.wf-file :deep(.cm-scroller) {
  padding: 10px 0;
  font-family: var(--font-mono-stack);
}

.wf-file :deep(.wf-file__error-line) {
  background: color-mix(in srgb, var(--error) 16%, transparent);
}
</style>
