import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import MessageBubble from "@/components/session/MessageBubble.vue";

const { resolveSessionFilesMock } = vi.hoisted(() => ({ resolveSessionFilesMock: vi.fn() }));

vi.mock("@/api/session-files", () => ({ resolveSessionFiles: resolveSessionFilesMock }));

const FILES = new Map([
  ["src/billing/tax.ts", "src/billing/tax.ts"],
  ["/home/sam/dev/harbor-api/src/billing/invoice-service.ts", "src/billing/invoice-service.ts"],
]);

async function bubble(body: string) {
  const wrapper = mount(MessageBubble, {
    props: { author: "Agent", role: "assistant", showIdentity: true, clusterPosition: "single", body, sessionId: "s1" },
    attachTo: document.body,
  });
  await flushPromises();
  return wrapper;
}

describe("MessageBubble with files named in it", () => {
  beforeEach(() => {
    resolveSessionFilesMock.mockReset();
    resolveSessionFilesMock.mockImplementation(async (_machine: unknown, _sessionId: string, paths: string[]) =>
      new Map(paths.filter((path) => FILES.has(path)).map((path) => [path, FILES.get(path)!])));
  });

  it("links the paths that are files in the session's folder, and leaves the rest as code", async () => {
    const wrapper = await bubble("Rounding is in `src/billing/tax.ts:13`; `src/billing/legacy-rates.ts` is gone.");

    expect(resolveSessionFilesMock).toHaveBeenCalledWith(expect.anything(), "s1", ["src/billing/tax.ts", "src/billing/legacy-rates.ts"]);
    const links = wrapper.findAll(".file-ref");
    expect(links).toHaveLength(1);
    expect(links[0].text()).toBe("src/billing/tax.ts:13");
    expect(links[0].attributes("title")).toBe("Open src/billing/tax.ts at line 13 · Ctrl-click keeps the tab");
    wrapper.unmount();
  });

  it("opens a preview tab at the line on a click, and keeps the tab on a Ctrl-click", async () => {
    const wrapper = await bubble("Changed `/home/sam/dev/harbor-api/src/billing/invoice-service.ts:17` and `src/billing/tax.ts`.");
    const [service, tax] = wrapper.findAll(".file-ref");

    await service.trigger("click");
    await tax.trigger("click", { ctrlKey: true });

    expect(wrapper.emitted("open-file")).toEqual([
      ["src/billing/invoice-service.ts", 17, false],
      ["src/billing/tax.ts", undefined, true],
    ]);
    wrapper.unmount();
  });

  it("opens from the keyboard with Enter", async () => {
    const wrapper = await bubble("See `src/billing/tax.ts:13`.");

    await wrapper.get(".file-ref").trigger("keydown", { key: "Enter" });

    expect(wrapper.emitted("open-file")).toEqual([["src/billing/tax.ts", 13, false]]);
    wrapper.unmount();
  });

  it("doesn't open when the click ended a drag that selected text", async () => {
    const wrapper = await bubble("See `src/billing/tax.ts:13`.");
    const link = wrapper.get(".file-ref");
    window.getSelection()?.selectAllChildren(link.element);

    await link.trigger("click");

    expect(wrapper.emitted("open-file")).toBeUndefined();
    window.getSelection()?.removeAllRanges();
    wrapper.unmount();
  });
});
