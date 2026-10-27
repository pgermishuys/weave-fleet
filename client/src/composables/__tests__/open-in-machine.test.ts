import { describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { mount, flushPromises } from "@vue/test-utils";
import { MACHINE_TARGET, type MachineTarget } from "@/lib/machine-target";
import { useAvailableTools } from "@/composables/use-available-tools";
import { useKeyFiles } from "@/composables/use-key-files";

vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(async () => ({ data: { tools: [{ id: "home-editor", label: "Home editor", iconName: "x", category: "editor" }], filesByTool: { "home-editor": ["home.md"] } } })) },
  apiOnMachine: vi.fn(),
}));

function peerTarget(get: ReturnType<typeof vi.fn>): MachineTarget {
  return { key: "peer-1", connection: null, isLive: false, api: { GET: get } as never };
}

function mountWith(target: MachineTarget, setup: () => unknown) {
  return mount(defineComponent({ setup, render: () => h("div") }), { global: { provide: { [MACHINE_TARGET as symbol]: () => target } } });
}

describe("Open in on a session of another machine", () => {
  it("asks that machine which tools it has, not the live one", async () => {
    const { api } = await import("@/api/client");
    const get = vi.fn(async () => ({ data: { tools: [{ id: "peer-editor", label: "Peer editor", iconName: "x", category: "editor" }] } }));
    let tools: { value: readonly { id: string }[] } | undefined;
    mountWith(peerTarget(get), () => { tools = useAvailableTools().tools; return {}; });
    await flushPromises();
    expect(get).toHaveBeenCalledWith("/api/available-tools");
    expect(tools!.value.map((tool) => tool.id)).toEqual(["peer-editor"]);
    expect(api.GET).not.toHaveBeenCalledWith("/api/available-tools", expect.anything());
  });

  it("reads key files from that machine", async () => {
    const get = vi.fn(async () => ({ data: { filesByTool: { "peer-editor": ["peer.md"] } } }));
    let keyFiles: ReturnType<typeof useKeyFiles> | undefined;
    mountWith(peerTarget(get), () => { keyFiles = useKeyFiles(); return {}; });
    await keyFiles!.fetch("/srv/project");
    expect(get).toHaveBeenCalledWith("/api/key-files", { params: { query: { directory: "/srv/project" } } });
    expect(keyFiles!.filesByTool.value).toEqual({ "peer-editor": ["peer.md"] });
  });
});
