import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import PhoneMarkdown from "@/components/phone/session/PhoneMarkdown.vue";

describe("PhoneMarkdown folds", () => {
  const text = "<details>\n<summary>Why</summary>\nbody\n</details>\n\n```sql\nSELECT 1;\n```";

  it("draws a fold and a coloured SQL block", () => {
    const wrapper = mount(PhoneMarkdown, { props: { text } });

    expect(wrapper.find("details.md-details > summary").text()).toBe("Why");
    expect(wrapper.find("details.md-details").attributes("open")).toBeUndefined();
    expect(wrapper.find("pre.hljs .hljs-keyword").exists()).toBe(true);
  });

  it("keeps a fold the reader opened when the text is redrawn", async () => {
    const wrapper = mount(PhoneMarkdown, { props: { text }, attachTo: document.body });
    await wrapper.find("summary").trigger("click");
    await wrapper.setProps({ text: `${text}\n\nmore` });

    expect(wrapper.find("details.md-details").attributes("open")).toBeDefined();
    wrapper.unmount();
  });
});
