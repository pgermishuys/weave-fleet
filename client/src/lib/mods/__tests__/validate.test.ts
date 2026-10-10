import { describe, expect, it } from "vitest";
import {
  MOD_COLOR_ROLES,
  MOD_ELEMENT_TYPES,
  MOD_ICON_NAMES,
  MOD_LIMITS,
  MOD_SPACES,
  MOD_TONES,
  type ModRenderSite,
} from "@/lib/mods/types";
import { MOD_ELEMENT_PROPS, validateModTree } from "@/lib/mods/validate";

type Tree = Record<string, unknown>;

const box = (props: Tree = {}, children: unknown[] = []): Tree => ({ type: "Box", props, children });
const text = (...children: unknown[]): Tree => ({ type: "Text", props: {}, children });
const pill = (props: Tree = {}): Tree => ({ type: "Pill", props: { tone: "good", label: "ok", ...props } });
const icon = (props: Tree = {}): Tree => ({ type: "Icon", props: { name: "check", ...props } });
const button = (props: Tree = {}, handles: Tree = { onPress: "h1" }): Tree => ({
  type: "Button",
  props: { key: "run", label: "Run", ...props },
  handles,
});
const input = (props: Tree = {}, handles: Tree = {}): Tree => ({ type: "Input", props: { key: "q", ...props }, handles });
const select = (props: Tree = {}, handles: Tree = { onSelect: "h2" }): Tree => ({
  type: "Select",
  props: { key: "pick", options: [{ value: "a", label: "A" }], ...props },
  handles,
});
const markdown = (props: Tree = {}): Tree => ({ type: "Markdown", props: { text: "hi", ...props } });
const code = (props: Tree = {}): Tree => ({ type: "Code", props: { source: "x = 1", ...props } });
const page = (props: Tree = {}, mod: unknown = "demo-mod@v3"): Tree => ({
  type: "Page",
  props: { key: "p", path: "ui/a.html", title: "A", ...props },
  mod,
});

function ok(tree: unknown, site: ModRenderSite = "Pane") {
  const check = validateModTree(tree, site);
  expect(check).toMatchObject({ ok: true });
  return check as Extract<typeof check, { ok: true }>;
}

function bad(tree: unknown, site: ModRenderSite = "Pane"): string {
  const check = validateModTree(tree, site);
  expect(check.ok).toBe(false);
  return (check as Extract<typeof check, { ok: false }>).reason;
}

describe("validateModTree: shape", () => {
  it("accepts null", () => {
    expect(validateModTree(null, "Pane")).toEqual({ ok: true, tree: null, textCut: false });
  });

  it("returns the same tree when nothing is cut", () => {
    const tree = box({}, [text("a")]);
    expect(ok(tree).tree).toBe(tree);
    expect(ok(tree).textCut).toBe(false);
  });

  it("refuses things that are not nodes", () => {
    for (const value of [undefined, 3, "x", [], true]) expect(validateModTree(value, "Pane").ok).toBe(false);
    expect(bad({ props: {}, children: [] })).toContain("type");
    expect(bad({ type: "Table", props: {}, children: [] })).toContain('unknown element "Table"');
    expect(bad(box({}, [42]))).toContain("Box > child[0]");
    expect(bad(box({}, [null]))).toContain("Box > child[0]");
  });

  it("refuses unknown fields on a node and props that are not an object", () => {
    expect(bad({ ...box(), extra: 1 })).toContain('unknown field "extra"');
    expect(bad({ type: "Box", props: null, children: [] })).toContain("props");
    expect(bad({ type: "Box", props: [], children: [] })).toContain("props");
    expect(bad({ type: "Box", children: [] })).toContain("props");
  });

  it("accepts every element type", () => {
    const tree = box({}, [text("t"), pill(), icon(), button(), input(), select(), markdown(), code(), page()]);
    ok(tree);
    expect(MOD_ELEMENT_TYPES).toHaveLength(10);
  });

  it("names where a problem is", () => {
    expect(bad(box({}, ["a", text("x"), { ...text("y"), props: { colour: "good" } }]))).toBe(
      'Box > Text[2]: unknown prop "colour"',
    );
  });
});

describe("validateModTree: Fleet", () => {
  it("accepts a lone Fleet and one inside a Box", () => {
    ok({ type: "Fleet" });
    ok(box({}, [{ type: "Fleet" }, text("after")]));
  });

  it("refuses a Fleet that carries anything", () => {
    expect(bad({ type: "Fleet", props: {} })).toContain("Fleet");
    expect(bad(box({}, [{ type: "Fleet", children: [] }]))).toContain("Fleet");
  });

  it("refuses two Fleets in one tree", () => {
    expect(bad(box({}, [{ type: "Fleet" }, box({}, [{ type: "Fleet" }])]))).toContain("Fleet");
  });
});

describe("validateModTree: props", () => {
  it("accepts every allowed value of every enum", () => {
    for (const gap of MOD_SPACES) ok(box({ gap, padding: gap, paddingX: gap, paddingY: gap }));
    for (const color of MOD_COLOR_ROLES) {
      ok(box({ borderColor: color }));
      ok({ type: "Text", props: { color }, children: ["x"] });
      ok(icon({ color }));
    }
    for (const tone of MOD_TONES) ok(pill({ tone }));
    for (const name of MOD_ICON_NAMES) {
      ok(icon({ name }));
      ok(pill({ icon: name }));
      ok(button({ icon: name }));
    }
    for (const flexDirection of ["row", "column"]) ok(box({ flexDirection }));
    for (const alignItems of ["start", "center", "end", "stretch", "baseline"]) ok(box({ alignItems }));
    for (const justifyContent of ["start", "center", "end", "space-between"]) ok(box({ justifyContent }));
    for (const flexWrap of ["wrap", "nowrap"]) ok(box({ flexWrap }));
    for (const flexGrow of [0, 1]) ok(box({ flexGrow }));
    for (const borderStyle of ["round", "single", "dashed", "quote"]) ok(box({ borderStyle }));
    for (const background of ["subtle", "tint"]) ok(box({ background }));
    for (const wrap of ["wrap", "truncate"]) {
      ok({ type: "Text", props: { wrap }, children: [] });
      ok(code({ wrap }));
    }
    for (const tone of ["primary", "danger", "quiet"]) ok(button({ tone }));
    for (const format of ["source", "diff"]) ok(code({ format }));
  });

  it("refuses a value outside each enum", () => {
    const cases: [Tree, string][] = [
      [box({ gap: 5 }), "gap"],
      [box({ padding: "1" }), "padding"],
      [box({ paddingX: -1 }), "paddingX"],
      [box({ paddingY: 9 }), "paddingY"],
      [box({ borderColor: "red" }), "borderColor"],
      [box({ flexDirection: "diagonal" }), "flexDirection"],
      [box({ alignItems: "middle" }), "alignItems"],
      [box({ justifyContent: "space-around" }), "justifyContent"],
      [box({ flexWrap: "wrap-reverse" }), "flexWrap"],
      [box({ flexGrow: 2 }), "flexGrow"],
      [box({ borderStyle: "double" }), "borderStyle"],
      [box({ background: "red" }), "background"],
      [{ type: "Text", props: { color: "red" }, children: [] }, "color"],
      [{ type: "Text", props: { wrap: "clip" }, children: [] }, "wrap"],
      [pill({ tone: "loud" }), "tone"],
      [pill({ icon: "rocket" }), "icon"],
      [icon({ name: "rocket" }), "name"],
      [icon({ color: "pink" }), "color"],
      [button({ tone: "loud" }), "tone"],
      [button({ icon: "nope" }), "icon"],
      [code({ format: "patch" }), "format"],
      [code({ wrap: "clip" }), "wrap"],
    ];
    for (const [tree, name] of cases) expect(bad(tree), name).toContain(`"${name}"`);
  });

  it("checks booleans", () => {
    for (const prop of ["bold", "italic", "strikethrough", "code", "dimColor"]) {
      ok({ type: "Text", props: { [prop]: true }, children: [] });
      expect(bad({ type: "Text", props: { [prop]: "yes" }, children: [] })).toContain(`"${prop}"`);
    }
    ok(button({ disabled: true }));
    expect(bad(button({ disabled: 1 }))).toContain('"disabled"');
    ok(markdown({ dimColor: false }));
    expect(bad(markdown({ dimColor: "no" }))).toContain('"dimColor"');
  });

  it("checks Box width as a percentage from 0 to 100", () => {
    for (const width of ["0%", "50%", "33.3%", "100%"]) ok(box({ width }));
    for (const width of ["101%", "-5%", "50", "50px", "%", "5 %", 50, "1e2%", "100.5%"]) {
      expect(bad(box({ width })), String(width)).toContain('"width"');
    }
  });

  it("checks startLine as a positive integer", () => {
    ok(code({ startLine: 1 }));
    ok(code({ startLine: 120 }));
    for (const startLine of [0, -1, 1.5, "3", Number.NaN]) expect(bad(code({ startLine }))).toContain('"startLine"');
  });

  it("checks string props", () => {
    ok(code({ language: "ts", path: "a/b.ts" }));
    expect(bad(code({ language: 3 }))).toContain('"language"');
    expect(bad(code({ path: null }))).toContain('"path"');
    expect(bad(pill({ label: 4 }))).toContain('"label"');
    expect(bad(markdown({ text: ["a"] }))).toContain('"text"');
    expect(bad(icon({ label: 1 }))).toContain('"label"');
  });

  it("refuses props an element does not list", () => {
    for (const type of MOD_ELEMENT_TYPES) {
      const base = { Box: box(), Text: text(), Button: button(), Input: input(), Select: select(), Page: page() }[type as string] ?? { type };
      const tree = { ...base, props: { ...(base.props as Tree ?? {}), notAProp: 1 } };
      expect(bad(tree), type).toContain('unknown prop "notAProp"');
    }
    expect(bad(box({ children: ["a"] }, ["b"]))).toContain('unknown prop "children"');
    expect(bad(button({ onPress: "h" }))).toContain('unknown prop "onPress"');
    expect(bad(box({ colour: "good" }))).toContain('unknown prop "colour"');
  });

  it("refuses a prop that is undefined or a function", () => {
    expect(bad(box({ gap: undefined }))).toContain('"gap"');
    expect(bad(box({ gap: () => 1 }))).toContain('"gap"');
  });

  it("lists the props of every element", () => {
    expect(Object.keys(MOD_ELEMENT_PROPS).sort()).toEqual([...MOD_ELEMENT_TYPES].sort());
    expect(MOD_ELEMENT_PROPS.Button).toContain("label");
    expect(MOD_ELEMENT_PROPS.Button).not.toContain("onPress");
    expect(MOD_ELEMENT_PROPS.Box).not.toContain("children");
  });
});

describe("validateModTree: required props", () => {
  it("requires them", () => {
    const cases: [Tree, string][] = [
      [{ type: "Pill", props: { label: "a" } }, "tone"],
      [{ type: "Pill", props: { tone: "good" } }, "label"],
      [{ type: "Icon", props: {} }, "name"],
      [{ type: "Button", props: { key: "a" }, handles: { onPress: "h" } }, "label"],
      [{ type: "Select", props: { key: "a" }, handles: { onSelect: "h" } }, "options"],
      [{ type: "Markdown", props: {} }, "text"],
      [{ type: "Code", props: {} }, "source"],
      [{ type: "Page", props: { key: "p", title: "t" }, mod: "m@v1" }, "path"],
      [{ type: "Page", props: { key: "p", path: "a.html" }, mod: "m@v1" }, "title"],
    ];
    for (const [tree, name] of cases) expect(bad(tree), name).toContain(`missing "${name}"`);
  });

  it("requires a key on Button, Input, Select and Page", () => {
    expect(bad(box({}, [{ type: "Button", props: { label: "a" }, handles: { onPress: "h" } }]))).toBe("Box > Button[0]: missing key");
    expect(bad({ type: "Input", props: {}, handles: {} })).toBe("Input: missing key");
    expect(bad({ type: "Select", props: { options: [{ value: "a", label: "A" }] }, handles: { onSelect: "h" } })).toBe(
      "Select: missing key",
    );
    expect(bad({ type: "Page", props: { path: "a.html", title: "t" }, mod: "m@v1" })).toBe("Page: missing key");
  });
});

describe("validateModTree: Select", () => {
  const options = (n: number) => Array.from({ length: n }, (_, i) => ({ value: `v${i}`, label: `L${i}` }));

  it("takes 1 to 200 options", () => {
    ok(select({ options: options(1) }));
    ok(select({ options: options(MOD_LIMITS.selectOptionsMax) }));
    expect(bad(select({ options: [] }))).toContain("options");
    expect(bad(select({ options: options(MOD_LIMITS.selectOptionsMax + 1) }))).toContain("options");
  });

  it("checks each option and that values are unique", () => {
    expect(bad(select({ options: "a" }))).toContain("options");
    expect(bad(select({ options: [{ value: "a" }] }))).toContain("options");
    expect(bad(select({ options: [{ value: 1, label: "A" }] }))).toContain("options");
    expect(bad(select({ options: [{ value: "a", label: "A", extra: 1 }] }))).toContain("options");
    expect(bad(select({ options: [{ value: "a", label: "A" }, { value: "a", label: "B" }] }))).toContain('duplicate option value "a"');
  });

  it("type-checks value without requiring it to be an option", () => {
    ok(select({ value: "zzz" }));
    expect(bad(select({ value: 3 }))).toContain('"value"');
  });
});

describe("validateModTree: Page", () => {
  it("takes a query of strings up to 4096 bytes", () => {
    ok(page({ query: { a: "1", b: "two words" } }));
    ok(page({ query: {} }));
    expect(bad(page({ query: { a: 1 } }))).toContain('"query"');
    expect(bad(page({ query: "a=1" }))).toContain('"query"');
    expect(bad(page({ query: ["a"] }))).toContain('"query"');
  });

  it("measures the query as it is sent", () => {
    const fits = "a".repeat(MOD_LIMITS.pageQueryBytes - 2); // "q=" + value
    ok(page({ query: { q: fits } }));
    expect(bad(page({ query: { q: `${fits}a` } }))).toContain('"query"');
    // A space is "+" and a multi-byte character is several percent escapes, so the sent form is what counts.
    expect(bad(page({ query: { q: "é".repeat(700) } }))).toContain('"query"');
  });
});

describe("validateModTree: Page owner", () => {
  it("requires the owning mod's id as a top-level field: name@vN or name@draft:session", () => {
    ok(page({}, "a@v1"));
    ok(page({}, "a-1-b@v12"));
    ok(page({}, `${"a".repeat(64)}@v3`));
    ok(page({}, "demo@draft:ses_0123-abc"));
    ok(page({}, `demo@draft:${"s".repeat(128)}`));
    const bads = [
      undefined, "", 3, null, "demo", "a", "A@v1", "1a@v1", "-a@v1", "a_b@v1", "a/b@v1", "a..@v1", "a b@v1", "../x@v1",
      `${"a".repeat(65)}@v1`, "a@v0", "a@v01", "a@v", "a@1", "a@V1", "a@draft", "a@draft:", "a@draft:a/b", "a@draft:a b",
      `a@draft:${"s".repeat(129)}`, "a@v1/../b", "a@v1\n", "a@v1@v2", "@v1",
    ];
    for (const mod of bads) {
      expect(bad({ ...page(), mod }), String(mod)).toContain("mod");
    }
  });

  it("refuses a mod in props, and on any other node", () => {
    expect(bad(page({ mod: "other" }))).toContain('unknown prop "mod"');
    expect(bad({ ...pill(), mod: "demo-mod@v1" })).toContain('unknown field "mod"');
    expect(bad({ ...box(), mod: "demo-mod@v1" })).toContain('unknown field "mod"');
    expect(bad({ ...button(), mod: "demo-mod@v1" })).toContain('unknown field "mod"');
  });

  it("refuses a Page with children or handles", () => {
    expect(bad({ ...page(), children: [] })).toContain("takes no children");
    expect(bad({ ...page(), handles: {} })).toContain("takes no handles");
  });
});

describe("validateModTree: names that every object has", () => {
  const NAMES = ["valueOf", "hasOwnProperty", "__proto__", "isPrototypeOf", "constructor", "toString", "toLocaleString"];

  it.each(NAMES)("%s is an unknown prop, not a throw", (name) => {
    for (const type of MOD_ELEMENT_TYPES) {
      const tree = JSON.parse(`{"type":"${type}","props":{"${name}":"x"},"children":[],"handles":{},"mod":"m@v1"}`);
      let check: ReturnType<typeof validateModTree> | undefined;
      expect(() => { check = validateModTree(tree, "Pane"); }, `${type}.${name}`).not.toThrow();
      expect(check?.ok).toBe(false);
    }
    const pillWith = JSON.parse(`{"type":"Pill","props":{"tone":"good","label":"x","${name}":"y"}}`);
    expect(bad(pillWith)).toContain(`unknown prop "${name}"`);
  });

  it.each(NAMES)("%s is not an element type", (name) => {
    expect(bad(JSON.parse(`{"type":"${name}","props":{}}`))).toContain("unknown element");
    expect(bad(box({}, [JSON.parse(`{"type":"${name}","props":{}}`)]))).toContain("unknown element");
  });

  it.each(NAMES)("%s is not a handle", (name) => {
    const tree = JSON.parse(`{"type":"Button","props":{"key":"k","label":"x"},"handles":{"onPress":"h1","${name}":"h2"}}`);
    expect(bad(tree)).toContain(`unknown handle "${name}"`);
  });

  it("a handle or prop that only the prototype has is missing", () => {
    // `onPress` inherited from a prototype is not there; the tree must be plain, so this is refused as not an element.
    const inherited = Object.create({ onPress: "h1" });
    Object.assign(inherited, { type: "Button", props: { key: "k", label: "x" }, handles: Object.create({ onPress: "h1" }) });
    expect(validateModTree(inherited, "Pane").ok).toBe(false);
  });
});

describe("validateModTree: what the host and the browser agree on", () => {
  const filler = (n: number) => "a".repeat(n);

  it("counts a Button label, then the Markdown, against the 100,000", () => {
    const check = ok(box({}, [button({ label: "Go" }), markdown({ text: filler(100_000) })]));
    expect(check.textCut).toBe(true);
    const cut = check.tree as unknown as { children: { props: { label?: string; text?: string } }[] };
    expect(cut.children[0].props.label).toBe("Go");
    expect(cut.children[1].props.text).toHaveLength(100_000 - 2);
  });

  it("counts Select option labels, in tree order", () => {
    const check = ok(box({}, [
      select({ options: [{ value: "v", label: filler(60_000) }] }),
      markdown({ text: filler(60_000) }),
    ]));
    expect(check.textCut).toBe(true);
    const cut = check.tree as unknown as { children: { props: { text?: string } }[] };
    expect(cut.children[1].props.text).toHaveLength(40_000);
  });

  it("counts an Input's label, placeholder, submitLabel, then value, as the host does", () => {
    const check = ok(box({}, [
      input({ label: filler(40_000), placeholder: filler(40_000), submitLabel: filler(40_000), value: filler(40_000) }, { onSubmit: "h1" }),
      markdown({ text: "tail" }),
    ]));
    const cut = check.tree as unknown as { children: { props: Record<string, string> }[] };
    const p = cut.children[0].props;
    expect([p.label.length, p.placeholder.length, p.submitLabel.length, p.value.length]).toEqual([40_000, 40_000, 20_000, 0]);
    expect(cut.children[1].props.text).toBe("");
  });

  it("counts every Input text prop, Icon label and Page title", () => {
    const check = ok(box({}, [
      input({ label: filler(10), value: filler(20), placeholder: filler(30), submitLabel: filler(40) }, { onSubmit: "h1" }),
      icon({ label: filler(50) }),
      page({ title: filler(60) }),
      markdown({ text: filler(100_000) }),
    ]));
    const cut = check.tree as unknown as { children: { props: { text?: string } }[] };
    expect(cut.children[3].props.text).toHaveLength(100_000 - 210);
  });

  it("does not count the Select value, keys, paths or a Code language", () => {
    const check = ok(box({}, [
      select({ value: filler(500) }),
      code({ source: "x", language: filler(500), path: filler(500) }),
      markdown({ text: filler(99_998) }),
    ]));
    expect(check.textCut).toBe(false);
  });

  it("measures a Page query as the URL-encoded string", () => {
    // Slashes are percent-escaped (three bytes each as %2F), so 1366 of them are over 4096 as a query.
    ok(page({ query: { q: "/".repeat(1364) } })); // "q=" + 1364 * 3 = 4094
    expect(bad(page({ query: { q: "/".repeat(1366) } }))).toContain('"query"');
    // Non-ASCII: two bytes in UTF-8, six as escapes.
    ok(page({ query: { q: "é".repeat(681) } }));
    expect(bad(page({ query: { q: "é".repeat(683) } }))).toContain('"query"');
    // Control characters: three bytes each.
    ok(page({ query: { q: "\u0001".repeat(1364) } }));
    expect(bad(page({ query: { q: "\u0001".repeat(1366) } }))).toContain('"query"');
  });
});

describe("validateModTree: children", () => {
  it("gives Box and Text a children array", () => {
    expect(bad({ type: "Box", props: {} })).toContain("children");
    expect(bad({ type: "Box", props: {}, children: "a" })).toContain("children");
    expect(bad({ type: "Text", props: {} })).toContain("children");
  });

  it("lets a Box hold nodes and strings", () => {
    ok(box({}, ["a", text("b"), box(), pill()]));
  });

  it("lets a Text hold strings and Text nodes only", () => {
    ok(text("a", text("b", text("c"))));
    expect(bad(text(pill()))).toBe("Text > Pill[0]: a Text holds strings and Text only");
    expect(bad(text(box()))).toContain("Text > Box[0]");
    expect(bad(text({ type: "Fleet" }))).toContain("Text > Fleet[0]");
    expect(bad(text(5))).toContain("Text > child[0]");
  });

  it("refuses children or handles where an element has none", () => {
    for (const el of [pill(), icon(), markdown(), code(), page()]) {
      expect(bad({ ...el, children: [] })).toContain("children");
      expect(bad({ ...el, handles: {} })).toContain("handles");
    }
    expect(bad({ ...button(), children: [] })).toContain("children");
    expect(bad({ ...box(), handles: {} })).toContain("handles");
    expect(bad({ ...text("a"), handles: {} })).toContain("handles");
  });
});

describe("validateModTree: handles", () => {
  it("requires onPress on a Button and onSelect on a Select", () => {
    expect(bad(button({}, {}))).toContain('missing handle "onPress"');
    expect(bad({ type: "Button", props: { key: "a", label: "A" } })).toContain("handles");
    expect(bad(select({}, {}))).toContain('missing handle "onSelect"');
  });

  it("lets an Input have onSubmit, onInput, both or none", () => {
    ok(input({}, {}));
    ok(input({}, { onSubmit: "h" }));
    ok(input({}, { onInput: "h" }));
    ok(input({}, { onSubmit: "h", onInput: "g" }));
  });

  it("refuses handle names an element does not take", () => {
    expect(bad(button({}, { onPress: "h", onSubmit: "g" }))).toContain('unknown handle "onSubmit"');
    expect(bad(input({}, { onPress: "h" }))).toContain('unknown handle "onPress"');
    expect(bad(input({}, { onSelect: "h" }))).toContain('unknown handle "onSelect"');
    expect(bad(select({}, { onSelect: "h", onInput: "g" }))).toContain('unknown handle "onInput"');
  });

  it("wants non-empty string handle values", () => {
    for (const value of ["", 4, null, {}]) expect(bad(button({}, { onPress: value }))).toContain('handle "onPress"');
    expect(bad({ type: "Button", props: { key: "a", label: "A" }, handles: [] })).toContain("handles");
    expect(bad({ type: "Button", props: { key: "a", label: "A" }, handles: null })).toContain("handles");
  });
});

describe("validateModTree: keys", () => {
  it("accepts the allowed characters up to 64", () => {
    for (const key of ["a", "Run_1.x-y", "A".repeat(64), "0"]) ok(button({ key }));
  });

  it("refuses others", () => {
    for (const key of ["", "a b", "a/b", "é", "A".repeat(65), 3, null]) expect(bad(button({ key })), String(key)).toContain('"key"');
  });

  it("checks optional keys on Box and Markdown too", () => {
    ok(box({ key: "frame" }));
    ok(markdown({ key: "doc" }));
    expect(bad(box({ key: "no good" }))).toContain('"key"');
    expect(bad(markdown({ key: "" }))).toContain('"key"');
  });

  it("requires keys to be unique across all elements", () => {
    expect(bad(box({}, [button({ key: "run" }), button({ key: "run" })]))).toBe('duplicate key "run"');
    expect(bad(box({ key: "x" }, [markdown({ key: "x" })]))).toBe('duplicate key "x"');
    expect(bad(box({}, [input({ key: "k" }), select({ key: "k" })]))).toBe('duplicate key "k"');
    expect(bad(box({}, [page({ key: "k" }), button({ key: "k" })]))).toBe('duplicate key "k"');
    ok(box({}, [button({ key: "a" }), button({ key: "b" })]));
  });
});

describe("validateModTree: inline sites", () => {
  const inlineSites: ModRenderSite[] = ["ToolUse", "StatusChip"];
  const otherSites: ModRenderSite[] = ["ToolResult", "ComposerBand", "Pane"];

  it("accepts inline elements", () => {
    for (const site of inlineSites) {
      ok(text("a"), site);
      ok(pill(), site);
      ok(icon(), site);
      ok(button(), site);
      ok({ type: "Fleet" }, site);
      ok(box({ flexDirection: "row", gap: 1 }, [text("a"), pill(), icon(), button(), { type: "Fleet" }]), site);
      ok(box({ flexDirection: "row" }, [box({ flexDirection: "row" }, [pill()])]), site);
      ok(null, site);
    }
  });

  it("refuses the other elements", () => {
    for (const site of inlineSites) {
      expect(bad(input(), site)).toBe(`Input is not inline (${site} takes inline elements only)`);
      expect(bad(select(), site)).toContain("Select is not inline");
      expect(bad(markdown(), site)).toContain("Markdown is not inline");
      expect(bad(code(), site)).toContain("Code is not inline");
      expect(bad(page(), site)).toContain("Page is not inline");
      expect(bad(box({ flexDirection: "row" }, [input()]), site)).toBe(
        `Box > Input[0] is not inline (${site} takes inline elements only)`,
      );
    }
  });

  it("takes a Box with no direction as a row, and refuses only a column", () => {
    for (const site of inlineSites) {
      ok(box({}, [pill()]), site);
      ok(box({}, [box({}, [pill(), text("a")]), button()]), site);
      ok(box({ flexDirection: "row" }, [box({}, [pill()])]), site);
      expect(bad(box({ flexDirection: "column" }, [pill()]), site)).toContain("Box is not inline");
      expect(bad(box({}, [box({ flexDirection: "column" }, [pill()])]), site)).toContain("Box > Box[0] is not inline");
      expect(bad(box({}, [input()]), site)).toContain("Box > Input[0] is not inline");
    }
  });

  it("lets the other sites take everything", () => {
    for (const site of otherSites) {
      ok(box({}, [input(), select(), markdown(), code(), page(), box({ flexDirection: "column" })]), site);
    }
  });
});

describe("validateModTree: limits", () => {
  function nodes(n: number): Tree {
    // 1 box + (n - 1) pills.
    return box({}, Array.from({ length: n - 1 }, () => pill()));
  }

  function deep(levels: number): Tree {
    let tree: Tree = text("leaf");
    for (let i = 1; i < levels; i++) tree = box({}, [tree]);
    return tree;
  }

  it("allows 2000 elements and refuses 2001", () => {
    ok(nodes(MOD_LIMITS.treeNodes));
    expect(bad(nodes(MOD_LIMITS.treeNodes + 1))).toBe("tree has 2001 elements (limit 2000)");
  });

  it("counts Fleet and Text nodes as elements, not strings", () => {
    ok(box({}, Array.from({ length: 5000 }, () => "s")));
  });

  it("allows depth 32 (root is 1) and refuses 33", () => {
    ok(deep(MOD_LIMITS.treeDepth));
    expect(bad(deep(MOD_LIMITS.treeDepth + 1))).toBe("tree is 33 levels deep (limit 32)");
  });

  it("refuses a very deep tree without overflowing the stack", () => {
    expect(bad(deep(20_000))).toContain("levels deep");
  });

  it("allows 262,144 bytes of JSON and refuses one more", () => {
    // {"type":"Markdown","props":{"text":""}} plus the text.
    const base = new TextEncoder().encode(JSON.stringify(markdown({ text: "" }))).length;
    const fits = markdown({ text: "a".repeat(MOD_LIMITS.treeBytes - base) });
    expect(new TextEncoder().encode(JSON.stringify(fits)).length).toBe(MOD_LIMITS.treeBytes);
    // Over the text budget too? No: 262,144 chars is over 100,000, so the text is cut, not refused.
    expect(ok(fits).textCut).toBe(true);
    const over = markdown({ text: "a".repeat(MOD_LIMITS.treeBytes - base + 1) });
    expect(bad(over)).toBe("tree is 262145 bytes (limit 262144)");
  });

  it("counts bytes, not characters", () => {
    const base = new TextEncoder().encode(JSON.stringify(markdown({ text: "" }))).length;
    // 3 bytes each.
    const chars = Math.floor((MOD_LIMITS.treeBytes - base) / 3) + 1;
    expect(bad(markdown({ text: "€".repeat(chars) }))).toContain("bytes (limit 262144)");
  });

  it("refuses a tree that cannot be JSON", () => {
    const cyclic: Tree = box();
    (cyclic.children as unknown[]).push(cyclic);
    expect(validateModTree(cyclic, "Pane").ok).toBe(false);
    const big = { type: "Markdown", props: { text: BigInt(10) } };
    expect(validateModTree(big, "Pane").ok).toBe(false);
  });
});

describe("validateModTree: text cut", () => {
  const budget = MOD_LIMITS.treeTextChars;

  it("keeps exactly 100,000 characters", () => {
    const check = ok(markdown({ text: "a".repeat(budget) }));
    expect(check.textCut).toBe(false);
  });

  it("cuts at 100,001", () => {
    const tree = markdown({ text: "a".repeat(budget + 1) });
    const check = ok(tree);
    expect(check.textCut).toBe(true);
    expect(((check.tree as Tree).props as Tree).text).toBe("a".repeat(budget));
  });

  it("does not mutate its input when it cuts", () => {
    const tree = markdown({ text: "a".repeat(budget + 50) });
    const before = JSON.stringify(tree);
    const check = ok(tree);
    expect(check.tree).not.toBe(tree);
    expect(JSON.stringify(tree)).toBe(before);
  });

  it("cuts in document order and empties what follows", () => {
    const tree = box({}, ["a".repeat(60_000), text("b".repeat(60_000)), pill({ label: "late" })]);
    const check = ok(tree);
    expect(check.textCut).toBe(true);
    const root = check.tree as unknown as { children: Tree[] & string[] };
    expect(root.children[0]).toBe("a".repeat(60_000));
    expect(((root.children[1] as unknown as { children: string[] }).children[0])).toBe("b".repeat(40_000));
    expect(((root.children[2] as Tree).props as Tree).label).toBe("");
    expect(((root.children[2] as Tree).props as Tree).tone).toBe("good");
  });

  it("counts every drawn text", () => {
    const filler = markdown({ text: "x".repeat(budget - 5) });
    const cases: [Tree, (cut: Tree) => unknown][] = [
      [pill({ label: "123456789" }), (n) => (n.props as Tree).label],
      [button({ label: "123456789" }), (n) => (n.props as Tree).label],
      [input({ placeholder: "123456789" }), (n) => (n.props as Tree).placeholder],
      [input({ label: "123456789" }), (n) => (n.props as Tree).label],
      [code({ source: "123456789" }), (n) => (n.props as Tree).source],
      [page({ title: "123456789" }), (n) => (n.props as Tree).title],
      [select({ options: [{ value: "v", label: "123456789" }] }), (n) => ((n.props as Tree).options as Tree[])[0]!.label],
    ];
    for (const [el, read] of cases) {
      const check = ok(box({}, [filler, el]));
      expect(check.textCut).toBe(true);
      const cut = (check.tree as unknown as { children: Tree[] }).children[1]!;
      expect(read(cut), String(el.type)).toBe("12345");
    }
  });

  it("truncates the string where the budget runs out", () => {
    const check = ok(box({}, [markdown({ text: "x".repeat(budget - 5) }), pill({ label: "123456789" })]));
    const pillNode = (check.tree as unknown as { children: Tree[] }).children[1]!;
    expect((pillNode.props as Tree).label).toBe("12345");
  });

  it("keeps handles, keys and other props when it cuts", () => {
    const check = ok(box({}, [markdown({ text: "x".repeat(budget) }), button({ key: "go", label: "Go" })]));
    const cut = (check.tree as unknown as { children: Tree[] }).children[1]!;
    expect(cut).toEqual({ type: "Button", props: { key: "go", label: "" }, handles: { onPress: "h1" } });
  });
});
