import { flushPromises, mount } from "@vue/test-utils";
import { createPinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import ToolCard from "@/components/session/ToolCard.vue";
import ToolOutputBody from "@/components/session/ToolOutputBody.vue";

const ESC = "\x1b";

function numbered(count: number): string {
  return Array.from({ length: count }, (_, i) => `${ESC}[32m✓${ESC}[0m case ${i + 1}`).join("\n");
}

function shown(wrapper: ReturnType<typeof mount>): string[] {
  return wrapper.get("[data-testid='tool-card-output']").text().split("\n");
}

describe("ToolOutputBody", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("draws short output whole, coloured, with its line count", () => {
    const wrapper = mount(ToolOutputBody, { props: { output: `${ESC}[31mFAIL${ESC}[0m limiter\nok\n` } });

    expect(shown(wrapper)).toEqual(["FAIL limiter", "ok"]);
    expect(wrapper.get(".ansi-fg-red").text()).toBe("FAIL");
    expect(wrapper.get("[data-testid='tool-card-output-count']").text()).toBe("2 lines");
    expect(wrapper.find("[data-testid='tool-card-output-expand']").exists()).toBe(false);
    expect(wrapper.html()).not.toContain(ESC);
  });

  it("shows the last 12 lines until asked for the rest, then only the last 12 again", async () => {
    const wrapper = mount(ToolOutputBody, { props: { output: numbered(200) } });

    expect(shown(wrapper)).toHaveLength(12);
    expect(shown(wrapper)[0]).toBe("✓ case 189");
    expect(shown(wrapper).at(-1)).toBe("✓ case 200");
    expect(wrapper.get("[data-testid='tool-card-output-count']").text()).toBe("200 lines");

    const expand = wrapper.get("[data-testid='tool-card-output-expand']");
    expect(expand.text()).toBe("Show 188 earlier lines");
    await expand.trigger("click");

    expect(shown(wrapper)).toHaveLength(200);
    expect(wrapper.get("[data-testid='tool-card-output']").classes()).toContain("tool-output--expanded");
    expect(wrapper.find("[data-testid='tool-card-output-capped']").exists()).toBe(false);

    const collapse = wrapper.get("[data-testid='tool-card-output-collapse']");
    expect(collapse.text()).toBe("Show only the last 12 lines");
    await collapse.trigger("click");
    expect(shown(wrapper)).toHaveLength(12);
  });

  it("draws at most the last 2,000 lines when expanded, and says so", async () => {
    const wrapper = mount(ToolOutputBody, { props: { output: numbered(5000) } });

    expect(wrapper.get("[data-testid='tool-card-output-expand']").text()).toBe("Show 4,988 earlier lines");
    await wrapper.get("[data-testid='tool-card-output-expand']").trigger("click");

    expect(shown(wrapper)).toHaveLength(2000);
    expect(shown(wrapper)[0]).toBe("✓ case 3001");
    expect(wrapper.get("[data-testid='tool-card-output-capped']").text()).toBe(
      "Showing the last 2,000 lines of 5,000. Copy takes all of them.",
    );
  });

  it("copies the whole output as plain text, with no codes and only the last frame of a progress line", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const output = `${numbered(30)}\n${ESC}[33m[#  ] 10%\r[###] 100%${ESC}[0m\n`;
    const wrapper = mount(ToolOutputBody, { props: { output } });

    const copy = wrapper.get("[data-testid='tool-card-output-copy']");
    await copy.trigger("click");
    await flushPromises();

    const copied = writeText.mock.calls[0]?.[0] as string;
    expect(copied.split("\n")).toHaveLength(31);
    expect(copied.startsWith("✓ case 1\n")).toBe(true);
    expect(copied.endsWith("✓ case 30\n[###] 100%")).toBe(true);
    expect(copied).not.toContain(ESC);
    expect(copy.text()).toBe("Copied");
  });

  it("stays quiet when the clipboard refuses", async () => {
    vi.stubGlobal("navigator", { clipboard: { writeText: vi.fn().mockRejectedValue(new Error("denied")) } });
    const wrapper = mount(ToolOutputBody, { props: { output: "x" } });

    await wrapper.get("[data-testid='tool-card-output-copy']").trigger("click");
    await flushPromises();

    expect(wrapper.get("[data-testid='tool-card-output-copy']").text()).toBe("Copy");
  });
});

describe("ToolCard body", () => {
  it("draws no body while the row is closed, and the output once it opens", async () => {
    const wrapper = mount(ToolCard, {
      global: { plugins: [createPinia()] },
      props: { id: "t1", title: "bun test", kind: "bash", status: "Completed", output: numbered(40), initiallyCollapsed: true },
    });

    expect(wrapper.find("[data-testid='tool-card-body']").exists()).toBe(false);

    const details = wrapper.get("details").element as HTMLDetailsElement;
    details.open = true;
    await wrapper.get("details").trigger("toggle");

    expect(wrapper.find("[data-testid='tool-card-body']").exists()).toBe(true);
    expect(shown(wrapper)).toHaveLength(12);
    expect(wrapper.get("[data-testid='tool-card-output-expand']").text()).toBe("Show 28 earlier lines");
  });
});
