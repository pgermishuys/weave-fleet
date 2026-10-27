import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import MissionControlPage from "@/components/pages/MissionControlPage.vue";
import { saveMachines, setActiveMachine } from "@/lib/machines";

const navigate = vi.hoisted(() => vi.fn());
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));
vi.mock("@/composables/use-signalr-socket", () => ({
  feedHubFor: () => ({
    start: async () => { throw new Error("no hub"); },
    stop: async () => undefined,
    invoke: async () => undefined,
    on: () => undefined,
    onclose: () => undefined,
    state: "Disconnected",
  }),
}));

enableAutoUnmount(afterEach);

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function item(id: string, title: string, patch: Record<string, unknown> = {}) {
  return {
    session: { id, title, time: { created: 1, updated: Date.now() }, tags: [] },
    instanceId: "i", workspaceId: "w", workspaceDirectory: "/work/ledger-api", workspaceDisplayName: null, isolationStrategy: "existing",
    sessionStatus: "active", activityStatus: "busy", retentionStatus: "active", lifecycleStatus: "running", typedInstanceStatus: "running",
    isHidden: false, harnessType: "opencode2", projectName: "ledger-api", tags: [],
    ...patch,
  };
}

const questionMessage = {
  id: "m1", role: "assistant", timestamp: "2026-10-27T12:00:00Z", textContent: "",
  parts: [{
    type: "tool", kind: 1, toolName: "question", toolCallId: "call-q", state: 1,
    arguments: { questions: [{ header: "Retries", question: "Retry three times or five?", options: [{ label: "Three", description: "" }, { label: "Five", description: "" }] }] },
  }],
};

describe("Mission control page", () => {
  let enabled: boolean;
  let requests: string[];
  let permissionAnswered: boolean;
  let questionAnswered: boolean;

  beforeEach(() => {
    localStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    navigate.mockClear();
    enabled = true;
    permissionAnswered = false;
    questionAnswered = false;
    requests = [];
    saveMachines([{ id: "m2", name: "mini", baseUrl: "http://mini.test:5732", token: "mini-token", addedAt: "2026-10-27T08:00:00Z" }]);
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : String(input);
      requests.push(`${init?.method ?? "GET"} ${url}`);
      const path = url.replace(/^https?:\/\/[^/]+/, "").split("?")[0];
      const onMini = url.startsWith("http://mini.test");
      if (path === "/api/preferences") return json(enabled ? { "features.mission-control.enabled": "true" } : {});
      if (path === "/api/sessions") {
        return json(onMini
          ? [item("mini-q", "Rate limiter", questionAnswered ? {} : { sessionStatus: "waiting_input", activityStatus: "idle" })]
          : [item("atlas-w", "Index rebuild"), item("atlas-p", "Dependency bump", { sessionStatus: "waiting_input", activityStatus: "idle" })]);
      }
      if (path === "/api/sessions/now") return onMini ? json({}, 404) : json({ "atlas-w": { text: "bash · pnpm test webhooks", kind: "tool", at: "" } });
      if (path === "/api/sessions/mini-q/permissions") return json([]);
      if (path === "/api/sessions/mini-q/messages") return json({ messages: questionAnswered ? [] : [questionMessage] });
      if (path === "/api/sessions/atlas-p/permissions") {
        return json(permissionAnswered ? [] : [{ id: "perm1", sessionId: "atlas-p", kind: "shell", tool: "bash", title: "pnpm install --no-frozen-lockfile", always: [], askedAt: "2026-10-27T12:00:00Z" }]);
      }
      if (path === "/api/sessions/atlas-p/permissions/perm1") { permissionAnswered = true; return json({}); }
      if (path === "/api/sessions/mini-q/questions/call-q/answer") { questionAnswered = true; return json({}); }
      if (path.endsWith("/work")) return json([]);
      if (path.endsWith("/prompt")) return json({});
      if (path.endsWith("/messages")) return json({ messages: [] });
      if (path === "/api/harnesses") return json([{ type: "opencode2", capabilities: { supportsSteering: true } }]);
      return json({}, 404);
    }));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("with the switch off only says how to turn it on, and asks no machine for anything", async () => {
    enabled = false;
    const wrapper = mount(MissionControlPage);
    await flushPromises();

    expect(wrapper.find("[data-testid='mission-control']").exists()).toBe(false);
    expect(wrapper.text()).toContain("Mission control");
    expect(requests.filter((request) => !request.endsWith("/api/preferences"))).toEqual([]);
  });

  it("puts every machine's sessions on the wall with their now lines, and regroups them", async () => {
    const wrapper = mount(MissionControlPage);
    await flushPromises();

    const tiles = wrapper.findAll("[data-testid='mission-tile']");
    expect(tiles).toHaveLength(3);
    expect(tiles.map((tile) => tile.find("[data-testid='tile-machine']").text()).sort()).toEqual(["This machine", "This machine", "mini"]);
    expect(wrapper.find("[data-state='working'] [data-testid='tile-now']").text()).toBe("bash · pnpm test webhooks");
    // The old machine has no now line and no error: its tile shows the question.
    expect(wrapper.text()).toContain("Retry three times or five?");

    await wrapper.find("[data-testid='group-machine']").trigger("click");
    expect(wrapper.findAll("[data-testid='mission-group'] h3").map((heading) => heading.text().replace(/\s+\d+$/, "")).sort()).toEqual(["This machine", "mini"]);
  });

  it("answers a question and a permission in place, each on its own machine", async () => {
    const wrapper = mount(MissionControlPage);
    await flushPromises();
    const asks = wrapper.findAll("[data-testid='mission-ask']");
    expect(asks).toHaveLength(2);

    await asks.find((ask) => ask.text().includes("Retry three times"))!.findAll("button").find((button) => button.text() === "Five")!.trigger("click");
    await asks.find((ask) => ask.text().includes("pnpm install"))!.findAll("button").find((button) => button.text() === "Allow once")!.trigger("click");
    await flushPromises();

    expect(requests).toContain("POST http://mini.test:5732/api/sessions/mini-q/questions/call-q/answer");
    expect(requests).toContain("POST /api/sessions/atlas-p/permissions/perm1");
    expect(wrapper.findAll("[data-testid='mission-ask']")).toHaveLength(1);
  });

  it("peeks at a tile and sends a nudge to its machine, steering where the harness does", async () => {
    const wrapper = mount(MissionControlPage);
    await flushPromises();

    await wrapper.find("[data-state='working']").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid='mission-peek']").text()).toContain("Index rebuild");

    await wrapper.find("[data-testid='peek-nudge']").setValue("also cover the timeout path");
    await wrapper.find("form").trigger("submit");
    await flushPromises();
    await flushPromises();

    expect(requests).toContain("POST /api/sessions/atlas-w/prompt");
    expect(wrapper.find("[data-testid='peek-sent']").text()).toBe("Sent.");
  });
});
