import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { afterEach, describe, expect, it, vi } from "vitest";
import { linePicking, pickedLines, pickLines, selectedLines } from "../line-pick";

const doc = Array.from({ length: 30 }, (_, i) => `line ${i + 1}`).join("\n");
let view: EditorView;

function mountView(): EditorView {
  view = new EditorView({ state: EditorState.create({ doc, extensions: linePicking }), parent: document.body });
  return view;
}

/** jsdom has no layout: the gutter finds its line by height, so point that at line `n`. */
function overLine(n: number): void {
  vi.spyOn(view, "lineBlockAtHeight").mockReturnValue(view.lineBlockAt(view.state.doc.line(n).from));
}

function pressNumber(n: number, init: MouseEventInit = {}): void {
  overLine(n);
  view.dom.querySelector(".cm-lineNumbers")!.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, button: 0, ...init }));
}

afterEach(() => {
  view?.destroy();
  vi.restoreAllMocks();
  document.body.innerHTML = "";
});

describe("line picking", () => {
  it("a click on a line number picks that line and highlights it", () => {
    mountView();
    pressNumber(18);
    window.dispatchEvent(new MouseEvent("mouseup"));

    expect(pickedLines(view.state)).toEqual({ first: 18, last: 18 });
    expect(view.dom.querySelectorAll(".cm-picked-line").length).toBe(1);
  });

  it("shift-click extends from where the pick started, either way", () => {
    mountView();
    pressNumber(18);
    window.dispatchEvent(new MouseEvent("mouseup"));
    pressNumber(27, { shiftKey: true });
    window.dispatchEvent(new MouseEvent("mouseup"));
    expect(pickedLines(view.state)).toEqual({ first: 18, last: 27 });

    pressNumber(12, { shiftKey: true });
    window.dispatchEvent(new MouseEvent("mouseup"));
    expect(pickedLines(view.state)).toEqual({ first: 12, last: 18 });
  });

  it("a plain click starts a new pick", () => {
    mountView();
    pickLines(view, 18, 27);
    pressNumber(5);
    window.dispatchEvent(new MouseEvent("mouseup"));
    expect(pickedLines(view.state)).toEqual({ first: 5, last: 5 });
  });

  it("dragging down the numbers extends the pick until the button comes up", () => {
    mountView();
    pressNumber(4);
    overLine(9);
    window.dispatchEvent(new MouseEvent("mousemove"));
    expect(pickedLines(view.state)).toEqual({ first: 4, last: 9 });

    window.dispatchEvent(new MouseEvent("mouseup"));
    overLine(20);
    window.dispatchEvent(new MouseEvent("mousemove"));
    expect(pickedLines(view.state)).toEqual({ first: 4, last: 9 });
  });

  it("Esc clears the pick", () => {
    mountView();
    pickLines(view, 3, 6);
    view.contentDOM.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(pickedLines(view.state)).toBeNull();
    expect(view.dom.querySelectorAll(".cm-picked-line").length).toBe(0);
  });

  it("survives a click in the text, but not selecting text", () => {
    mountView();
    pickLines(view, 3, 6);
    view.dispatch({ selection: { anchor: view.state.doc.line(10).from }, userEvent: "select.pointer" });
    expect(pickedLines(view.state)).toEqual({ first: 3, last: 6 });

    view.dispatch({ selection: { anchor: 0, head: 8 }, userEvent: "select.pointer" });
    expect(pickedLines(view.state)).toBeNull();
  });

  it("moves with lines added above it", () => {
    mountView();
    pickLines(view, 3, 6);
    view.dispatch({ changes: { from: 0, insert: "new\nnew\n" } });
    expect(pickedLines(view.state)).toEqual({ first: 5, last: 8 });
  });
});

describe("selectedLines", () => {
  it("spans the selection's lines, not the line it ends at the start of", () => {
    const state = EditorState.create({ doc: "one\ntwo\nthree\nfour" });
    const line = (n: number) => state.doc.line(n).from;
    expect(selectedLines(state)).toBeNull();
    expect(selectedLines(state.update({ selection: { anchor: line(2), head: line(4) } }).state)).toEqual({ first: 2, last: 3 });
    expect(selectedLines(state.update({ selection: { anchor: line(2) + 1, head: line(2) + 2 } }).state)).toEqual({ first: 2, last: 2 });
  });
});
