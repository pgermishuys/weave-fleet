import { unifiedMergeView } from "@codemirror/merge";
import { EditorState, type Extension, type Text } from "@codemirror/state";
import { EditorView, lineNumbers } from "@codemirror/view";
import type { FileBufferRecord } from "@/stores/file-buffers";
import { baseText, changedLines, markChangedLines } from "./agent-lines";
import { mergeSlot, readOnly } from "./editor-extensions";
import { commit } from "./file-buffer";
import { fleetTheme } from "./theme";

/**
 * The two merge views. Diff: the git base against your buffer, editable, with Revert per hunk.
 * Compare: the agent's version against yours; per hunk you take the agent's lines or keep yours.
 */
export type MergeKind = "diff" | "compare";

const LABELS: Record<MergeKind, Record<"reject" | "accept", string | null>> = {
  diff: { reject: "Revert", accept: null },
  compare: { reject: "Take the agent's", accept: "Keep mine" },
};

function controls(kind: MergeKind) {
  return (type: "reject" | "accept", action: (event: MouseEvent) => void): HTMLElement => {
    const label = LABELS[kind][type];
    if (!label) return document.createElement("span");
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.className = `cm-merge-${type}`;
    button.onmousedown = action;
    return button;
  };
}

export function mergeExtension(kind: MergeKind, original: Text): Extension {
  return unifiedMergeView({
    original,
    highlightChanges: true,
    gutter: true,
    mergeControls: controls(kind),
    collapseUnchanged: kind === "diff" ? { margin: 3, minSize: 8 } : undefined,
  });
}

/** Show a merge view in the buffer's editor, or none (`null`). */
export function setMerge(record: FileBufferRecord, merge: { kind: MergeKind; original: Text } | null): void {
  commit(record, { effects: mergeSlot.reconfigure(merge ? mergeExtension(merge.kind, merge.original) : []) });
}

/** Mark the lines that differ from the git base (or from disk, for a file git sees as unchanged). */
export function markStripe(record: FileBufferRecord, base: Text | null): void {
  const doc = record.state?.doc;
  if (!doc) return;
  const reference = base ?? record.savedDoc;
  commit(record, { effects: markChangedLines(doc, reference ? changedLines(reference, doc) : []) });
}

/** A deleted file: its last version, read-only, as all removed lines. */
export function createDeletedView(parent: HTMLElement, before: string): EditorView {
  return new EditorView({
    parent,
    state: EditorState.create({
      doc: "",
      extensions: [
        lineNumbers(),
        fleetTheme,
        readOnly(true),
        unifiedMergeView({ original: baseText(before), mergeControls: false, gutter: true }),
      ],
    }),
  });
}
