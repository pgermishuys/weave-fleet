import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, nextTick, reactive } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AccumulatedMessage } from "@/lib/client-types";
import type { ModDraft } from "@/lib/mods/kept";

const { store } = vi.hoisted(() => ({
  store: {} as {
    modsSwitch: unknown;
    isSwitchedOn: boolean;
    drafts: Record<string, unknown[]>;
    draftsFor: (id: string) => unknown[];
    loadDrafts: ReturnType<typeof import("vitest").vi.fn>;
    loadSwitch: ReturnType<typeof import("vitest").vi.fn>;
  },
}));

vi.mock("@/stores/mods", () => ({ useModsStore: () => store }));
vi.mock("@/components/mods/review/ModDraftCard.vue", () => ({
  default: defineComponent({ name: "ModDraftCard", props: ["draft", "phone"], setup: (p) => () => h("div", { "data-testid": "card", "data-phone": String(p.phone) }, (p.draft as ModDraft).name) }),
}));

import ModDraftCards from "@/components/mods/review/ModDraftCards.vue";

function draft(name: string): ModDraft {
  return { sessionId: "s1", name, description: "d", version: "1", off: null, kept: null };
}

function tool(tool: string, status = "completed", partId = tool + status): AccumulatedMessage {
  return { messageId: "m" + partId, sessionId: "s1", role: "assistant", parts: [{ partId, type: "tool", tool, callId: partId, state: { status } }] };
}

beforeEach(() => {
  store.modsSwitch = { on: true, safeMode: false };
  store.isSwitchedOn = true;
  store.drafts = reactive({ s1: [draft("test-chips"), draft("context-gauge")] });
  store.draftsFor = (id) => store.drafts[id] ?? [];
  store.loadDrafts = vi.fn().mockResolvedValue(undefined);
  store.loadSwitch = vi.fn().mockResolvedValue(undefined);
});

describe("ModDraftCards", () => {
  it("shows one card per draft of the session and loads them", async () => {
    const wrapper = mount(ModDraftCards, { props: { sessionId: "s1" } });
    await flushPromises();
    expect(wrapper.findAll("[data-testid=card]").map((card) => card.text())).toEqual(["test-chips", "context-gauge"]);
    expect(store.loadDrafts).toHaveBeenCalledWith("s1");
  });

  it("tells the cards when they're on the phone", async () => {
    const wrapper = mount(ModDraftCards, { props: { sessionId: "s1", phone: true } });
    await flushPromises();
    expect(wrapper.find("[data-testid=card]").attributes("data-phone")).toBe("true");
  });

  it("renders nothing with the Mods switch off, and doesn't load drafts", async () => {
    store.isSwitchedOn = false;
    const wrapper = mount(ModDraftCards, { props: { sessionId: "s1" } });
    await flushPromises();
    expect(wrapper.html()).toBe("<!--v-if-->");
    expect(store.loadDrafts).not.toHaveBeenCalled();
  });

  it("reads the switch when it hasn't been read", async () => {
    store.modsSwitch = null;
    mount(ModDraftCards, { props: { sessionId: "s1" } });
    await flushPromises();
    expect(store.loadSwitch).toHaveBeenCalled();
  });

  it("loads again for another session", async () => {
    const wrapper = mount(ModDraftCards, { props: { sessionId: "s1" } });
    await flushPromises();
    await wrapper.setProps({ sessionId: "s2" });
    expect(store.loadDrafts).toHaveBeenLastCalledWith("s2");
  });

  it("loads again when a fleet_mod_ tool call completes, not for other tools or running calls", async () => {
    const wrapper = mount(ModDraftCards, { props: { sessionId: "s1", messages: [tool("bash")] } });
    await flushPromises();
    expect(store.loadDrafts).toHaveBeenCalledTimes(1);

    await wrapper.setProps({ messages: [tool("bash"), tool("fleet_mod_write", "running", "p1")] });
    await nextTick();
    expect(store.loadDrafts).toHaveBeenCalledTimes(1);

    await wrapper.setProps({ messages: [tool("bash"), tool("fleet_mod_write", "completed", "p1")] });
    await flushPromises();
    expect(store.loadDrafts).toHaveBeenCalledTimes(2);

    await wrapper.setProps({ messages: [tool("fleet_mod_write", "completed", "p1"), tool("mcp__fleet__fleet_mod_write", "completed", "p2")] });
    await flushPromises();
    expect(store.loadDrafts).toHaveBeenCalledTimes(3);
  });
});
