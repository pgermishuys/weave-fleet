import "fake-indexeddb/auto";
import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearCredentials, saveCredentials } from "@/lib/device-credentials";

const started: { machineId: string; baseUrl: string; token: string | null }[] = [];

vi.mock("@/lib/phone/machine-feed", () => ({
  MachineFeed: class {
    constructor(private readonly options: { target: { machineId: string; baseUrl: string; token: string | null }; onChange: (s: unknown) => void }) {}
    async start() {
      started.push(this.options.target);
      this.options.onChange({ status: "live", lastHeardAt: 1, sessions: [], asks: {}, error: null });
    }
    async stop() {}
    async refresh() {}
  },
}));

import { useInbox } from "../phone/use-inbox";

describe("useInbox", () => {
  let posts: { url: string; init: RequestInit }[];

  beforeEach(async () => {
    started.length = 0;
    posts = [];
    localStorage.clear();
    await clearCredentials();
    await saveCredentials({
      homeMachineId: "hangar",
      homeMachineName: "hangar",
      homeBaseUrl: "",
      deviceId: "d1",
      token: "fdt_home.1",
      grants: [{ machineId: "falcon", baseUrl: "https://falcon.ts.net", token: "fdt_falcon.1" }],
      pairedAt: "",
    });
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = String(input);
      if (url === "/api/machines") {
        return Response.json({ machines: [
          { id: "hangar", name: "hangar", baseUrl: "https://hangar.ts.net" },
          { id: "falcon", name: "falcon", baseUrl: "https://falcon.ts.net" },
          { id: "osprey", name: "osprey", baseUrl: "https://osprey.ts.net" },
        ] });
      }
      if (url.includes("/device-grant")) return Response.json({ error: "Can't reach osprey from hangar right now." }, { status: 502 });
      posts.push({ url, init });
      return new Response(null, { status: 204 });
    }));
  });

  afterEach(() => vi.unstubAllGlobals());

  async function mountInbox() {
    let api!: ReturnType<typeof useInbox>;
    mount(defineComponent({ setup: () => { api = useInbox(); return () => h("div"); } }));
    await flushPromises();
    await vi.waitFor(() => expect(api.loading.value).toBe(false));
    await flushPromises();
    return api;
  }

  it("opens a feed for home and each machine it has a key for, and says why not for the rest", async () => {
    const inbox = await mountInbox();

    expect(started).toEqual([
      { machineId: "hangar", baseUrl: "", token: null },
      { machineId: "falcon", baseUrl: "https://falcon.ts.net", token: "fdt_falcon.1" },
    ]);
    const osprey = inbox.machines.value.find((m) => m.id === "osprey");
    expect(osprey?.status).toBe("unreachable");
    expect(osprey?.problem).toBe("Can't reach osprey from hangar right now.");
    expect(inbox.machines.value.find((m) => m.id === "hangar")?.isHome).toBe(true);
  });

  it("answers on the machine that asked, with that machine's key", async () => {
    const inbox = await mountInbox();

    const outcome = await inbox.answerPermission("falcon", "s1", "p1", "once");

    expect(outcome.ok).toBe(true);
    expect(posts[0].url).toBe("https://falcon.ts.net/api/sessions/s1/permissions/p1");
    expect((posts[0].init.headers as Record<string, string>).Authorization).toBe("Bearer fdt_falcon.1");
    expect((await inbox.answerPermission("osprey", "s1", "p1", "once")).ok).toBe(false);
  });
});
