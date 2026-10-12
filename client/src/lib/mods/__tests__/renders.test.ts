import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import PhoneToolRun from "@/components/phone/session/PhoneToolRun.vue";
import ToolCard from "@/components/session/ToolCard.vue";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";
import { foldMessages } from "@/lib/phone/fold-steps";
import { toolRowViews } from "@/lib/mods/points";
import { MAX_INPUT_BYTES, MAX_OUTPUT_CHARS, resetModRenders } from "@/lib/mods/renders";
import type { ModWireElement } from "@/lib/mods/types";

const { fetchMock, listeners } = vi.hoisted(() => ({
  fetchMock: vi.fn(),
  listeners: new Map<string, Set<(event: unknown) => void>>(),
}));
vi.mock("@/lib/api-client", async (importOriginal) => ({ ...(await importOriginal<object>()), apiFetchOn: fetchMock }));
vi.mock("@/composables/use-signalr-socket", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  onGlobalEvent: (_machine: unknown, topic: string, handler: (event: unknown) => void) => {
    if (!listeners.has(topic)) listeners.set(topic, new Set());
    listeners.get(topic)!.add(handler);
    return () => listeners.get(topic)?.delete(handler);
  },
}));

const text = (value: string) => ({ type: "Text", props: {}, children: [value] }) as unknown as ModWireElement;
const answer = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const emit = (topic: string, type: string, payload: unknown) => listeners.get(topic)?.forEach((handler) => handler({ type, payload }));

const BOTH = [{ site: "ToolUse", tools: ["bash"] }, { site: "ToolResult", tools: ["bash"] }];
let sitesBody: { status: number; body: unknown };
let renderAnswers: Array<unknown>;

const renders = () => fetchMock.mock.calls.filter(([, path]) => String(path).endsWith("/mods/render"));
const sent = () => renders().map(([, , init]) => JSON.parse((init as RequestInit).body as string));

const mounted: Array<{ unmount: () => void }> = [];

function card(props: Record<string, unknown> = {}) {
  const pinia = createPinia();
  setActivePinia(pinia);
  const wrapper = mount(ToolCard, {
    global: { plugins: [pinia] },
    props: { id: "p1", callId: "c1", sessionId: "s1", kind: "bash", title: "bun test", status: "Completed", output: "ok", input: { command: "bun test" }, ...props },
  });
  mounted.push(wrapper);
  return wrapper;
}

beforeEach(() => {
  resetModRenders();
  toolRowViews.clear();
  listeners.clear();
  sitesBody = { status: 200, body: { sites: BOTH } };
  renderAnswers = [];
  fetchMock.mockReset().mockImplementation(async (_machine: unknown, path: string) => {
    if (path.endsWith("/mods/sites")) return answer(sitesBody.body, sitesBody.status);
    const next = renderAnswers.length > 1 ? renderAnswers.shift() : renderAnswers[0];
    return answer(next ?? { tree: text("5 passed"), mods: [{ name: "test-chips", draft: false }] });
  });
});
afterEach(() => {
  mounted.splice(0).forEach((wrapper) => wrapper.unmount());
  toolRowViews.clear();
});

describe("mod tool row renders", () => {
  it("asks both hooked sites with the contract's props and draws the trees", async () => {
    const wrapper = card();
    await flushPromises();
    expect(sent().map((body) => body.site).sort()).toEqual(["ToolResult", "ToolUse"]);
    expect(sent()[0]).toMatchObject({
      requestId: "c1",
      props: { tool: "bash", rawTool: "bash", category: "shell", status: "completed", title: "bun test", input: { command: "bun test" }, inputTruncated: false, output: "ok", outputTruncated: false },
    });
    expect(toolRowViews.get("ToolUse:s1:c1")?.mods).toEqual([{ name: "test-chips", draft: false }]);
    expect(wrapper.get(".tool-header__mod").text()).toBe("5 passed");
    expect(wrapper.get(".tool-body").text()).toBe("5 passed");
  });

  it("maps Fleet's status words and sends the harness's tool name beside the canonical one", async () => {
    card({ kind: "Bash", status: "Stopped" });
    await flushPromises();
    expect(sent()[0].props).toMatchObject({ tool: "bash", rawTool: "Bash", status: "cancelled" });
  });

  it("never asks for a tool no mod hooks, or without a session or call", async () => {
    sitesBody.body = { sites: [{ site: "ToolUse", tools: ["read"] }] };
    card();
    card({ sessionId: undefined });
    card({ callId: undefined });
    await flushPromises();
    expect(renders()).toHaveLength(0);
  });

  it("asks only the site that is hooked, and any tool when tools is null", async () => {
    sitesBody.body = { sites: [{ site: "ToolResult", tools: null }] };
    card({ kind: "read" });
    await flushPromises();
    expect(sent().map((body) => body.site)).toEqual(["ToolResult"]);
  });

  it("asks nothing when Mods is off (404 sites), and does not retry", async () => {
    sitesBody = { status: 404, body: {} };
    card();
    card({ callId: "c2" });
    await flushPromises();
    expect(renders()).toHaveLength(0);
    expect(fetchMock.mock.calls.filter(([, path]) => String(path).endsWith("/mods/sites"))).toHaveLength(1);
  });

  it("shares one request between rows of a call", async () => {
    card();
    card();
    await flushPromises();
    expect(renders()).toHaveLength(2);
  });

  it("asks again when the status changes, not when only the output does", async () => {
    const wrapper = card({ status: "Running", output: "" });
    await flushPromises();
    expect(renders()).toHaveLength(2);
    await wrapper.setProps({ output: "ok\nok\n" });
    await flushPromises();
    expect(renders()).toHaveLength(2);
    await wrapper.setProps({ status: "Completed" });
    await flushPromises();
    expect(renders()).toHaveLength(4);
    expect(sent()[3].props).toMatchObject({ status: "completed", output: "ok\nok\n" });
  });

  it("removes a view when no mod draws any more, and keeps one a mod draws as nothing", async () => {
    const wrapper = card();
    await flushPromises();
    expect(toolRowViews.get("ToolUse:s1:c1")).toBeDefined();
    renderAnswers = [{ tree: null, mods: [] }];
    emit("session:s1", "mod.ui", { sessionId: "s1", site: null, requestId: null, mod: "test-chips" });
    await flushPromises();
    expect(toolRowViews.get("ToolUse:s1:c1")).toBeUndefined();
    expect(wrapper.find(".tool-header__mod").exists()).toBe(false);
    renderAnswers = [{ tree: null, mods: [{ name: "test-chips", draft: false }] }];
    emit("sessions", "mod.ui", { sessionId: null, site: null, requestId: null, mod: "test-chips" });
    await flushPromises();
    expect(toolRowViews.get("ToolUse:s1:c1")?.tree).toBeNull();
  });

  it("draws again on a mod's own mod.ui with no session, without asking for the sites again", async () => {
    card();
    await flushPromises();
    const sites = () => fetchMock.mock.calls.filter(([, path]) => String(path).endsWith("/mods/sites")).length;
    const before = { sites: sites(), renders: renders().length };
    emit("sessions", "mod.ui", { sessionId: null, site: null, requestId: null, mod: "test-chips" });
    await flushPromises();
    expect(renders().length).toBeGreaterThan(before.renders);
    expect(sites()).toBe(before.sites);
  });

  it("renders again on mod.ui for the session, for one call, and leaves other calls and sessions alone", async () => {
    card();
    card({ callId: "c2" });
    await flushPromises();
    expect(renders()).toHaveLength(4);
    emit("session:s1", "mod.ui", { sessionId: "s1", site: null, requestId: "c2", mod: "m" });
    await flushPromises();
    expect(renders()).toHaveLength(6);
    emit("session:s1", "mod.ui", { sessionId: "s1", site: null, requestId: null, mod: "m" });
    await flushPromises();
    expect(renders()).toHaveLength(10);
  });

  it("fetches the sites again on mods.changed and renders again", async () => {
    card();
    await flushPromises();
    sitesBody.body = { sites: [{ site: "ToolUse", tools: ["bash"] }] };
    emit("sessions", "mods.changed", { name: "test-chips", reason: "version", sessionId: null });
    await flushPromises();
    expect(fetchMock.mock.calls.filter(([, path]) => String(path).endsWith("/mods/sites"))).toHaveLength(2);
    expect(renders()).toHaveLength(3);
    // The site that is no longer hooked loses its view.
    expect(toolRowViews.get("ToolResult:s1:c1")).toBeUndefined();
  });

  it("asks for the sites again when Fleet's mods change under a row that nothing hooked yet", async () => {
    // The host hadn't loaded the sample when the row appeared; Fleet's mod.ui with no session follows the load.
    sitesBody.body = { sites: [] };
    card();
    await flushPromises();
    expect(renders()).toHaveLength(0);
    sitesBody.body = { sites: BOTH };
    emit("sessions", "mod.ui", { sessionId: null, site: null, requestId: null });
    await flushPromises();
    expect(renders()).toHaveLength(2);
    expect(toolRowViews.get("ToolUse:s1:c1")?.mods).toEqual([{ name: "test-chips", draft: false }]);
  });

  it("sends an input over 64 KiB as {} and the last 262,144 characters of a long output", async () => {
    const big = { command: "x".repeat(MAX_INPUT_BYTES + 1) };
    const output = `${"a".repeat(10)}${"b".repeat(MAX_OUTPUT_CHARS)}`;
    card({ input: big, output });
    await flushPromises();
    expect(sent()[0].props).toMatchObject({ input: {}, inputTruncated: true, outputTruncated: true });
    expect(sent()[0].props.output).toBe("b".repeat(MAX_OUTPUT_CHARS));
  });

  it("asks at most four at a time", async () => {
    const release: Array<() => void> = [];
    let active = 0;
    let peak = 0;
    fetchMock.mockImplementation(async (_machine: unknown, path: string) => {
      if (path.endsWith("/mods/sites")) return answer({ sites: [{ site: "ToolUse", tools: null }] });
      active += 1;
      peak = Math.max(peak, active);
      await new Promise<void>((resolve) => release.push(resolve));
      active -= 1;
      return answer({ tree: null, mods: [] });
    });
    for (let i = 0; i < 10; i++) card({ callId: `c${i}` });
    await flushPromises();
    while (release.length > 0) {
      release.shift()!();
      await flushPromises();
    }
    expect(fetchMock.mock.calls.filter(([, path]) => String(path).endsWith("/mods/render"))).toHaveLength(10);
    expect(peak).toBe(4);
  });

  it("asks for a phone row and shares the call's request with a desktop row", async () => {
    const part = { type: "tool", partId: "p1", callId: "c1", tool: "bash", state: { status: "completed", input: { command: "bun test" }, output: "ok" } } as AccumulatedToolPart;
    const message = { messageId: "m1", role: "assistant", parts: [part], createdAt: 1 } as unknown as AccumulatedMessage;
    const parts = foldMessages([message]).filter((block) => block.kind === "steps") as never[];
    mounted.push(mount(PhoneToolRun, { props: { parts, sessionId: "s1" } }));
    card();
    await flushPromises();
    expect(renders()).toHaveLength(2);
    expect(sent()[0].props).toMatchObject({ tool: "bash", status: "completed", input: { command: "bun test" } });
  });
});

describe("controls in a mod's tree", () => {
  const actions = () => fetchMock.mock.calls.filter(([, path]) => String(path).endsWith("/mods/action"));
  const pressed = (handled: unknown, status = 200) => {
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation(async (machine: unknown, path: string, init?: RequestInit) =>
      path.endsWith("/mods/action") ? answer(handled, status) : base(machine, path, init));
  };

  it("posts a used control to the server, with its surface", async () => {
    pressed({ handled: true });
    card();
    await flushPromises();
    toolRowViews.get("ToolResult:s1:c1")!.onAction!({ handle: "h1", kind: "press" }, "desktop");
    await flushPromises();
    expect(actions()).toHaveLength(1);
    expect(actions()[0][1]).toBe("/api/sessions/s1/mods/action");
    expect(JSON.parse((actions()[0][2] as RequestInit).body as string)).toEqual({ handle: "h1", kind: "press", surface: "desktop" });
    // The mod redraws through mod.ui; the client doesn't ask on its own.
    expect(renders()).toHaveLength(2);
  });

  it("draws the call's row again when the server can't deliver the action (an expired handle)", async () => {
    pressed({ handled: false });
    card();
    await flushPromises();
    toolRowViews.get("ToolUse:s1:c1")!.onAction!({ handle: "h1", kind: "input", value: "x" }, "phone");
    await flushPromises();
    expect(JSON.parse((actions()[0][2] as RequestInit).body as string)).toMatchObject({ kind: "input", value: "x", surface: "phone" });
    expect(renders()).toHaveLength(4);
  });

  it("draws again when the request itself fails", async () => {
    pressed({}, 500);
    card();
    await flushPromises();
    toolRowViews.get("ToolUse:s1:c1")!.onAction!({ handle: "h1", kind: "press" }, "desktop");
    await flushPromises();
    expect(renders()).toHaveLength(4);
  });
});
