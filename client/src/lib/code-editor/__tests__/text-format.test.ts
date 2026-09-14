import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { describe, expect, it } from "vitest";
import { readTextFormat, textFormatExtension, toDiskText } from "../text-format";

const encoder = new TextEncoder();
// The server decodes file bytes with UTF8Encoding, which keeps a BOM as U+FEFF.
const decode = (bytes: Uint8Array) => new TextDecoder("utf-8", { ignoreBOM: true }).decode(bytes);

function roundTrip(bytes: Uint8Array): Uint8Array {
  const { text, format } = readTextFormat(decode(bytes));
  const state = EditorState.create({ doc: text, extensions: textFormatExtension(format) });
  return encoder.encode(toDiskText(state, format));
}

function open(raw: string) {
  const { text, format } = readTextFormat(raw);
  return { format, state: EditorState.create({ doc: text, extensions: textFormatExtension(format) }) };
}

describe("text format round trip", () => {
  const cases: Record<string, string> = {
    "LF with a trailing newline": "one\ntwo\n",
    "LF without a trailing newline": "one\ntwo",
    "CRLF": "one\r\ntwo\r\n",
    "CRLF without a trailing newline": "one\r\ntwo",
    "old Mac CR": "one\rtwo\r",
    "mixed, mostly CRLF": "a\r\nb\nc\r\nd\r\n",
    "mixed, mostly LF": "a\nb\r\nc\nd\n",
    "LF with a stray CR": "a\rb\nc\n",
    "BOM + CRLF": "\uFEFFone\r\ntwo\r\n",
    "BOM, no newline": "\uFEFFx",
    "empty": "",
    "only a newline": "\n",
    "blank lines at the end": "a\n\n\n",
    "non-ASCII": "naïve — 日本 🎉\r\n",
  };

  for (const [name, raw] of Object.entries(cases)) {
    it(`keeps the bytes of ${name}`, () => {
      const bytes = encoder.encode(raw);
      expect(roundTrip(bytes)).toEqual(bytes);
    });
  }

  it("keeps the BOM out of the editor text", () => {
    const { state, format } = open("\uFEFFhello\r\n");
    expect(format).toEqual({ lineSeparator: "\r\n", bom: true });
    expect(state.doc.line(1).text).toBe("hello");
  });

  it("splits a CRLF file into lines", () => {
    const { state } = open("a\r\nb\r\nc");
    expect(state.doc.lines).toBe(3);
    expect(state.lineBreak).toBe("\r\n");
  });
});

describe("editing keeps the file's line breaks", () => {
  it("a new line in a CRLF file uses CRLF", () => {
    const { state, format } = open("a\r\nb\r\n");
    const line = state.doc.line(1);
    const next = state.update({ changes: { from: line.to, insert: state.lineBreak + "x" } }).state;
    expect(toDiskText(next, format)).toBe("a\r\nx\r\nb\r\n");
  });

  it("pasted LF text becomes CRLF lines in a CRLF file", () => {
    const { format } = open("a\r\nb\r\n");
    const parent = document.createElement("div");
    const view = new EditorView({
      parent,
      state: EditorState.create({ doc: "a\r\nb\r\n", extensions: textFormatExtension(format) }),
    });
    const filters = view.state.facet(EditorView.clipboardInputFilter);
    const pasted = filters.reduce((text, filter) => filter(text, view.state), "x\ny\n");
    view.dispatch({ changes: { from: 0, insert: pasted } });
    expect(view.state.doc.lines).toBe(5);
    expect(toDiskText(view.state, format)).toBe("x\r\ny\r\na\r\nb\r\n");
    view.destroy();
  });

  it("an untouched mixed file keeps its odd breaks after an edit elsewhere", () => {
    const { state, format } = open("a\r\nb\nc\r\n");
    const next = state.update({ changes: { from: 0, insert: "z" } }).state;
    expect(toDiskText(next, format)).toBe("za\r\nb\nc\r\n");
  });
});
