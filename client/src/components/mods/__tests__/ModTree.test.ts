import { mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h, type DefineComponent } from "vue";
import { readFileSync } from "node:fs";
import type { ModAction, ModRenderSite, ModWireElement } from "@/lib/mods/types";
import { MOD_COLOR_ROLES, MOD_ICON_NAMES, MOD_TONES } from "@/lib/mods/types";

const validate = vi.hoisted(() => vi.fn());
vi.mock("@/lib/mods/validate", () => ({ validateModTree: validate }));

const { default: ModTreeComponent } = await import("@/components/mods/ModTree.vue");
// CI's @vue/test-utils typings reject components with typed slots and emits in mount(); test through the props.
const ModTree = ModTreeComponent as unknown as DefineComponent<{
  tree: unknown;
  site: ModRenderSite;
  sessionId?: string;
}>;

/** The declarations of the first rule for `selector` in a component's `<style>`: jsdom can't lay out, so CSS is read as written. */
function cssRule(component: string, selector: string): Record<string, string> {
  const source = readFileSync(`${process.cwd()}/src/components/mods/${component}.vue`, "utf8");
  const style = source.slice(source.indexOf("<style"));
  const rule = new RegExp(`(?:^|\\n)${selector.replace(/[.[\]="]/g, "\\$&")}\\s*\\{([^}]*)\\}`).exec(style);
  if (!rule) throw new Error(`no rule for ${selector} in ${component}`);
  return Object.fromEntries(rule[1].replace(/\/\*[^]*?\*\//g, "").split(";").map((d) => d.trim()).filter(Boolean).map((d) => {
    const at = d.indexOf(":");
    return [d.slice(0, at).trim(), d.slice(at + 1).trim()];
  }));
}

type Tree = ModWireElement | string;
const box = (props: Record<string, unknown>, ...children: Tree[]): ModWireElement => ({ type: "Box", props, children } as ModWireElement);
const text = (props: Record<string, unknown>, ...children: Tree[]): ModWireElement => ({ type: "Text", props, children } as ModWireElement);
const leaf = (type: string, props: Record<string, unknown>): ModWireElement => ({ type, props } as ModWireElement);
const control = (type: string, props: Record<string, unknown>, handles: Record<string, string>): ModWireElement =>
  ({ type, props, handles } as ModWireElement);

function show(tree: unknown, options: { site?: ModRenderSite; sessionId?: string; fleet?: boolean } = {}) {
  validate.mockImplementation((t: unknown) => ({ ok: true, tree: t, textCut: false }));
  return mount(ModTree, {
    props: { tree, site: options.site ?? "Pane", sessionId: options.sessionId },
    slots: options.fleet === false ? {} : { fleet: () => h("div", { class: "fleet-own" }, "Fleet draws") },
  });
}

const actions = (wrapper: ReturnType<typeof show>) => (wrapper.emitted("action") ?? []).map((e) => e[0] as ModAction);

beforeEach(() => validate.mockReset());
afterEach(() => vi.useRealTimers());

describe("ModTree root", () => {
  it("draws nothing for a null tree", () => {
    const wrapper = show(null);
    expect(wrapper.find(".mod-tree").exists()).toBe(true);
    expect(wrapper.find(".mod-tree").element.children.length).toBe(0);
    expect(wrapper.find(".fleet-own").exists()).toBe(false);
  });

  it("validates the tree for its site", () => {
    const tree = text({}, "hi");
    show(tree, { site: "StatusChip" });
    expect(validate).toHaveBeenCalledWith(tree, "StatusChip");
  });

  it("draws only the fleet slot, and says why once, when the tree is invalid", async () => {
    validate.mockReturnValue({ ok: false, reason: "Box.key is not allowed" });
    const wrapper = mount(ModTree, {
      props: { tree: text({}, "x"), site: "Pane" },
      slots: { fleet: () => h("div", { class: "fleet-own" }, "Fleet draws") },
    });
    expect(wrapper.find(".fleet-own").exists()).toBe(true);
    expect(wrapper.find(".mod-text").exists()).toBe(false);
    expect(wrapper.emitted("invalid")).toEqual([["Box.key is not allowed"]]);
    await wrapper.setProps({ tree: text({}, "x") });
    expect(wrapper.emitted("invalid")).toHaveLength(2); // a new tree, a new report
    await wrapper.setProps({ site: "Pane" });
    expect(wrapper.emitted("invalid")).toHaveLength(2);
  });

  it("draws the tree the validator returns (text cut), not the one it was given", () => {
    validate.mockReturnValue({ ok: true, tree: text({}, "short"), textCut: true });
    const wrapper = mount(ModTree, { props: { tree: text({}, "a very long text"), site: "Pane" } });
    expect(wrapper.text()).toBe("short");
  });

  it("draws the fleet slot for a Fleet node, at the root and inside a Box", () => {
    expect(show({ type: "Fleet" }).find(".fleet-own").exists()).toBe(true);
    const inBox = show(box({}, text({}, "above"), { type: "Fleet" } as ModWireElement));
    expect(inBox.find(".mod-box .fleet-own").exists()).toBe(true);
    expect(inBox.text()).toContain("above");
  });

  it("draws nothing for a Fleet node with no slot", () => {
    const wrapper = show(box({}, { type: "Fleet" } as ModWireElement), { fleet: false });
    expect(wrapper.find(".fleet-own").exists()).toBe(false);
    expect(wrapper.find(".mod-box").element.children.length).toBe(0);
  });

  it("is inline at ToolUse and StatusChip, block elsewhere", () => {
    for (const site of ["ToolUse", "StatusChip"] as const) {
      expect(show(text({}, "x"), { site }).get(".mod-tree").classes()).toContain("mod-tree--inline");
    }
    for (const site of ["ToolResult", "ComposerBand", "Pane"] as const) {
      expect(show(text({}, "x"), { site }).get(".mod-tree").classes()).not.toContain("mod-tree--inline");
    }
  });
});

describe("Box", () => {
  it("draws strings as text and nests children", () => {
    const wrapper = show(box({}, "plain", box({}, text({}, "deep"))));
    expect(wrapper.get(".mod-box").text()).toContain("plain");
    expect(wrapper.get(".mod-box .mod-box .mod-text").text()).toBe("deep");
  });

  it("lays out with direction, gap, padding in 4 px steps, alignment, wrap and grow", () => {
    const el = show(box({
      flexDirection: "column", gap: 2, padding: 1, paddingX: 3, paddingY: 4,
      alignItems: "start", justifyContent: "space-between", flexWrap: "wrap", flexGrow: 1, width: "50%",
    })).get(".mod-box").element as HTMLElement;
    const style = el.getAttribute("style")!;
    expect(style).toContain("flex-direction: column");
    expect(style).toContain("gap: 8px");
    // padding 1 is the base; paddingX and paddingY override their sides
    expect([el.style.paddingTop, el.style.paddingRight, el.style.paddingBottom, el.style.paddingLeft]).toEqual(["16px", "12px", "16px", "12px"]);
    expect(style).toContain("align-items: flex-start");
    expect(style).toContain("justify-content: space-between");
    expect(style).toContain("flex-wrap: wrap");
    expect(style).toContain("flex-grow: 1");
    expect(style).toContain("width: 50%");
  });

  it("keeps padding on every side when only padding is given, and the other sides when only paddingX is", () => {
    const sides = (el: HTMLElement) => [el.style.paddingTop, el.style.paddingRight, el.style.paddingBottom, el.style.paddingLeft];
    expect(sides(show(box({ padding: 3 })).get(".mod-box").element as HTMLElement)).toEqual(["12px", "12px", "12px", "12px"]);
    expect(sides(show(box({ padding: 1, paddingX: 3 })).get(".mod-box").element as HTMLElement)).toEqual(["4px", "12px", "4px", "12px"]);
  });

  it.each([
    ["start", "flex-start"], ["center", "center"], ["end", "flex-end"], ["stretch", "stretch"], ["baseline", "baseline"],
  ])("alignItems %s", (value, css) => {
    expect(show(box({ alignItems: value })).get(".mod-box").attributes("style")).toContain(`align-items: ${css}`);
  });

  it.each([["start", "flex-start"], ["center", "center"], ["end", "flex-end"], ["space-between", "space-between"]])(
    "justifyContent %s", (value, css) => {
      expect(show(box({ justifyContent: value })).get(".mod-box").attributes("style")).toContain(`justify-content: ${css}`);
    },
  );

  it("takes its width as a share of the parent, never past it", () => {
    const wrapper = show(box({ width: "40%" }));
    expect(wrapper.get(".mod-box").attributes("style")).toContain("width: 40%");
    // never past the parent, and not a flex-basis: in a column parent that would size the height
    expect(wrapper.get(".mod-box").attributes("style")).toContain("max-width: 100%");
    expect(wrapper.get(".mod-box").attributes("style")).not.toContain("flex-basis");
    expect(cssRule("ModBox", ".mod-box")).toMatchObject({ "min-width": "0", "max-width": "100%" });
  });

  it("sets no width, max-width or flex-basis without a width", () => {
    const style = show(box({ gap: 1 })).get(".mod-box").attributes("style");
    expect(style).not.toMatch(/width|flex-basis/);
  });

  it("does not turn a column child's width into its height", () => {
    const wrapper = show(box({ flexDirection: "column" }, box({ width: "50%" }, "x")));
    const child = wrapper.findAll(".mod-box")[1].attributes("style")!;
    expect(child).toContain("width: 50%");
    expect(child).not.toContain("flex-basis");
  });

  it("draws a single border square and a round one with the card radius", () => {
    expect(cssRule("ModBox", '.mod-box[data-border="single"]')["border-radius"]).toBe("0");
    expect(cssRule("ModBox", '.mod-box[data-border="round"]')["border-radius"]).toBe("var(--radius-card)");
    expect(cssRule("ModBox", '.mod-box[data-border="single"]').border).toContain("1px solid");
  });

  it.each(["round", "single", "dashed", "quote"])("borderStyle %s", (borderStyle) => {
    expect(show(box({ borderStyle })).get(".mod-box").attributes("data-border")).toBe(borderStyle);
  });

  it("colours the border and the tint wash by role", () => {
    const wrapper = show(box({ borderStyle: "round", borderColor: "bad", background: "tint" }));
    const box_ = wrapper.get(".mod-box");
    expect(box_.attributes("style")).toContain("--mod-edge: var(--error)");
    expect(box_.attributes("data-background")).toBe("tint");
  });

  it("draws the subtle background", () => {
    expect(show(box({ background: "subtle" })).get(".mod-box").attributes("data-background")).toBe("subtle");
  });

  it("has no border or background attributes by default", () => {
    const el = show(box({})).get(".mod-box");
    expect(el.attributes("data-border")).toBeUndefined();
    expect(el.attributes("data-background")).toBeUndefined();
  });
});

describe("Text", () => {
  it.each(MOD_COLOR_ROLES)("colours %s", (color) => {
    const style = show(text({ color }, "x")).get(".mod-text").attributes("style");
    expect(style).toContain(`color: var(--${{ text: "text", muted: "muted", accent: "accent", good: "running", warn: "idle", bad: "error" }[color]})`);
  });

  it("styles bold, italic, strikethrough, code and dim", () => {
    const el = show(text({ bold: true, italic: true, strikethrough: true, code: true, dimColor: true }, "x")).get(".mod-text");
    expect(el.classes()).toEqual(expect.arrayContaining([
      "mod-text--bold", "mod-text--italic", "mod-text--strike", "mod-text--code",
    ]));
    expect(el.attributes("style")).toContain("color: var(--muted)");
  });

  it("an explicit colour beats dimColor", () => {
    expect(show(text({ color: "good", dimColor: true }, "x")).get(".mod-text").attributes("style")).toContain("var(--running)");
  });

  it("wraps by default and truncates on request", () => {
    expect(show(text({}, "x")).get(".mod-text").classes()).not.toContain("mod-text--truncate");
    expect(show(text({ wrap: "truncate" }, "x")).get(".mod-text").classes()).toContain("mod-text--truncate");
    expect(show(text({ wrap: "wrap" }, "x")).get(".mod-text").classes()).not.toContain("mod-text--truncate");
  });

  it("nests Text", () => {
    const wrapper = show(text({}, "a ", text({ bold: true }, "b"), " c"));
    expect(wrapper.get(".mod-text").text()).toBe("a b c");
    expect(wrapper.get(".mod-text .mod-text").classes()).toContain("mod-text--bold");
  });
});

describe("Pill", () => {
  it.each(MOD_TONES)("tone %s", (tone) => {
    const el = show(leaf("Pill", { tone, label: "Ready" })).get(".mod-pill");
    expect(el.text()).toBe("Ready");
    expect(el.attributes("data-tone")).toBe(tone);
    expect(el.attributes("style")).toContain("--mod-tone: var(--");
  });

  it("draws its icon", () => {
    const wrapper = show(leaf("Pill", { tone: "good", label: "Pass", icon: "check" }));
    expect(wrapper.find(".mod-pill svg").exists()).toBe(true);
    expect(show(leaf("Pill", { tone: "good", label: "Pass" })).find(".mod-pill svg").exists()).toBe(false);
  });

  it("puts the label in an inner span that ellipsizes, and shrinks with its row", () => {
    const wrapper = show(leaf("Pill", { tone: "good", label: "A very long label indeed" }));
    expect(wrapper.get(".mod-pill > .mod-pill__label").text()).toBe("A very long label indeed");
    expect(cssRule("ModPill", ".mod-pill__label")).toMatchObject({
      "min-width": "0", overflow: "hidden", "text-overflow": "ellipsis", "white-space": "nowrap",
    });
    expect(cssRule("ModPill", ".mod-pill")).toMatchObject({ "min-width": "0", "max-width": "100%" });
    expect(cssRule("ModPill", ".mod-pill").display).toBe("inline-flex");
  });
});

describe("Icon", () => {
  it.each(MOD_ICON_NAMES)("draws %s", (name) => {
    const wrapper = show(leaf("Icon", { name }));
    expect(wrapper.find(".mod-icon svg").exists(), name).toBe(true);
    expect(wrapper.find(".mod-icon").attributes("data-icon")).toBe(name);
  });

  it("is decoration without a label", () => {
    const el = show(leaf("Icon", { name: "check" })).get(".mod-icon");
    expect(el.attributes("aria-hidden")).toBe("true");
    expect(el.attributes("role")).toBeUndefined();
  });

  it("is an image with a label", () => {
    const el = show(leaf("Icon", { name: "check", label: "Passed" })).get(".mod-icon");
    expect(el.attributes("role")).toBe("img");
    expect(el.attributes("aria-label")).toBe("Passed");
    expect(el.attributes("aria-hidden")).toBeUndefined();
  });

  it.each(MOD_COLOR_ROLES)("colours %s", (color) => {
    expect(show(leaf("Icon", { name: "dot", color })).get(".mod-icon").attributes("style")).toContain("color: var(--");
  });

  it("spins the loader", () => {
    expect(show(leaf("Icon", { name: "loader" })).get(".mod-icon").classes()).toContain("mod-icon--spin");
    expect(show(leaf("Icon", { name: "check" })).get(".mod-icon").classes()).not.toContain("mod-icon--spin");
  });
});

describe("Button", () => {
  const button = (props: Record<string, unknown> = {}) =>
    control("Button", { key: "go", label: "Run", ...props }, { onPress: "h1" });

  it("emits a press with its handle", async () => {
    const wrapper = show(button());
    await wrapper.get("button").trigger("click");
    expect(actions(wrapper)).toEqual([{ handle: "h1", kind: "press" }]);
  });

  it("does not bubble or follow, so a tool row does not toggle", async () => {
    validate.mockImplementation((t: unknown) => ({ ok: true, tree: t, textCut: false }));
    const mounted = mount(ModTree, { props: { tree: button(), site: "ToolUse" }, attachTo: document.body });
    const outer = vi.fn();
    mounted.element.parentElement!.addEventListener("click", outer);
    const event = new MouseEvent("click", { bubbles: true, cancelable: true });
    mounted.get("button").element.dispatchEvent(event);
    expect(outer).not.toHaveBeenCalled();
    expect(event.defaultPrevented).toBe(true);
    mounted.unmount();
  });

  it("does nothing when disabled", async () => {
    const wrapper = show(button({ disabled: true }));
    expect(wrapper.get("button").attributes("disabled")).toBeDefined();
    await wrapper.get("button").trigger("click");
    expect(actions(wrapper)).toEqual([]);
  });

  it("puts the label in an inner span that ellipsizes, and shrinks with its row", () => {
    const wrapper = show(button({ label: "A very long button label" }));
    expect(wrapper.get(".mod-button > .mod-button__label").text()).toBe("A very long button label");
    expect(cssRule("ModButton", ".mod-button__label")).toMatchObject({
      "min-width": "0", overflow: "hidden", "text-overflow": "ellipsis", "white-space": "nowrap",
    });
    expect(cssRule("ModButton", ".mod-button")).toMatchObject({ "min-width": "0", "max-width": "100%" });
  });

  it.each(["primary", "danger", "quiet"])("tone %s", (tone) => {
    expect(show(button({ tone })).get("button").classes()).toContain(`mod-button--${tone}`);
  });

  it("is quiet by default and shows label and icon", () => {
    const wrapper = show(button({ icon: "play" }));
    expect(wrapper.get("button").classes()).toContain("mod-button--quiet");
    expect(wrapper.get("button").text()).toBe("Run");
    expect(wrapper.find("button svg").exists()).toBe(true);
    expect(wrapper.get("button").attributes("type")).toBe("button");
  });
});

describe("Input", () => {
  const input = (props: Record<string, unknown> = {}, handles: Record<string, string> = { onSubmit: "sub" }) =>
    control("Input", { key: "q", ...props }, handles);

  it("labels the field with a real label, with a placeholder and value", () => {
    const wrapper = show(input({ label: "Filter", placeholder: "type", value: "abc" }));
    const field = wrapper.get("input");
    const label = wrapper.get("label");
    expect(label.text()).toBe("Filter");
    expect(label.attributes("for")).toBe(field.attributes("id"));
    expect(field.attributes("placeholder")).toBe("type");
    expect((field.element as HTMLInputElement).value).toBe("abc");
  });

  it("has no label element without a label", () => {
    expect(show(input()).find("label").exists()).toBe(false);
  });

  it("submits on Enter", async () => {
    const wrapper = show(input({ value: "a" }));
    await wrapper.get("input").setValue("hello");
    await wrapper.get("input").trigger("keydown", { key: "Enter" });
    expect(actions(wrapper)).toEqual([{ handle: "sub", kind: "submit", value: "hello" }]);
  });

  it("submits with the button, labelled Submit by default", async () => {
    const wrapper = show(input());
    expect(wrapper.get(".mod-input__submit").text()).toBe("Submit");
    await wrapper.get("input").setValue("x");
    await wrapper.get(".mod-input__submit").trigger("click");
    expect(actions(wrapper)).toEqual([{ handle: "sub", kind: "submit", value: "x" }]);
  });

  it("takes the submit label", () => {
    expect(show(input({ submitLabel: "Search" })).get(".mod-input__submit").text()).toBe("Search");
  });

  it("has no submit button or Enter action without onSubmit", async () => {
    const wrapper = show(input({}, { onInput: "inp" }));
    expect(wrapper.find(".mod-input__submit").exists()).toBe(false);
    await wrapper.get("input").trigger("keydown", { key: "Enter" });
    expect(actions(wrapper)).toEqual([]);
  });

  it("keeps the user's typing until the tree's value changes", async () => {
    validate.mockImplementation((t: unknown) => ({ ok: true, tree: t, textCut: false }));
    const wrapper = mount(ModTree, { props: { tree: input({ value: "one" }), site: "Pane" } });
    const field = () => wrapper.get("input").element as HTMLInputElement;
    await wrapper.get("input").setValue("typed");
    await wrapper.setProps({ tree: input({ value: "one" }) }); // same value, redrawn
    expect(field().value).toBe("typed");
    await wrapper.setProps({ tree: input({ value: "two" }) });
    expect(field().value).toBe("two");
  });

  describe("onInput", () => {
    beforeEach(() => vi.useFakeTimers());

    it("sends the first change at once and not before it is typed", async () => {
      const wrapper = show(input({}, { onInput: "inp" }));
      expect(actions(wrapper)).toEqual([]);
      await wrapper.get("input").setValue("a");
      expect(actions(wrapper)).toEqual([{ handle: "inp", kind: "input", value: "a" }]);
    });

    it("coalesces: 10 changes in a second send at most 4 in any second, and the last value always goes", async () => {
      const wrapper = show(input({}, { onInput: "inp" }));
      const times: number[] = [];
      let seen = 0;
      const note = () => {
        const sent = actions(wrapper);
        for (; seen < sent.length; seen++) times.push(Date.now());
      };
      for (let i = 1; i <= 10; i++) {
        await wrapper.get("input").setValue(`v${i}`);
        note();
        vi.advanceTimersByTime(100);
        note();
      }
      vi.advanceTimersByTime(1000);
      note();
      const sent = actions(wrapper);
      expect(sent.every((a) => a.kind === "input" && a.handle === "inp")).toBe(true);
      expect(sent.length).toBeGreaterThanOrEqual(2);
      expect(sent.at(-1)?.value).toBe("v10");
      // never more than 4 starting at any send, within a second of it
      for (const start of times) expect(times.filter((t) => t >= start && t < start + 1000).length).toBeLessThanOrEqual(4);
    });

    it("holds changes in the window and sends the latest when it ends", async () => {
      const wrapper = show(input({}, { onInput: "inp" }));
      await wrapper.get("input").setValue("a");
      await wrapper.get("input").setValue("b");
      await wrapper.get("input").setValue("c");
      expect(actions(wrapper).map((a) => a.value)).toEqual(["a"]);
      vi.advanceTimersByTime(249);
      expect(actions(wrapper).map((a) => a.value)).toEqual(["a"]);
      vi.advanceTimersByTime(1);
      expect(actions(wrapper).map((a) => a.value)).toEqual(["a", "c"]);
    });

    // `emitted()` is cleared on unmount, so these listen for the action instead.
    function mountInput(handles: Record<string, string> = { onInput: "inp" }) {
      validate.mockImplementation((t: unknown) => ({ ok: true, tree: t, textCut: false }));
      const sent: ModAction[] = [];
      const wrapper = mount(ModTree, {
        props: { tree: box({}, input({}, handles)), site: "Pane", onAction: (a: ModAction) => sent.push(a) } as never,
      });
      return { wrapper, values: () => sent.map((a) => a.value) };
    }

    it("clears its timer on unmount, and sends the last pending value first", async () => {
      const { wrapper, values } = mountInput();
      await wrapper.get("input").setValue("a");
      await wrapper.get("input").setValue("b");
      expect(values()).toEqual(["a"]);
      wrapper.unmount();
      expect(values()).toEqual(["a", "b"]);
      vi.advanceTimersByTime(1000);
      expect(vi.getTimerCount()).toBe(0);
      expect(values()).toEqual(["a", "b"]); // once
    });

    it("sends nothing on unmount when nothing is pending", async () => {
      const { wrapper, values } = mountInput();
      await wrapper.get("input").setValue("a");
      wrapper.unmount();
      expect(values()).toEqual(["a"]);
    });

    it("sends the pending value when a redraw removes the field", async () => {
      const { wrapper, values } = mountInput();
      await wrapper.get("input").setValue("a");
      await wrapper.get("input").setValue("last");
      await wrapper.setProps({ tree: box({}, text({}, "gone")) });
      expect(values()).toEqual(["a", "last"]);
    });
  });

  it("keeps room for a placeholder, yet shrinks below its container", () => {
    const rule = cssRule("ModInput", ".mod-input");
    expect(rule["min-width"]).toBe("min(10em, 100%)");
    expect(cssRule("ModInput", ".mod-input__field")).toMatchObject({ "min-width": "0", "text-overflow": "ellipsis" });
    expect(cssRule("ModInput", ".mod-input__row")["min-width"]).toBe("0");
  });
});

describe("Select", () => {
  const select = (props: Record<string, unknown> = {}) =>
    control("Select", { key: "s", options: [{ value: "a", label: "Alpha" }, { value: "b", label: "Beta" }], ...props }, { onSelect: "pick" });

  it("draws the options, label and value", () => {
    const wrapper = show(select({ label: "Pick", value: "b" }));
    expect(wrapper.get("label").text()).toBe("Pick");
    expect(wrapper.get("label").attributes("for")).toBe(wrapper.get("select").attributes("id"));
    expect(wrapper.findAll("option").map((o) => [o.attributes("value"), o.text()])).toEqual([["a", "Alpha"], ["b", "Beta"]]);
    expect((wrapper.get("select").element as HTMLSelectElement).value).toBe("b");
  });

  it("emits a select", async () => {
    const wrapper = show(select());
    await wrapper.get("select").setValue("b");
    expect(actions(wrapper)).toEqual([{ handle: "pick", kind: "select", value: "b" }]);
  });

  it("has no label element without a label", () => {
    expect(show(select()).find("label").exists()).toBe(false);
  });

  it("shrinks with its row", () => {
    expect(cssRule("ModSelect", ".mod-select")["min-width"]).toBe("0");
    expect(cssRule("ModSelect", ".mod-select__field")).toMatchObject({ "min-width": "0", "max-width": "100%" });
  });
});

describe("Markdown", () => {
  it("draws Fleet's conversation Markdown", () => {
    const wrapper = show(leaf("Markdown", { text: "# Title\n\nSome **bold** and `code`" }));
    expect(wrapper.get(".mod-markdown").classes()).toContain("md-content");
    expect(wrapper.get("h1").text()).toBe("Title");
    expect(wrapper.get("strong").text()).toBe("bold");
  });

  it("opens links in a new tab", () => {
    const wrapper = show(leaf("Markdown", { text: "[docs](https://example.com/a) and https://example.org/b" }));
    const links = wrapper.findAll("a");
    expect(links).toHaveLength(2);
    for (const a of links) {
      expect(a.attributes("target")).toBe("_blank");
      expect(a.attributes("rel")).toBe("noopener noreferrer");
    }
  });

  it("does not run HTML", () => {
    const wrapper = show(leaf("Markdown", { text: "<img src=x onerror=alert(1)>" }));
    expect(wrapper.find("img").exists()).toBe(false);
  });

  it("dims", () => {
    expect(show(leaf("Markdown", { text: "x", dimColor: true })).get(".mod-markdown").classes()).toContain("mod-markdown--dim");
    expect(show(leaf("Markdown", { text: "x" })).get(".mod-markdown").classes()).not.toContain("mod-markdown--dim");
  });
});

describe("Code", () => {
  it("draws source in Fleet's code block", () => {
    const wrapper = show(leaf("Code", { source: "const a = 1;\nlet b = 2;", language: "typescript" }));
    expect(wrapper.get(".mod-code pre.hljs code").text()).toContain("const a = 1;");
  });

  it("survives backtick fences inside the source", () => {
    const wrapper = show(leaf("Code", { source: "before\n```\ninner\n```\nafter" }));
    expect(wrapper.get(".mod-code pre code").text()).toContain("inner");
    expect(wrapper.get(".mod-code pre code").text()).toContain("after");
  });

  it("has a header with the path and start line", () => {
    const wrapper = show(leaf("Code", { source: "x", path: "src/app.ts", startLine: 12 }));
    expect(wrapper.get(".mod-code__head").text()).toBe("src/app.ts:12");
    expect(show(leaf("Code", { source: "x", path: "src/app.ts" })).get(".mod-code__head").text()).toBe("src/app.ts");
    expect(show(leaf("Code", { source: "x" })).find(".mod-code__head").exists()).toBe(false);
  });

  it("wraps by default and keeps lines whole on truncate", () => {
    expect(show(leaf("Code", { source: "x" })).get(".mod-code").attributes("data-wrap")).toBe("wrap");
    expect(show(leaf("Code", { source: "x", wrap: "truncate" })).get(".mod-code").attributes("data-wrap")).toBe("truncate");
  });

  it("draws a diff with Fleet's diff view", () => {
    const patch = "--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,2 @@\n keep\n-old\n+new\n";
    const wrapper = show(leaf("Code", { source: patch, format: "diff", path: "f.txt" }));
    const rows = wrapper.findAll("[data-testid='tool-card-diff-row']");
    expect(rows.map((r) => r.attributes("data-diff-type"))).toEqual(["context", "remove", "add"]);
    expect(wrapper.get(".mod-code__head").text()).toBe("f.txt");
    expect(wrapper.find("pre").exists()).toBe(false);
  });
});

describe("Page", () => {
  const page = (props: Record<string, unknown>, mod = "demo-mod@v3"): ModWireElement =>
    ({ type: "Page", props: { key: "p", ...props }, mod } as ModWireElement);

  it("draws the page through the conversation frame, at the mod's address under the session", () => {
    const wrapper = show(page({ path: "ui/report.html", title: "Report", query: { run: "1" } }), { sessionId: "ses_1" });
    const frame = wrapper.get("iframe");
    expect(frame.attributes("src")).toMatch(/\/api\/sessions\/ses_1\/mods\/demo-mod%40v3\/pages\/ui\/report\.html\?run=1$/);
    expect(frame.attributes("title")).toBe("Report");
    expect(frame.attributes("sandbox")).not.toContain("allow-same-origin");
    expect(frame.attributes("sandbox")).not.toContain("allow-popups-to-escape-sandbox");
  });

  it("takes the owner from the node, not from the props", () => {
    const wrapper = show(page({ path: "a.html", title: "A" }, "other-mod@draft:ses_2"), { sessionId: "s" });
    expect(wrapper.get("iframe").attributes("src")).toContain("/mods/other-mod%40draft%3Ases_2/pages/a.html");
  });

  it("has no open in a new tab control, but still opens full size", () => {
    const wrapper = show(page({ path: "a.html", title: "A" }), { sessionId: "s" });
    expect(wrapper.find("[title='Open in a new tab']").exists()).toBe(false);
    expect(wrapper.find("[aria-label='Open in a new tab']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='conversation-page-expand']").exists()).toBe(true);
  });

  it.each([
    ["dot-dot", "../../../api/sessions.html"],
    ["javascript", "javascript:alert(1).html"],
    ["leading slash", "/api/x.html"],
    ["backslash", "a\\b.html"],
    ["not html", "a.js"],
  ])("is a quiet placeholder for a %s path", (_name, path) => {
    const wrapper = show(page({ path, title: "Report" }), { sessionId: "s" });
    expect(wrapper.find("iframe").exists()).toBe(false);
    expect(wrapper.get(".mod-page--unavailable").text()).toBe("Report: Page unavailable");
  });

  it("is a quiet placeholder with the title when there is no session", () => {
    const wrapper = show(page({ path: "a.html", title: "Report" }));
    expect(wrapper.find("iframe").exists()).toBe(false);
    expect(wrapper.get(".mod-page--unavailable").text()).toBe("Report: Page unavailable");
  });

  it("is a quiet placeholder for a bad owner", () => {
    const wrapper = show(page({ path: "a.html", title: "Report" }, "../x@v1"), { sessionId: "s" });
    expect(wrapper.find("iframe").exists()).toBe(false);
  });
});

describe("inline sites wrap", () => {
  const source = readFileSync(`${process.cwd()}/src/components/mods/ModTree.vue`, "utf8");
  const style = source.slice(source.indexOf("<style"));
  const nested = box({ flexDirection: "row" }, box({ flexDirection: "row" }, leaf("Pill", { label: "a" }), leaf("Pill", { label: "b" })));

  it("styles every row Box under the inline root to wrap, items not to shrink, and keeps max-width and the label ellipsis", () => {
    expect(/\.mod-tree--inline :deep\(\.mod-box\[style\*="flex-direction: row"\]\) \{\s*flex-wrap: wrap;/.test(style)).toBe(true);
    const items = /\.mod-tree--inline :deep\(\.mod-pill\),\s*\.mod-tree--inline :deep\(\.mod-button\) \{([^}]*)\}/.exec(style)![1];
    expect(items).toContain("flex-shrink: 0");
    expect(items).toContain("max-width: 100%");
    expect(cssRule("ModPill", ".mod-pill__label")["text-overflow"]).toBe("ellipsis");
    expect(cssRule("ModButton", ".mod-button__label")["text-overflow"]).toBe("ellipsis");
  });

  it.each(["ToolUse", "StatusChip"] as const)("at %s a nested row Box is under the inline rule and sets no wrap of its own", (site) => {
    const wrapper = show(nested, { site });
    expect(wrapper.find(".mod-tree").classes()).toContain("mod-tree--inline");
    for (const el of wrapper.findAll(".mod-box")) {
      expect(el.attributes("style")).toContain("flex-direction: row");
      expect(el.attributes("style")).not.toContain("flex-wrap");
    }
  });

  it("at Pane the rule doesn't apply, and a Box wraps only when its own flexWrap says so", () => {
    const wrapper = show(box({ flexDirection: "row", flexWrap: "wrap" }, box({ flexDirection: "row" }, leaf("Pill", { label: "a" }))), { site: "Pane" });
    expect(wrapper.find(".mod-tree").classes()).not.toContain("mod-tree--inline");
    const [outer, inner] = wrapper.findAll(".mod-box");
    expect(outer.attributes("style")).toContain("flex-wrap: wrap");
    expect(inner.attributes("style")).not.toContain("flex-wrap");
  });
});
