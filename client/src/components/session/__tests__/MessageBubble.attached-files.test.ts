import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import MessageBubble from "@/components/session/MessageBubble.vue";

function bubble(body: string, role: "user" | "assistant" = "user") {
  return mount(MessageBubble, {
    props: { author: "You", role, showIdentity: true, clusterPosition: "single", body },
  });
}

const NOTE = "Attached: .weave/attachments/3f9a2c1d/build-output.log (2,031 lines, 184 KB)\nRead it as needed.";

describe("MessageBubble with attached files", () => {
  it("shows the typed text, a chip for the file and the note under it", () => {
    const wrapper = bubble(`Why does it fail?\n\n${NOTE}`);

    expect(wrapper.get(".msg-body__content").text()).toBe("Why does it fail?");
    const chip = wrapper.get(".msg-file");
    expect(chip.text()).toContain("LOG");
    expect(chip.text()).toContain("build-output.log");
    expect(chip.text()).toContain("2,031 lines, 184 KB");
    expect(wrapper.get("[data-testid='message-files-note']").text())
      .toBe("Attached: .weave/attachments/3f9a2c1d/build-output.log (2,031 lines, 184 KB)");
  });

  it("opens the file, keeping the tab on Ctrl-click", async () => {
    const wrapper = bubble(NOTE);

    await wrapper.get(".msg-file").trigger("click");
    await wrapper.get(".msg-file").trigger("click", { ctrlKey: true });

    expect(wrapper.emitted("open-file")).toEqual([
      [".weave/attachments/3f9a2c1d/build-output.log", undefined, false],
      [".weave/attachments/3f9a2c1d/build-output.log", undefined, true],
    ]);
    expect(wrapper.find(".msg-body__content").exists()).toBe(false);
  });

  it("leaves the agent's replies alone", () => {
    expect(bubble(`Done.\n\n${NOTE}`, "assistant").find(".msg-file").exists()).toBe(false);
  });
});
