import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";
import { MACHINE_TARGET, type MachineTarget } from "@/lib/machine-target";

const globalApi = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }));
vi.mock("@/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), api: globalApi }));

const { useSkills } = await import("@/composables/use-skills");
const { useTools } = await import("@/composables/use-tools");
const { useUpdateStatus } = await import("@/composables/use-update-status");
const { default: CredentialsSection } = await import("@/components/settings/CredentialsSection.vue");
const { useReleaseNotes } = await import("@/composables/use-release-notes");

function falcon() {
  const api = { GET: vi.fn().mockResolvedValue({ data: { skills: [], tools: [], releases: [], fetchedAt: null, error: null, status: "uptodate", currentVersion: "9.9.9" } }), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() };
  const target = { key: "falcon", connection: { id: "falcon" }, isLive: false, api } as unknown as MachineTarget;
  return { api, target };
}

describe("Settings pages ask the machine they show", () => {
  it("skills, tools, update status and release notes go to that machine, not the live one", async () => {
    const { api, target } = falcon();
    const Probe = defineComponent({
      setup() {
        void useSkills().fetchSkills();
        void useTools().fetchTools();
        void useReleaseNotes().load();
        useUpdateStatus();
        return () => null;
      },
    });
    mount(Probe, { global: { provide: { [MACHINE_TARGET as symbol]: () => target } } });
    await flushPromises();

    const asked = api.GET.mock.calls.map(([path]) => path);
    expect(asked).toEqual(expect.arrayContaining(["/api/skills", "/api/tools", "/api/update/releases", "/api/update/status"]));
    expect(globalApi.GET).not.toHaveBeenCalled();
  });

  it("API keys are read from and saved to that machine", async () => {
    const { api, target } = falcon();
    api.GET.mockResolvedValue({ data: [] });
    api.PUT.mockResolvedValue({});
    mount(CredentialsSection, { global: { provide: { [MACHINE_TARGET as symbol]: () => target } } });
    await flushPromises();

    expect(api.GET).toHaveBeenCalledWith("/api/credentials");
    expect(globalApi.GET).not.toHaveBeenCalled();
  });
});
