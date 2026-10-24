import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import AddPhonePanel from "@/components/settings/AddPhonePanel.vue";

function mountPanel(publicUrl: string | null) {
  return mount(AddPhonePanel, {
    props: { machineName: "hangar", publicUrl, addresses: [], requiresToken: true, port: 2113, deviceNames: [] },
  });
}

describe("AddPhonePanel: reach it from outside", () => {
  it("says any https address will do, that Fleet runs no relay, and links the docs", () => {
    const text = mountPanel("https://hangar.example.test").get("[data-testid='add-phone-outside']").text();

    expect(text).toContain("Reach it from outside your network");
    expect(text).toContain("https://hangar.example.test");
    expect(text).toContain("Any https:// address that reaches this computer");
    expect(text).toContain("Fleet runs no relay");
    expect(mountPanel(null).get("[data-testid='add-phone-outside'] a").attributes("href")).toBe("https://fleet.tryweave.io/docs/phone");
  });

  it("opens the address field from the help", async () => {
    const wrapper = mountPanel(null);

    await wrapper.get("[data-testid='add-phone-set-address']").trigger("click");

    expect(wrapper.find("[data-testid='add-phone-url']").exists()).toBe(true);
  });
});
