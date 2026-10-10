import { describe, expect, test } from "bun:test";
import { createElements, ownerOf, toWire, type WireOptions } from "../src/tree";

const A = createElements("mod-a");
const B = createElements("mod-b");
const noop = () => {};

function opts(over: Partial<WireOptions> = {}): WireOptions & { handles: any[] } {
  const handles: any[] = [];
  return {
    site: "ComposerBand",
    defaultOwner: "mod-a",
    pageExists: () => true,
    allocHandle: (cb) => {
      handles.push(cb);
      return `h${handles.length}`;
    },
    handles,
    ...over,
  };
}
const wire = (tree: unknown, over: Partial<WireOptions> = {}) => toWire(tree, opts(over));
const reason = (tree: unknown, over: Partial<WireOptions> = {}) => {
  const r = wire(tree, over);
  if (r.ok) throw new Error("expected invalid, got " + JSON.stringify(r.tree));
  return r.reason;
};
const tree = (tree: unknown, over: Partial<WireOptions> = {}) => {
  const r = wire(tree, over);
  if (!r.ok) throw new Error(r.reason);
  return r.tree as any;
};

describe("factories", () => {
  test("Box and Text split children from props and normalise to an array", () => {
    const e: any = A.Box({ gap: 1, children: "hi" as any });
    expect(e.type).toBe("Box");
    expect(e.props).toEqual({ gap: 1 });
    expect(e.children).toEqual(["hi"]);
    expect((A.Text({}) as any).children).toEqual([]);
  });
  test("other elements are type and props only", () => {
    const e: any = A.Pill({ tone: "good", label: "x" });
    expect(Object.keys(e)).toEqual(["type", "props"]);
  });
  test("the host knows each element's owner, which isn't on the element; elements are frozen", () => {
    const e: any = A.Pill({ tone: "good", label: "x" });
    expect(ownerOf(e)).toBe("mod-a");
    expect(Object.getOwnPropertySymbols(e)).toEqual([]);
    expect(ownerOf({ ...e })).toBeUndefined();
    expect(Object.isFrozen(e)).toBe(true);
    expect(JSON.stringify(e)).toBe('{"type":"Pill","props":{"tone":"good","label":"x"}}');
  });
  test("factories don't throw on odd input", () => {
    expect(() => (A.Box as any)(null)).not.toThrow();
    expect(() => (A.Pill as any)(42)).not.toThrow();
    expect(() => (A.Text as any)(undefined)).not.toThrow();
  });
});

describe("happy paths", () => {
  test("null draws nothing", () => {
    expect(wire(null)).toEqual({ ok: true, tree: null, drawnBy: [] });
  });
  test("Box with every prop", () => {
    const t = tree(
      A.Box({
        key: "b", flexDirection: "row", gap: 2, padding: 1, paddingX: 3, paddingY: 4, alignItems: "baseline",
        justifyContent: "space-between", flexWrap: "wrap", flexGrow: 1, width: "33.3%", borderStyle: "quote",
        borderColor: "warn", background: "tint", children: ["a", 1],
      }),
    );
    expect(t.props.width).toBe("33.3%");
    expect(t.children).toEqual(["a", "1"]);
  });
  test("Text with every prop", () => {
    const t = tree(A.Text({ color: "muted", bold: true, italic: true, strikethrough: true, code: true, dimColor: true, wrap: "truncate", children: ["x"] }));
    expect(t).toEqual({
      type: "Text",
      props: { color: "muted", bold: true, italic: true, strikethrough: true, code: true, dimColor: true, wrap: "truncate" },
      children: ["x"],
    });
  });
  test("Pill and Icon", () => {
    expect(tree(A.Pill({ tone: "accent", label: "L", icon: "bug" }))).toEqual({ type: "Pill", props: { tone: "accent", label: "L", icon: "bug" } });
    expect(tree(A.Icon({ name: "eye-off", color: "good", label: "hidden" }))).toEqual({ type: "Icon", props: { name: "eye-off", color: "good", label: "hidden" } });
  });
  test("Button, Input and Select become props plus handles", () => {
    const o = opts();
    const t: any = toWire(
      A.Box({
        children: [
          A.Button({ key: "go", label: "Go", onPress: noop, icon: "play", tone: "danger", disabled: false }),
          A.Input({ key: "in", label: "L", placeholder: "p", value: "v", submitLabel: "S", onSubmit: noop, onInput: noop }),
          A.Select({ key: "sel", label: "Pick", options: [{ value: "a", label: "A" }], value: "a", onSelect: noop }),
        ],
      }),
      o,
    );
    const [b, i, s] = t.tree.children;
    expect(b).toEqual({ type: "Button", props: { key: "go", label: "Go", icon: "play", tone: "danger", disabled: false }, handles: { onPress: "h1" } });
    expect(i.handles).toEqual({ onSubmit: "h2", onInput: "h3" });
    expect(i.props).toEqual({ key: "in", label: "L", placeholder: "p", value: "v", submitLabel: "S" });
    expect(s.handles).toEqual({ onSelect: "h4" });
    expect(o.handles.map((h) => [h.owner, h.kind, h.key])).toEqual([
      ["mod-a", "onPress", "go"], ["mod-a", "onSubmit", "in"], ["mod-a", "onInput", "in"], ["mod-a", "onSelect", "sel"],
    ]);
    expect(o.handles[0].fn).toBe(noop);
  });
  test("an Input with no callbacks still has empty handles", () => {
    expect(tree(A.Input({ key: "i" }))).toEqual({ type: "Input", props: { key: "i" }, handles: {} });
  });
  test("Markdown, Code and Page", () => {
    expect(tree(A.Markdown({ text: "# hi", key: "m", dimColor: true })).props).toEqual({ text: "# hi", key: "m", dimColor: true });
    expect(tree(A.Code({ source: "x", language: "ts", path: "a.ts", startLine: 3, format: "diff", wrap: "wrap" })).props.startLine).toBe(3);
    expect(tree(A.Page({ key: "p", path: "ui/a.html", title: "T", query: { a: "1" } }))).toEqual({
      type: "Page", props: { key: "p", path: "ui/a.html", title: "T", query: { a: "1" } }, mod: "mod-a",
    });
  });
  test("undefined props are absent", () => {
    expect(tree(A.Pill({ tone: "good", label: "x", icon: undefined }))).toEqual({ type: "Pill", props: { tone: "good", label: "x" } });
  });
  test("the output survives JSON", () => {
    const t = tree(A.Box({ children: [A.Text({ children: ["a", A.Text({ bold: true, children: ["b"] })] }), A.Button({ key: "k", label: "l", onPress: noop })] }));
    expect(JSON.parse(JSON.stringify(t))).toEqual(t);
    expect(Object.getOwnPropertySymbols(t)).toEqual([]);
  });
  test("Fleet alone is a tree", () => {
    expect(tree({ type: "Fleet" })).toEqual({ type: "Fleet" });
  });
});

describe("bad values", () => {
  test("a bad tone names the element, path and allowed values", () => {
    const r = reason(A.Box({ children: [A.Text({}), A.Pill({ tone: "red" as any, label: "x" })] }));
    expect(r).toBe(`Box > Pill[1]: tone "red" isn't one of good, warn, bad, neutral, accent`);
  });
  test.each([
    ["Box flexDirection", A.Box({ flexDirection: "diagonal" as any })],
    ["Box gap", A.Box({ gap: 5 as any })],
    ["Box padding", A.Box({ padding: "1" as any })],
    ["Box alignItems", A.Box({ alignItems: "top" as any })],
    ["Box justifyContent", A.Box({ justifyContent: "around" as any })],
    ["Box flexWrap", A.Box({ flexWrap: true as any })],
    ["Box flexGrow", A.Box({ flexGrow: 2 as any })],
    ["Box width px", A.Box({ width: "50px" as any })],
    ["Box width over 100", A.Box({ width: "101%" as any })],
    ["Box borderStyle", A.Box({ borderStyle: "double" as any })],
    ["Box borderColor hex", A.Box({ borderColor: "#fff" as any })],
    ["Box background", A.Box({ background: "red" as any })],
    ["Box key", A.Box({ key: "no spaces" })],
    ["Text color", A.Text({ color: "red" as any })],
    ["Text bold", A.Text({ bold: "yes" as any })],
    ["Text wrap", A.Text({ wrap: "clip" as any })],
    ["Pill label type", A.Pill({ tone: "good", label: 3 as any })],
    ["Pill icon", A.Pill({ tone: "good", label: "x", icon: "rocket" as any })],
    ["Icon name", A.Icon({ name: "rocket" as any })],
    ["Icon color", A.Icon({ name: "check", color: "blue" as any })],
    ["Button onPress type", A.Button({ key: "k", label: "l", onPress: "x" as any })],
    ["Button tone", A.Button({ key: "k", label: "l", onPress: noop, tone: "loud" as any })],
    ["Button disabled", A.Button({ key: "k", label: "l", onPress: noop, disabled: 1 as any })],
    ["Input value", A.Input({ key: "k", value: 3 as any })],
    ["Input onInput", A.Input({ key: "k", onInput: 1 as any })],
    ["Select empty options", A.Select({ key: "k", options: [], onSelect: noop })],
    ["Select 201 options", A.Select({ key: "k", options: Array.from({ length: 201 }, (_, i) => ({ value: "v" + i, label: "l" })), onSelect: noop })],
    ["Select duplicate values", A.Select({ key: "k", options: [{ value: "a", label: "A" }, { value: "a", label: "B" }], onSelect: noop })],
    ["Select extra option field", A.Select({ key: "k", options: [{ value: "a", label: "A", x: 1 } as any], onSelect: noop })],
    ["Select non-string value", A.Select({ key: "k", options: [{ value: 1, label: "A" } as any], onSelect: noop })],
    ["Markdown text", A.Markdown({ text: 1 as any })],
    ["Code startLine zero", A.Code({ source: "x", startLine: 0 })],
    ["Code startLine fraction", A.Code({ source: "x", startLine: 1.5 })],
    ["Code format", A.Code({ source: "x", format: "patch" as any })],
    ["Page query values", A.Page({ key: "k", path: "a.html", title: "T", query: { a: 1 } as any })],
    ["Page query over 4 KiB", A.Page({ key: "k", path: "a.html", title: "T", query: { a: "x".repeat(5000) } })],
  ])("%s is refused", (_n, t) => {
    expect(wire(t).ok).toBe(false);
  });
  test("a good width of 0% and 100% passes", () => {
    expect(wire(A.Box({ width: "0%" })).ok).toBe(true);
    expect(wire(A.Box({ width: "100%" })).ok).toBe(true);
  });
  test("a missing required prop is named", () => {
    expect(reason(A.Pill({ tone: "good" } as any))).toBe("Pill: label is required");
    expect(reason(A.Icon({} as any))).toContain("name is required");
    expect(reason(A.Button({ key: "k", label: "l" } as any))).toContain("onPress is required");
    expect(reason(A.Select({ key: "k", options: [{ value: "a", label: "A" }] } as any))).toContain("onSelect is required");
    expect(reason(A.Markdown({} as any))).toContain("text is required");
    expect(reason(A.Code({} as any))).toContain("source is required");
    expect(reason(A.Page({ key: "k", path: "a.html" } as any))).toContain("title is required");
  });
  test("an unknown prop is invalid", () => {
    expect(reason(A.Pill({ tone: "good", label: "x", size: 3 } as any))).toBe("Pill: size isn't a prop of Pill");
    expect(reason(A.Text({ children: [], style: "x" } as any))).toContain("style isn't a prop");
  });
  test("an unknown type is invalid", () => {
    expect(reason({ type: "Table", props: {} })).toContain(`unknown element type "Table"`);
    expect(reason("hello")).toContain("not an element");
    expect(reason({ nope: 1 })).toContain("not an element");
  });
  test("a hand-written element with an extra field is invalid", () => {
    expect(reason({ type: "Pill", props: { tone: "good", label: "x" }, extra: 1 })).toContain(`unknown field "extra"`);
    expect(reason({ type: "Fleet", props: {} })).toContain("Fleet takes no props");
  });
});

describe("keys", () => {
  test("controls and Page need a key", () => {
    expect(reason(A.Button({ label: "l", onPress: noop } as any))).toContain("key is required");
    expect(reason(A.Input({} as any))).toContain("key is required");
    expect(reason(A.Select({ options: [{ value: "a", label: "A" }], onSelect: noop } as any))).toContain("key is required");
    expect(reason(A.Page({ path: "a.html", title: "T" } as any))).toContain("key is required");
  });
  test("Box and Markdown keys are optional", () => {
    expect(wire(A.Box({})).ok).toBe(true);
    expect(wire(A.Markdown({ text: "x" })).ok).toBe(true);
  });
  test("a key must match the pattern", () => {
    expect(reason(A.Button({ key: "a b", label: "l", onPress: noop }))).toContain("key");
    expect(reason(A.Button({ key: "x".repeat(65), label: "l", onPress: noop }))).toContain("key");
    expect(reason(A.Button({ key: "", label: "l", onPress: noop }))).toContain("key");
    expect(wire(A.Button({ key: "x".repeat(64), label: "l", onPress: noop })).ok).toBe(true);
    expect(wire(A.Button({ key: "A_b-c.1", label: "l", onPress: noop })).ok).toBe(true);
  });
  test("keys are unique across every element kind", () => {
    const r = reason(A.Box({ key: "same", children: [A.Button({ key: "same", label: "l", onPress: noop })] }));
    expect(r).toBe(`Box > Button[0]: key "same" is used twice in the tree`);
    expect(reason(A.Box({ children: [A.Markdown({ key: "m", text: "a" }), A.Markdown({ key: "m", text: "b" })] }))).toContain("used twice");
  });
});

describe("Fleet", () => {
  test("at most once per tree", () => {
    expect(wire(A.Box({ children: [{ type: "Fleet" } as any] })).ok).toBe(true);
    expect(reason(A.Box({ children: [{ type: "Fleet" } as any, A.Box({ children: [{ type: "Fleet" } as any] })] }))).toContain("only once");
  });
  test("has no owner", () => {
    const r: any = wire(A.Box({ children: [{ type: "Fleet" } as any] }));
    expect(r.drawnBy).toEqual(["mod-a"]);
    expect(wire({ type: "Fleet" })).toEqual({ ok: true, tree: { type: "Fleet" }, drawnBy: [] });
  });
});

describe("children", () => {
  test("false, null, undefined are dropped, arrays flattened, numbers stringified", () => {
    const t = tree(A.Box({ children: ["a", false, null, undefined, [1, [2, A.Pill({ tone: "good", label: "p" })]] as any, 0] }));
    expect(t.children).toEqual(["a", "1", "2", { type: "Pill", props: { tone: "good", label: "p" } }, "0"]);
  });
  test("Text takes strings, numbers and Text only", () => {
    expect(wire(A.Text({ children: ["a", 1, A.Text({ children: ["b"] })] })).ok).toBe(true);
    expect(reason(A.Text({ children: [A.Pill({ tone: "good", label: "p" })] }))).toContain("Text can only hold strings, numbers and Text, not Pill");
    expect(reason(A.Text({ children: [A.Box({})] }))).toContain("not Box");
  });
  test("Box refuses odd children", () => {
    expect(reason(A.Box({ children: [true as any] }))).toContain("must be an element, string or number");
    expect(reason(A.Box({ children: [{ a: 1 } as any] }))).toContain("must be an element");
  });
  test("other elements have no children", () => {
    expect(reason(A.Pill({ tone: "good", label: "x", children: ["a"] } as any))).toContain("children isn't a prop of Pill");
    expect(reason({ type: "Pill", props: { tone: "good", label: "x" }, children: [] })).toContain(`unknown field "children"`);
  });
  test("a cycle is invalid and doesn't hang", () => {
    const kids: any[] = [];
    const loop: any = { type: "Box", props: {}, children: kids };
    kids.push(loop);
    expect(reason(loop)).toContain("contains itself");
    const arr: any[] = [];
    arr.push(arr);
    expect(reason({ type: "Box", props: {}, children: arr })).toContain("contain themselves");
  });
  test("a node shared without a cycle is fine", () => {
    const p = A.Pill({ tone: "good", label: "p" });
    expect(tree(A.Box({ children: [p, p] })).children).toHaveLength(2);
  });
});

describe("inline sites", () => {
  const row = (children: any[]) => A.Box({ flexDirection: "row", children });
  const pill = A.Pill({ tone: "good", label: "p" });
  for (const site of ["ToolUse", "StatusChip"] as const) {
    test(`${site} takes a row of Pills, Text, Icon, Button`, () => {
      expect(wire(row([pill, A.Text({ children: ["t"] }), A.Icon({ name: "check" }), A.Button({ key: "b", label: "l", onPress: noop })]), { site }).ok).toBe(true);
      expect(wire(row([row([pill])]), { site }).ok).toBe(true);
      expect(wire(pill, { site }).ok).toBe(true);
      expect(wire({ type: "Fleet" }, { site }).ok).toBe(true);
    });
    test(`${site} refuses a column Box, Markdown, Input, Select, Code, Page`, () => {
      expect(reason(A.Box({ flexDirection: "column", children: [pill] }), { site })).toContain("must be a row");
      expect(reason(A.Markdown({ text: "x" }), { site })).toBe(`Markdown: Markdown isn't allowed at ${site}, which takes inline elements only`);
      expect(reason(A.Input({ key: "i" }), { site })).toContain("inline");
      expect(reason(row([pill, A.Markdown({ text: "x" })]), { site })).toContain("Box > Markdown[1]");
      expect(reason(row([A.Code({ source: "x" })]), { site })).toContain("inline");
      expect(reason(row([A.Select({ key: "s", options: [{ value: "a", label: "A" }], onSelect: noop })]), { site })).toContain("inline");
      expect(reason(row([A.Page({ key: "p", path: "a.html", title: "t" })]), { site })).toContain("inline");
    });
  }
  for (const site of ["ToolResult", "ComposerBand", "Pane"] as const) {
    test(`${site} takes everything`, () => {
      expect(wire(A.Box({ children: [A.Markdown({ text: "x" }), A.Input({ key: "i" }), A.Code({ source: "s" })] }), { site }).ok).toBe(true);
    });
  }
});

describe("limits", () => {
  test("node count", () => {
    const t = A.Box({ children: [A.Text({}), A.Text({}), A.Text({})] });
    expect(wire(t, { limits: { treeNodes: 4 } }).ok).toBe(true);
    expect(reason(t, { limits: { treeNodes: 3 } })).toContain("more than 3 elements");
  });
  test("strings don't count as nodes", () => {
    expect(wire(A.Box({ children: ["a", "b", "c", "d", "e"] }), { limits: { treeNodes: 1 } }).ok).toBe(true);
  });
  test("depth, root being 1", () => {
    const t = A.Box({ children: [A.Box({ children: [A.Text({})] })] });
    expect(wire(t, { limits: { treeDepth: 3 } }).ok).toBe(true);
    expect(reason(t, { limits: { treeDepth: 2 } })).toContain("deeper than 2");
  });
  test("a very deep tree is refused at the default limit without overflowing", () => {
    let t: any = A.Text({});
    for (let i = 0; i < 5000; i++) t = A.Box({ children: [t] });
    expect(reason(t)).toContain("deeper than 32");
  });
  test("text is cut across several strings", () => {
    const t = tree(A.Box({ children: ["abcd", "efgh", "ijkl"] }), { limits: { treeTextChars: 6 } });
    expect(t.children).toEqual(["abcd", "ef", ""]);
  });
  test("text is cut across Markdown and Code in tree order", () => {
    const t = tree(A.Box({ children: ["abc", A.Markdown({ text: "defg" }), A.Code({ source: "hij" }), "k"] }), { limits: { treeTextChars: 5 } });
    expect(t.children[0]).toBe("abc");
    expect(t.children[1].props.text).toBe("de");
    expect(t.children[2].props.source).toBe("");
    expect(t.children[3]).toBe("");
  });
  test("numbers count as text", () => {
    expect(tree(A.Box({ children: [12345] }), { limits: { treeTextChars: 3 } }).children).toEqual(["123"]);
  });
  test("bytes over the limit are invalid, measured in UTF-8", () => {
    const t = A.Markdown({ text: "é".repeat(30) });
    expect(wire(t, { limits: { treeBytes: 200 } }).ok).toBe(true);
    expect(reason(t, { limits: { treeBytes: 60 } })).toContain("byte limit");
  });
});

describe("Page", () => {
  test("asks pageExists with the owner and path", () => {
    const calls: any[] = [];
    const page = A.Page({ key: "p", path: "ui/a.html", title: "T" });
    expect(wire(page, { pageExists: (o, p) => (calls.push([o, p]), true) }).ok).toBe(true);
    expect(calls).toEqual([["mod-a", "ui/a.html"]]);
    expect(reason(page, { pageExists: () => false })).toContain(`path "ui/a.html"`);
  });
  test("a subtree from another mod is checked against that mod", () => {
    const calls: any[] = [];
    wire(A.Box({ children: [B.Page({ key: "p", path: "x.html", title: "T" })] }), { pageExists: (o) => (calls.push(o), true) });
    expect(calls).toEqual(["mod-b"]);
  });
});

describe("handles", () => {
  test("allocated in tree order", () => {
    const o = opts();
    toWire(A.Box({ children: [A.Button({ key: "one", label: "1", onPress: noop }), A.Box({ children: [A.Button({ key: "two", label: "2", onPress: noop })] })] }), o);
    expect(o.handles.map((h) => h.key)).toEqual(["one", "two"]);
  });
  test("an invalid tree allocates none", () => {
    const o = opts();
    const r = toWire(A.Box({ children: [A.Button({ key: "one", label: "1", onPress: noop }), A.Pill({ tone: "red" } as any)] }), o);
    expect(r.ok).toBe(false);
    expect(o.handles).toEqual([]);
  });
  test("a tree over the byte limit allocates none", () => {
    const o = opts({ limits: { treeBytes: 10 } });
    expect(toWire(A.Button({ key: "one", label: "1", onPress: noop }), o).ok).toBe(false);
    expect(o.handles).toEqual([]);
  });
  test("functions never reach the wire props", () => {
    const t = tree(A.Button({ key: "k", label: "l", onPress: noop }));
    expect(JSON.stringify(t)).toBe('{"type":"Button","props":{"key":"k","label":"l"},"handles":{"onPress":"h1"}}');
  });
});

describe("owners", () => {
  test("drawnBy lists distinct owners in first-seen order, and handles carry their own owner", () => {
    const o = opts();
    const r: any = toWire(
      A.Box({ children: [B.Box({ children: [B.Button({ key: "b", label: "l", onPress: noop })] }), A.Text({ children: ["x"] }), B.Pill({ tone: "good", label: "p" })] }),
      o,
    );
    expect(r.drawnBy).toEqual(["mod-a", "mod-b"]);
    expect(o.handles[0].owner).toBe("mod-b");
  });
  test("an untagged hand-written element belongs to the default owner", () => {
    const r: any = wire({ type: "Box", props: {}, children: [{ type: "Pill", props: { tone: "good", label: "x" } }, B.Icon({ name: "check" })] }, { defaultOwner: "mod-z" });
    expect(r.ok).toBe(true);
    expect(r.drawnBy).toEqual(["mod-z", "mod-b"]);
  });
  test("a hand-written element may omit props and children", () => {
    expect(tree({ type: "Box" })).toEqual({ type: "Box", props: {}, children: [] });
  });
});

describe("never throws", () => {
  test("a getter that throws becomes an invalid tree", () => {
    const bad = { type: "Pill", get props() { throw new Error("boom"); } };
    expect(reason(bad)).toContain("boom");
  });
  test.each([[undefined], [3], ["x"], [[]], [true]])("odd root %p", (v) => {
    expect(wire(v).ok).toBe(false);
  });
});

describe("test-chips trees", () => {
  test("the ToolUse row of pills", () => {
    const row = A.Box({
      flexDirection: "row",
      gap: 1,
      children: [A.Pill({ tone: "good", label: "212 passed" }), false, A.Pill({ tone: "bad", label: "2 failed" }), A.Pill({ tone: "neutral", label: "4 skipped" })],
    });
    const r: any = wire(row, { site: "ToolUse" });
    expect(r.ok).toBe(true);
    expect(r.tree.children.map((c: any) => c.props.label)).toEqual(["212 passed", "2 failed", "4 skipped"]);
  });
  test("the ToolResult column with failing names and Fleet's own body", () => {
    const col = A.Box({
      flexDirection: "column",
      gap: 2,
      children: [
        ...["Adds up", "Splits lines"].map((n) => A.Text({ code: true, color: "bad", children: [n] })),
        { type: "Fleet" } as any,
      ],
    });
    const r: any = wire(col, { site: "ToolResult" });
    expect(r.ok).toBe(true);
    expect(r.tree.children).toEqual([
      { type: "Text", props: { code: true, color: "bad" }, children: ["Adds up"] },
      { type: "Text", props: { code: true, color: "bad" }, children: ["Splits lines"] },
      { type: "Fleet" },
    ]);
    expect(r.drawnBy).toEqual(["mod-a"]);
  });
});

describe("review 2 addendum (agreeing with the client's renderer)", () => {
  test("names that live on Object.prototype are unknown props and unknown elements, refused cleanly", () => {
    for (const name of ["isPrototypeOf", "hasOwnProperty", "__proto__", "constructor", "toString", "valueOf"]) {
      const props: Record<string, unknown> = { tone: "good", label: "x" };
      Object.defineProperty(props, name, { value: () => {}, enumerable: true });
      expect(reason({ type: "Pill", props })).toBe(`Pill: ${name} isn't a prop of Pill`);
      expect(reason({ type: name, props: {} })).toBe(`${name}: unknown element type ${JSON.stringify(name)}`);
    }
  });

  test("every drawn label counts towards the text budget, in tree order, and is cut past it", () => {
    const t = tree(
      A.Box({
        children: [
          A.Pill({ tone: "good", label: "ab" }),
          A.Icon({ name: "check", label: "cd" }),
          A.Button({ key: "b", label: "ef", onPress: noop }),
          A.Input({ key: "i", label: "gh", placeholder: "ij", submitLabel: "kl", value: "mn" }),
          A.Select({ key: "s", label: "op", options: [{ value: "v1", label: "qr" }, { value: "v2", label: "st" }], onSelect: noop }),
          A.Page({ key: "p", path: "p.html", title: "uv" }),
          "wx",
        ],
      }),
      { limits: { treeTextChars: 19 } },
    );
    const [pill, icon, button, input, select, page, text] = t.children;
    expect([pill.props.label, icon.props.label, button.props.label]).toEqual(["ab", "cd", "ef"]);
    expect([input.props.label, input.props.placeholder, input.props.submitLabel, input.props.value]).toEqual(["gh", "ij", "kl", "mn"]);
    expect([select.props.label, select.props.options[0].label, select.props.options[1].label]).toEqual(["op", "qr", "s"]);
    expect(select.props.options.map((o: any) => o.value)).toEqual(["v1", "v2"]);
    expect([page.props.title, text]).toEqual(["", ""]);
  });

  test("a Page query is measured as the URL-encoded query string, at most 4 KiB", () => {
    const fits = "a".repeat(4096 - "q=".length);
    expect(wire(A.Page({ key: "p", path: "p.html", title: "t", query: { q: fits } })).ok).toBe(true);
    // 1,400 spaces are 1,400 bytes of JSON but 4,200 once encoded (%20 each, or + in a form: either is over 4 KiB of address).
    expect(reason(A.Page({ key: "p", path: "p.html", title: "t", query: { q: " é".repeat(700) } }))).toContain("4 KiB");
    expect(reason(A.Page({ key: "p", path: "p.html", title: "t", query: { q: fits + "a" } }))).toContain("4 KiB");
  });

  test("a Page on the wire names the mod whose folder it comes from, from the host's own record", () => {
    const inner = B.Page({ key: "p", path: "p.html", title: "t" });
    const t = tree(A.Box({ children: [inner, { type: "Page", props: { key: "q", path: "q.html", title: "u" } }] }));
    expect(t.children[0].mod).toBe("mod-b");
    expect(t.children[1].mod).toBe("mod-a");
    expect(t.children[0].props.mod).toBeUndefined();
  });

  test("a Box with no flexDirection is a row, so it may hold inline elements at an inline site", () => {
    expect(wire(A.Box({ children: [A.Pill({ tone: "good", label: "x" })] }), { site: "ToolUse" }).ok).toBe(true);
    expect(reason(A.Box({ flexDirection: "column", children: [] }), { site: "StatusChip" })).toContain("row");
  });
});
