import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MachineConnection } from "@/lib/machines";

const fetchOnMachine = vi.fn();
let active: MachineConnection | null = null;
vi.mock("@/lib/machines", () => ({
  fetchOnMachine: (...args: unknown[]) => fetchOnMachine(...args),
}));
// The session's machine: the one the page provides, live or opened in place.
vi.mock("@/lib/machine-target", () => ({
  useMachineTarget: () => ({ key: active?.id ?? "home", connection: active, isLive: active === null, api: {} }),
}));

import { useMachineWebApp } from "../phone/use-machine-web-app";

const atlas: MachineConnection = { id: "m-atlas", name: "atlas", baseUrl: "http://127.0.0.2:5572", token: "t", addedAt: "" };

async function setup() {
  let webApp!: ReturnType<typeof useMachineWebApp>;
  mount(defineComponent({ setup: () => { webApp = useMachineWebApp(); return () => h("div"); } }));
  await flushPromises();
  return webApp;
}

function answer(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("useMachineWebApp", () => {
  beforeEach(() => {
    fetchOnMachine.mockReset();
    active = atlas;
  });

  it("is false for a machine that says it has no web app", async () => {
    fetchOnMachine.mockResolvedValue(answer({ id: "m-atlas", webApp: false }));

    expect((await setup()).value).toBe(false);
    expect(fetchOnMachine).toHaveBeenCalledWith(atlas, "/api/machine");
  });

  it("is true for a machine that says it has one", async () => {
    fetchOnMachine.mockResolvedValue(answer({ id: "m-atlas", webApp: true }));

    expect((await setup()).value).toBe(true);
  });

  it("is true for a Fleet too old to say", async () => {
    fetchOnMachine.mockResolvedValue(answer({ id: "m-atlas" }));

    expect((await setup()).value).toBe(true);
  });

  it("stays true when the machine doesn't answer", async () => {
    fetchOnMachine.mockRejectedValueOnce(new TypeError("offline"));
    expect((await setup()).value).toBe(true);

    fetchOnMachine.mockResolvedValueOnce(answer({ error: "nope" }, 401));
    expect((await setup()).value).toBe(true);
  });

  it("doesn't ask home, which serves this page", async () => {
    active = null;

    expect((await setup()).value).toBe(true);
    expect(fetchOnMachine).not.toHaveBeenCalled();
  });
});
