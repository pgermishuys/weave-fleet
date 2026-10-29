import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { HarnessInfo } from "@/api/client";
import PermissionsSection from "@/components/settings/PermissionsSection.vue";
import { forgetHarnessLists } from "@/composables/use-harnesses";
import { useSettingsNav } from "@/composables/use-settings-nav";

const { getMock, putMock, apiFetchMock } = vi.hoisted(() => ({ getMock: vi.fn(), putMock: vi.fn(), apiFetchMock: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

function harness(type: string, displayName: string, permissionModes: Record<"ask" | "edits" | "all", string> | null): HarnessInfo {
  return {
    type,
    displayName,
    available: true,
    userEnabled: true,
    state: "ready",
    capabilities: { supportsPermissionLevels: permissionModes !== null, supportsAgentBrowser: permissionModes !== null } as HarnessInfo["capabilities"],
    presentation: { order: 0, eyebrow: "CLI harness", description: "", icon: "terminal", permissionModes },
  };
}

/** What each harness says it's handed, as the server describes it; Pi can't take a level. */
const HARNESSES: HarnessInfo[] = [
  harness("opencode2", "OpenCode 2", {
    ask: "Session rules ask for everything but reading",
    edits: "Session rules ask for everything but reading and edits",
    all: "Session rules allow everything",
  }),
  harness("claude-code", "Claude Code", {
    ask: "--permission-mode default, asking Fleet",
    edits: "--permission-mode acceptEdits, asking Fleet",
    all: "--permission-mode bypassPermissions",
  }),
  harness("pi", "Pi", null),
];

async function mountSection(preferences: Record<string, string> = {}) {
  getMock.mockImplementation((path: string) =>
    Promise.resolve({ data: path === "/api/harnesses" ? HARNESSES : preferences, response: new Response(null, { status: 200 }) }));
  apiFetchMock.mockResolvedValue(new Response(JSON.stringify({ enabled: true, pages: "session", scripts: false }), { status: 200 }));
  putMock.mockResolvedValue({});
  const wrapper = mount(PermissionsSection);
  await flushPromises();
  return wrapper;
}

describe("PermissionsSection", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    forgetHarnessLists();
    getMock.mockReset();
    putMock.mockReset();
  });

  it("allows everything until a level is picked, as Fleet always has", async () => {
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid='permission-level-all']").attributes("aria-checked")).toBe("true");
    expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("Allow everything (default)");
  });

  it("saves the default level, and every harness on the default follows it", async () => {
    const wrapper = await mountSection();

    await wrapper.get("[data-testid='permission-level-ask']").trigger("click");

    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "PermissionLevel" } }, body: { value: "ask" } });
    expect(wrapper.get("[data-testid='permission-level-ask']").attributes("aria-checked")).toBe("true");
    expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("Ask before changes (default)");
    expect(wrapper.get("[data-testid='permission-handed-opencode2']").text()).toBe("Ask before changes (default)");
  });

  it("lists each harness read-only: its own level without 'default', and none for one that can't take a level", async () => {
    const wrapper = await mountSection({ PermissionLevel: "ask", "PermissionLevel.claude-code": "edits" });

    expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("Allow edits");
    expect(wrapper.get("[data-testid='permission-handed-opencode2']").text()).toBe("Ask before changes (default)");
    expect(wrapper.get("[data-testid='permission-handed-pi']").text()).toBe("Not available");
    expect(wrapper.find("select[aria-label$='permissions']").exists()).toBe(false);
  });

  it("links each harness to its tab, at What it may do", async () => {
    const wrapper = await mountSection();
    const nav = useSettingsNav();

    await wrapper.get("[data-testid='permission-change-claude-code']").trigger("click");

    expect(nav.activeSection.value).toBe("harnesses");
    expect(nav.activeHarness.value).toBe("claude-code");
    expect(nav.activeAnchor.value).toBe("may-do");
  });

  it("shows whether each harness's agents get the browser", async () => {
    const wrapper = await mountSection();

    const rows = wrapper.get("[data-testid='permissions-each-harness']").findAll(".permissions-row__browser");
    expect(rows.map((row) => row.text())).toEqual(["On · Change", "On · Change", "Off · Change"]);
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
