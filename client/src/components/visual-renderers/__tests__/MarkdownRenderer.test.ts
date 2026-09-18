import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import MarkdownRenderer from "@/components/visual-renderers/MarkdownRenderer.vue";

const { renderMermaidSvgMock } = vi.hoisted(() => ({ renderMermaidSvgMock: vi.fn() }));

vi.mock("@/lib/mermaid", () => ({ renderMermaidSvg: renderMermaidSvgMock }));

const doc = (diagram: string) => `# Flow\n\n\`\`\`mermaid\n${diagram}\n\`\`\`\n\n\`\`\`ts\nconst x = 1;\n\`\`\`\n`;

describe("MarkdownRenderer", () => {
  beforeEach(() => {
    renderMermaidSvgMock.mockReset();
  });

  it("draws a mermaid block as a diagram and leaves other code blocks alone", async () => {
    renderMermaidSvgMock.mockResolvedValue('<svg class="drawn"></svg>');

    const wrapper = mount(MarkdownRenderer, { props: { content: doc("graph TD; A-->B") } });
    await flushPromises();

    expect(renderMermaidSvgMock).toHaveBeenCalledWith("graph TD; A-->B\n");
    expect(wrapper.find(".md-mermaid.mermaid-diagram svg.drawn").exists()).toBe(true);
    expect(wrapper.find("pre.mermaid-source").exists()).toBe(false);
    expect(wrapper.find("pre code").text()).toContain("const x = 1;");
  });

  it("keeps the source when the diagram doesn't parse", async () => {
    renderMermaidSvgMock.mockRejectedValue(new Error("Parse error"));

    const wrapper = mount(MarkdownRenderer, { props: { content: doc("not a diagram") } });
    await flushPromises();

    expect(wrapper.find(".md-mermaid").exists()).toBe(false);
    expect(wrapper.get("pre.mermaid-source").text()).toBe("not a diagram");
  });

  it("draws the new diagram when the content changes", async () => {
    renderMermaidSvgMock.mockImplementation(async (source: string) => `<svg data-source="${source.trim()}"></svg>`);

    const wrapper = mount(MarkdownRenderer, { props: { content: doc("graph TD; A-->B") } });
    await flushPromises();
    await wrapper.setProps({ content: doc("graph TD; B-->C") });
    await flushPromises();

    expect(wrapper.findAll(".md-mermaid")).toHaveLength(1);
    expect(renderMermaidSvgMock).toHaveBeenLastCalledWith("graph TD; B-->C\n");
  });
});
