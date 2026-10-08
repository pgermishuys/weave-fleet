import "fake-indexeddb/auto";
import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearCredentials, saveCredentials } from "@/lib/device-credentials";

const started: { machineId: string; baseUrl: string; token: string | null }[] = [];
const hubs: unknown[] = [];
/** What each machine's feed reads, by machine id. */
const feedSessions: Record<string, unknown[]> = {};

vi.mock("@/lib/phone/inbox-feed", () => ({
  InboxFeed: class {
    constructor(private readonly options: {
      target: { machineId: string; baseUrl: string; token: string | null };
      onChange: (s: unknown) => void;
      createHub?: (url: string, token: string | null) => unknown;
    }) {}
    async start() {
      started.push(this.options.target);
      hubs.push(this.options.createHub?.(`${this.options.target.baseUrl}/hubs/session-events`, this.options.target.token));
      this.options.onChange({ status: "live", lastHeardAt: 1, sessions: feedSessions[this.options.target.machineId] ?? [], asks: {}, error: null });
    }
    async stop() {}
    async refresh() {}
  },
}));

// The machine's shared event hub, by the machine the feed asks for.
vi.mock("@/composables/use-signalr-socket", () => ({
  feedHubFor: (machine: { key: string; connection: { token: string } | null }) => ({ sharedHubOf: machine.key, token: machine.connection?.token ?? null }),
}));

import { inboxSession, useInbox } from "../phone/use-inbox";

describe("useInbox", () => {
  let posts: { url: string; init: RequestInit }[];

  beforeEach(async () => {
    started.length = 0;
    hubs.length = 0;
    for (const id of Object.keys(feedSessions)) delete feedSessions[id];
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
          { id: "falcon", name: "falcon", baseUrl: "https://falcon.ts.net", os: "macos" },
          { id: "osprey", name: "osprey", baseUrl: "https://osprey.ts.net" },
        ] });
      }
      if (url === "/api/machine") return Response.json({ id: "hangar", name: "hangar", os: "linux" });
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

  it("gives each machine's feed that machine's event hub, which a session opened in place shares", async () => {
    await mountInbox();

    expect(hubs).toEqual([{ sharedHubOf: "home", token: null }, { sharedHubOf: "falcon", token: "fdt_falcon.1" }]);
  });

  it("finds a session's row in its machine's feed", async () => {
    feedSessions.falcon = [{ session: { id: "s-falcon", title: "Hero image sizes" } }];
    const inbox = await mountInbox();

    expect(inboxSession(inbox, "falcon", "s-falcon")?.session.title).toBe("Hero image sizes");
    expect(inboxSession(inbox, "hangar", "s-falcon")).toBeNull();
    expect(inboxSession(null, "falcon", "s-falcon")).toBeNull();
  });

  it("knows each machine's operating system: home's from itself, the rest from home's list", async () => {
    const inbox = await mountInbox();

    expect(inbox.machines.value.map((m) => [m.id, m.os])).toEqual([["hangar", "linux"], ["falcon", "macos"], ["osprey", null]]);
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
