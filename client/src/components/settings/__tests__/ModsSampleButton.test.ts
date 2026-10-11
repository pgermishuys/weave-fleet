import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ModsSampleButton from "@/components/settings/ModsSampleButton.vue";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => ({ ...(await importOriginal<object>()), apiFetch: apiFetchMock }));

describe("ModsSampleButton", () => {
  beforeEach(() => apiFetchMock.mockReset());

  it("posts the sample route and says where to see it", async () => {
    apiFetchMock.mockResolvedValue(new Response("{}", { status: 200 }));
    const wrapper = mount(ModsSampleButton);
    await wrapper.get("[data-testid='mods-sample-button']").trigger("click");
    await flushPromises();
    expect(apiFetchMock).toHaveBeenCalledWith("/api/mods/samples/test-chips", { method: "POST" });
    expect(wrapper.get("[data-testid='mods-sample-message']").text()).toBe("Added. Run tests in any session to see it.");
  });

  it("shows the server's message when it refuses", async () => {
    apiFetchMock.mockResolvedValue(new Response(JSON.stringify({ error: "The mods runtime isn't ready." }), { status: 400 }));
    const wrapper = mount(ModsSampleButton);
    await wrapper.get("[data-testid='mods-sample-button']").trigger("click");
    await flushPromises();
    expect(wrapper.get("[data-testid='mods-sample-message']").text()).toBe("The mods runtime isn't ready.");
  });
});
