import { EditorState, Text } from "@codemirror/state";
import { history, undo } from "@codemirror/commands";
import { EditorView } from "@codemirror/view";
import { describe, expect, it } from "vitest";
import type { FileBufferRecord } from "@/stores/file-buffers";
import { changedLines, baseText } from "../agent-lines";
import {
  applyDiskFile,
  diskTextOf,
  isDirty,
  loadBuffer,
  markSaved,
  rebaseOnDisk,
  takeDiskVersion,
  NOT_TEXT_MESSAGE,
  TOO_LARGE_MESSAGE,
  type DiskFile,
} from "../file-buffer";
import { minimalChange } from "../minimal-change";
import { textFormatExtension, type TextFormat } from "../text-format";

const build = (format: TextFormat) => textFormatExtension(format);

function record(): FileBufferRecord {
  return { sessionId: "s1", path: "src/app.ts", state: null, savedDoc: null, baseHash: null, format: null, view: null, handlers: {} };
}

function disk(content: string | null, hash: string, extra: Partial<DiskFile> = {}): DiskFile {
  return { content, hash, isBinary: false, isTruncated: false, ...extra };
}

function type(buffer: FileBufferRecord, insert: string, at = 0): void {
  buffer.state = buffer.state!.update({ changes: { from: at, insert } }).state;
}

function opened(content: string, hash = "h1"): FileBufferRecord {
  const buffer = record();
  expect(loadBuffer(buffer, disk(content, hash), build)).toBeNull();
  return buffer;
}

describe("minimalChange", () => {
  const text = (s: string) => Text.of(s.split("\n"));

  it("replaces only what differs", () => {
    const change = minimalChange(text("a\nb\nc\nd"), text("a\nB\nc\nd"))!;
    expect(change.from).toBe(2);
    expect(change.to).toBe(3);
    expect(change.insert.toString()).toBe("B");
    expect(change.lines).toEqual({ from: 2, to: 2 });
  });

  it("handles inserted and removed lines", () => {
    expect(minimalChange(text("a\nc"), text("a\nb\nc"))!.lines).toEqual({ from: 2, to: 3 });
    const removal = minimalChange(text("a\nb\nc"), text("a\nc"))!;
    expect(removal.insert.length).toBe(0);
  });

  it("is null for the same text", () => {
    expect(minimalChange(text("x\ny"), text("x\ny"))).toBeNull();
  });

  it("keeps the cursor where it was when the change is elsewhere", () => {
    const view = new EditorView({ parent: document.createElement("div"), state: EditorState.create({ doc: "one\ntwo\nthree" }) });
    view.dispatch({ selection: { anchor: 11 } });
    const change = minimalChange(view.state.doc, text("ONE\ntwo\nthree"))!;
    view.dispatch({ changes: { from: change.from, to: change.to, insert: change.insert } });
    expect(view.state.selection.main.head).toBe(11);
    view.destroy();
  });
});

describe("file buffer", () => {
  it("opens clean", () => {
    const buffer = opened("const one = 1;\n");
    expect(isDirty(buffer)).toBe(false);
    expect(buffer.baseHash).toBe("h1");
  });

  it("says why a file can't be edited", () => {
    expect(loadBuffer(record(), disk(null, "h", { isTruncated: true }), build)).toBe(TOO_LARGE_MESSAGE);
    expect(loadBuffer(record(), disk(null, "h", { isBinary: true }), build)).toBe(NOT_TEXT_MESSAGE);
  });

  it("is dirty after typing and clean again when the text goes back", () => {
    const buffer = opened("abc");
    type(buffer, "x");
    expect(isDirty(buffer)).toBe(true);
    buffer.state = buffer.state!.update({ changes: { from: 0, to: 1 } }).state;
    expect(isDirty(buffer)).toBe(false);
  });

  it("does nothing when the hash on disk is the same", () => {
    const buffer = opened("abc");
    const before = buffer.state;
    expect(applyDiskFile(buffer, disk("abc", "h1"), build)).toEqual({ kind: "unchanged" });
    expect(buffer.state).toBe(before);
  });

  it("takes the agent's change into a clean buffer, out of undo history", () => {
    const buffer = opened("line 1\nline 2\nline 3\n");
    const result = applyDiskFile(buffer, disk("line 1\nline TWO\nline 3\n", "h2"), build);
    expect(result).toEqual({ kind: "updated", lines: { from: 2, to: 2 } });
    expect(diskTextOf(buffer)).toBe("line 1\nline TWO\nline 3\n");
    expect(buffer.baseHash).toBe("h2");
    expect(isDirty(buffer)).toBe(false);
  });

  it("never touches a dirty buffer: a disk change is a conflict", () => {
    const buffer = opened("abc");
    type(buffer, "mine ");
    const result = applyDiskFile(buffer, disk("agent's", "h2"), build);
    expect(result).toEqual({ kind: "conflict", conflict: { diskText: "agent's", hash: "h2" } });
    expect(diskTextOf(buffer)).toBe("mine abc");
    expect(buffer.baseHash).toBe("h1");
  });

  it("\"Use the agent's\" replaces the buffer, as an edit you can undo", () => {
    const buffer = record();
    loadBuffer(buffer, disk("abc", "h1"), (format) => [textFormatExtension(format), history()]);
    type(buffer, "mine ");
    takeDiskVersion(buffer, { diskText: "agent's", hash: "h2" }, build);
    expect(diskTextOf(buffer)).toBe("agent's");
    expect(buffer.baseHash).toBe("h2");
    expect(isDirty(buffer)).toBe(false);

    const view = new EditorView({ parent: document.createElement("div"), state: buffer.state! });
    undo(view);
    expect(view.state.doc.toString()).toBe("mine abc");
    view.destroy();
  });

  it("after Compare the buffer is based on the agent's version and still unsaved", () => {
    const buffer = opened("abc");
    type(buffer, "mine ");
    rebaseOnDisk(buffer, { diskText: "agent's", hash: "h2" });
    expect(buffer.baseHash).toBe("h2");
    expect(isDirty(buffer)).toBe(true);
  });

  it("is clean after a save, and dirty again if typing went on during it", () => {
    const buffer = opened("abc");
    type(buffer, "1");
    const sent = buffer.state!.doc;
    type(buffer, "2");
    markSaved(buffer, "h2", sent);
    expect(buffer.baseHash).toBe("h2");
    expect(isDirty(buffer)).toBe(true);
    markSaved(buffer, "h3");
    expect(isDirty(buffer)).toBe(false);
  });

  it("keeps CRLF when the agent changes a CRLF file", () => {
    const buffer = opened("a\r\nb\r\n");
    applyDiskFile(buffer, disk("a\r\nB\r\nc\r\n", "h2"), build);
    expect(diskTextOf(buffer)).toBe("a\r\nB\r\nc\r\n");
    expect(buffer.state!.doc.lines).toBe(4);
  });

  it("starts a fresh state when the agent changes the line breaks", () => {
    const buffer = opened("a\nb\n");
    const result = applyDiskFile(buffer, disk("a\r\nb\r\n", "h2"), build);
    expect(result).toEqual({ kind: "updated", lines: null });
    expect(diskTextOf(buffer)).toBe("a\r\nb\r\n");
    expect(buffer.format?.lineSeparator).toBe("\r\n");
  });

  it("goes through the mounted editor so the view shows the change", () => {
    const buffer = opened("one\ntwo\n");
    buffer.view = new EditorView({ parent: document.createElement("div"), state: buffer.state! });
    applyDiskFile(buffer, disk("one\n2\n", "h2"), build);
    expect(buffer.view.state.doc.toString()).toBe("one\n2\n");
    expect(buffer.state).toBe(buffer.view.state);
    buffer.view.destroy();
  });

  it("a file that became binary while clean can't be edited any more", () => {
    const buffer = opened("abc");
    expect(applyDiskFile(buffer, disk(null, "h2", { isBinary: true }), build)).toEqual({ kind: "unavailable", message: NOT_TEXT_MESSAGE });
  });
});

describe("changedLines", () => {
  it("marks lines that differ from the git base", () => {
    const doc = Text.of(["a", "B", "c", "new", "d", ""]);
    expect(changedLines(baseText("a\nb\nc\nd\n"), doc)).toEqual([2, 4]);
  });

  it("ignores line breaks and a BOM in the base", () => {
    expect(changedLines(baseText("\uFEFFa\r\nb\r\n"), Text.of(["a", "b", ""]))).toEqual([]);
  });

  it("marks the line after a deletion", () => {
    expect(changedLines(baseText("a\nb\nc"), Text.of(["a", "c"]))).toEqual([2]);
  });
});
