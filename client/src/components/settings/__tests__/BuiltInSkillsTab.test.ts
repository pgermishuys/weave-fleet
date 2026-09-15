import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

import BuiltInSkillsTab from "@/components/settings/skills/BuiltInSkillsTab.vue";

const skills = [
  { name: "fleet-code-review", description: "Review a change for bugs.", enabled: false },
  { name: "fleet-run", description: "Run the app.", enabled: true },
];

function respond(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }));
}

function switchFor(name: string): string {
  return `button[role="switch"][aria-label="Use ${name} in new sessions"]`;
}

describe("BuiltInSkillsTab", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
  });

  it("lists Fleet's skills with whether each is on", async () => {
    apiFetchMock.mockImplementation(() => respond(skills));

    const wrapper = mount(BuiltInSkillsTab);
    await flushPromises();

    expect(apiFetchMock).toHaveBeenCalledWith("/api/skills/built-in");
    expect(wrapper.text()).toContain("fleet-code-review");
    expect(wrapper.text()).toContain("Review a change for bugs.");
    expect(wrapper.get(switchFor("fleet-code-review")).attributes("aria-checked")).toBe("false");
    expect(wrapper.get(switchFor("fleet-run")).attributes("aria-checked")).toBe("true");
  });

  it("turns a skill on through the API and shows the server's answer", async () => {
    let finishSave: (response: Response) => void = () => {};
    apiFetchMock.mockImplementation((_path: string, init?: RequestInit) =>
      init?.method === "PUT"
        ? new Promise<Response>((resolve) => { finishSave = resolve; })
        : respond(skills));
    const wrapper = mount(BuiltInSkillsTab);
    await flushPromises();

    await wrapper.get(switchFor("fleet-code-review")).trigger("click");

    const [path, init] = apiFetchMock.mock.calls[1] as [string, RequestInit];
    expect(path).toBe("/api/skills/built-in/fleet-code-review");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toEqual({ enabled: true });
    // One change at a time: the choice is one preference on the server.
    expect(wrapper.get(switchFor("fleet-run")).attributes("disabled")).toBeDefined();

    finishSave(new Response(JSON.stringify({ ...skills[0], enabled: true }), { status: 200 }));
    await flushPromises();

    expect(wrapper.get(switchFor("fleet-code-review")).attributes("aria-checked")).toBe("true");
    expect(wrapper.get(switchFor("fleet-run")).attributes("disabled")).toBeUndefined();
  });

  it("shows why a change failed and keeps the switch where it was", async () => {
    apiFetchMock.mockImplementation((_path: string, init?: RequestInit) =>
      init?.method === "PUT"
        ? respond({ error: "BuiltInSkill with id 'fleet-code-review' was not found." }, 404)
        : respond(skills));
    const wrapper = mount(BuiltInSkillsTab);
    await flushPromises();

    await wrapper.get(switchFor("fleet-code-review")).trigger("click");
    await flushPromises();

    expect(wrapper.get('[role="alert"]').text()).toBe("BuiltInSkill with id 'fleet-code-review' was not found.");
    expect(wrapper.get(switchFor("fleet-code-review")).attributes("aria-checked")).toBe("false");
  });
});
