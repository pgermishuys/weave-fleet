import { mount, type VueWrapper } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import { defineComponent, h } from "vue";
import ContextMenuHint from "@/components/ui/context-menu/ContextMenuHint.vue";
import { provideMenuHint, provideNoMenuHint, useMenuRowHint } from "@/components/ui/context-menu/hint";

const Row = defineComponent({
  props: { hint: { type: String, default: undefined }, open: Boolean },
  setup(props) {
    const { onFocus, onBlur } = useMenuRowHint(() => props.hint, (row) => row.dataset.state === "open");
    return () => h("button", { "data-state": props.open ? "open" : "closed", onFocus, onBlur });
  },
});

const Submenu = defineComponent({
  setup(_, { slots }) {
    provideNoMenuHint();
    return () => h("div", slots.default?.());
  },
});

const Menu = defineComponent({
  setup() {
    provideMenuHint();
    return () => h("div", [
      h(Row, { hint: "A new session with a copy of this conversation.", class: "fork" }),
      h(Row, { class: "plain" }),
      h(Row, { hint: "Now in Api.", open: true, class: "move" }),
      h(Submenu, () => h(Row, { hint: "Inside the submenu", class: "inner" })),
      h(ContextMenuHint, null, () => "Api · main · OpenCode"),
    ]);
  },
});

function footer(wrapper: VueWrapper): string {
  return wrapper.get("[data-slot='context-menu-hint']").text();
}

describe("ContextMenuHint", () => {
  it("shows its own words until a row with a hint is highlighted, and again after", async () => {
    const wrapper = mount(Menu);
    expect(footer(wrapper)).toBe("Api · main · OpenCode");

    await wrapper.get(".fork").trigger("focus");
    expect(footer(wrapper)).toBe("A new session with a copy of this conversation.");

    await wrapper.get(".fork").trigger("blur");
    expect(footer(wrapper)).toBe("Api · main · OpenCode");
  });

  it("goes back to its own words on a row without a hint", async () => {
    const wrapper = mount(Menu);
    await wrapper.get(".fork").trigger("focus");
    await wrapper.get(".plain").trigger("focus");
    expect(footer(wrapper)).toBe("Api · main · OpenCode");
  });

  it("keeps a submenu row's words while its submenu is open, and ignores the submenu's rows", async () => {
    const wrapper = mount(Menu);
    await wrapper.get(".move").trigger("focus");
    await wrapper.get(".move").trigger("blur");
    await wrapper.get(".inner").trigger("focus");
    expect(footer(wrapper)).toBe("Now in Api.");
  });
});
