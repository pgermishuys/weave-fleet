import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { Automation } from "@/stores/automations";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

import { useAutomationsStore } from "@/stores/automations";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function automation(id: string, overrides: Partial<Automation> = {}): Automation {
  return {
    id,
    name: `Automation ${id}`,
    prompt: "Summarise the open PRs",
    triggerType: "schedule",
    triggerConfig: "0 9 * * 1",
    maxConcurrentRuns: 1,
    maxRunsPerHour: 10,
    timeoutMinutes: 30,
    isEnabled: true,
    workspaceId: null,
    model: null,
    agent: null,
    createdAt: "2026-09-14T09:00:00Z",
    updatedAt: null,
    timeZone: "Africa/Johannesburg",
    ...overrides,
  };
}

/** A small stand-in for /api/automations that keeps its list between calls. */
function fakeServer(initial: Automation[]) {
  let list = [...initial];
  let refuseNextCreate: string | null = null;
  apiFetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    if (method === "GET" && url === "/api/automations") return json({ automations: list });
    if (method === "POST" && url === "/api/automations") {
      if (refuseNextCreate) {
        const error = refuseNextCreate;
        refuseNextCreate = null;
        return json({ error }, 400);
      }
      const body = JSON.parse(String(init?.body)) as Partial<Automation>;
      const created = automation(`a${list.length + 1}`, { ...body, isEnabled: true });
      list = [...list, created];
      return json(created, 201);
    }
    const toggle = url.match(/^\/api\/automations\/([^/]+)\/(enable|disable)$/);
    if (method === "POST" && toggle) {
      list = list.map((a) => (a.id === toggle[1] ? { ...a, isEnabled: toggle[2] === "enable" } : a));
      return new Response(null, { status: 204 });
    }
    return json({ error: "not faked" }, 404);
  });
  return {
    refuseNextCreate(message: string) {
      refuseNextCreate = message;
    },
    listCalls: () => apiFetchMock.mock.calls.filter(([url, init]) => url === "/api/automations" && !init?.method).length,
  };
}

describe("useAutomationsStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
  });

  it("lists a new automation as soon as it's created", async () => {
    fakeServer([automation("a1")]);
    const store = useAutomationsStore();
    await store.refresh();

    await store.createAutomation({ name: "Weekly digest", prompt: "Summarise", triggerType: "schedule", triggerConfig: "0 9 * * 1" });

    expect(store.automations.map((a) => a.name)).toEqual(["Automation a1", "Weekly digest"]);
  });

  it("shows a switch-off everywhere the list is shown", async () => {
    fakeServer([automation("a1")]);
    const store = useAutomationsStore();
    await store.refresh();

    await store.disableAutomation("a1");

    expect(store.automations[0]?.isEnabled).toBe(false);
  });

  it("shares one request between views that load at the same time", async () => {
    const server = fakeServer([automation("a1")]);
    const store = useAutomationsStore();

    await Promise.all([store.refresh(), store.refresh(), store.refresh()]);

    expect(server.listCalls()).toBe(1);
  });

  it("throws the server's reason when a create is refused", async () => {
    const server = fakeServer([]);
    server.refuseNextCreate("Invalid cron expression: bad format");
    const store = useAutomationsStore();

    await expect(
      store.createAutomation({ name: "Bad", prompt: "x", triggerType: "schedule", triggerConfig: "every monday" }),
    ).rejects.toThrow("Invalid cron expression: bad format");
  });

  it("stops showing the spinner after the first load and keeps it off for reloads", async () => {
    fakeServer([automation("a1")]);
    const store = useAutomationsStore();
    expect(store.isLoading).toBe(true);

    await store.refresh();
    const reload = store.refresh();

    expect(store.isLoading).toBe(false);
    await reload;
  });
});
