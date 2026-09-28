import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import PermissionsSection from "@/components/settings/PermissionsSection.vue";

const { getMock, putMock } = vi.hoisted(() => ({ getMock: vi.fn(), putMock: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));

async function mountSection(preferences: Record<string, string> = {}) {
  getMock.mockResolvedValue({ data: preferences });
  putMock.mockResolvedValue({});
  const wrapper = mount(PermissionsSection);
  await flushPromises();
  return wrapper;
}

describe("PermissionsSection", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
  });

  it("allows everything until a level is picked, as Fleet always has", async () => {
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid='permission-level-all']").attributes("aria-checked")).toBe("true");
    expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("--permission-mode bypassPermissions");
  });

  it("saves the default level, and every harness on the default follows it", async () => {
    const wrapper = await mountSection();

    await wrapper.get("[data-testid='permission-level-ask']").trigger("click");

    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "PermissionLevel" } }, body: { value: "ask" } });
    expect(wrapper.get("[data-testid='permission-level-ask']").attributes("aria-checked")).toBe("true");
    expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("--permission-mode default, asking Fleet");
    expect(wrapper.get("[data-testid='permission-handed-opencode2']").text()).toBe("Session rules ask for everything but reading");
  });

  it("gives one harness its own level, and back to the default clears it", async () => {
    const wrapper = await mountSection({ PermissionLevel: "ask" });
    const select = wrapper.get<HTMLSelectElement>("#permission-harness-claude-code");

    await select.setValue("edits");
    expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "PermissionLevel.claude-code" } }, body: { value: "edits" } });
    expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("--permission-mode acceptEdits, asking Fleet");

    await select.setValue("default");
    expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "PermissionLevel.claude-code" } }, body: { value: "" } });
    expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("--permission-mode default, asking Fleet");
  });

  it("keeps unattended runs asking about nothing unless told otherwise", async () => {
    const wrapper = await mountSection();
    const select = wrapper.get<HTMLSelectElement>("#permission-unattended");

    expect(select.element.value).toBe("all");
    await select.setValue("deny");

    expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "PermissionUnattended" } }, body: { value: "deny" } });
    expect(wrapper.text()).toContain("Anything that would ask is refused");
  });
});
